// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Receives MSBuild logging events over a pipe. This is the base class for <see cref="AnonymousPipeLoggerServer"/>
/// and <see cref="NamedPipeLoggerServer"/>.
/// </summary>
/// <typeparam name="TPipeStream">The concrete pipe stream type.</typeparam>
public abstract class PipeLoggerServer<TPipeStream> : EventArgsDispatcher, IPipeLoggerServer
    where TPipeStream : PipeStream
{
    private static readonly TimeSpan ReaderShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly int _fileFormatVersion;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly object _readLock = new();
    private readonly Thread _readerThread;
    private BinaryReader _binaryReader;
    private BuildEventArgsReader _buildEventArgsReader;
    private int _disposed;
    private int _listeningStopped;
    private int _pipeShutdownRequested;
    private int _started;

    internal PipeBuffer Buffer { get; } = new();

    /// <summary>
    /// Gets the pipe stream read by this server.
    /// </summary>
    protected TPipeStream PipeStream { get; }

    /// <summary>
    /// Gets the token used to cancel read operations.
    /// </summary>
    protected CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets a value indicating whether the server has been disposed or its cancellation token was triggered.
    /// </summary>
    protected bool IsShutdownRequested => Volatile.Read(ref _disposed) != 0 || CancellationToken.IsCancellationRequested;

    /// <summary>
    /// Gets a value indicating whether <see cref="StopListening"/> was called.
    /// </summary>
    protected bool IsListeningStopped => Volatile.Read(ref _listeningStopped) != 0;

    /// <summary>
    /// Creates a server that receives MSBuild events over a specified pipe.
    /// </summary>
    /// <param name="pipeStream">The pipe to receive events from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeStream"/> is <see langword="null"/>.</exception>
    protected PipeLoggerServer(TPipeStream pipeStream)
        : this(pipeStream, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a server that receives MSBuild events over a specified pipe.
    /// </summary>
    /// <param name="pipeStream">The pipe to receive events from.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeStream"/> is <see langword="null"/>.</exception>
    protected PipeLoggerServer(TPipeStream pipeStream, CancellationToken cancellationToken)
        : this(pipeStream, cancellationToken, true)
    {
    }

    /// <summary>
    /// Creates a server that receives MSBuild events over a specified pipe.
    /// </summary>
    /// <param name="pipeStream">The pipe to receive events from.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <param name="autoStart">A value indicating whether the background reader should start immediately.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipeStream"/> is <see langword="null"/>.</exception>
    protected PipeLoggerServer(TPipeStream pipeStream, CancellationToken cancellationToken, bool autoStart)
    {
        PipeStream = pipeStream ?? throw new ArgumentNullException(nameof(pipeStream));
        _fileFormatVersion = GetBinaryLoggerFileFormatVersion();
        _binaryReader = CreateBinaryReader();
        _buildEventArgsReader = new BuildEventArgsReader(_binaryReader, _fileFormatVersion);
        CancellationToken = cancellationToken;
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(static state =>
                ((PipeLoggerServer<TPipeStream>)state!).RequestPipeShutdown(), this);
        }

        _readerThread = new Thread(ReadFromTransport)
        {
            IsBackground = true,
            Name = "MSBuild pipe logger reader"
        };

        if (autoStart)
        {
            StartReading();
        }
    }

    /// <summary>
    /// Connects the server-side pipe stream to a client.
    /// </summary>
    protected abstract void Connect();

    /// <summary>
    /// Waits for the next client after the current client disconnected. Implementations that support
    /// more than one connection must not tear the listener down, otherwise a client can arrive while
    /// nothing is listening.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if another client connected and its events should be read; otherwise
    /// <see langword="false"/> to stop reading. The base implementation always returns <see langword="false"/>.
    /// </returns>
    protected virtual bool Reconnect() => false;

    /// <summary>
    /// Unblocks a pending wait for the next client so that the reader can finish. The base
    /// implementation does nothing.
    /// </summary>
    protected virtual void StopAcceptingConnections()
    {
    }

    /// <summary>
    /// Starts the background reader thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The reader thread was already started.</exception>
    protected void StartReading()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The logger server has already been started.");
        }

        _readerThread.Start();
    }

    private void ReadFromTransport()
    {
        try
        {
            Connect();
            while (true)
            {
                while (Buffer.FillFromStream(PipeStream, CancellationToken))
                {
                }

                if (!Reconnect())
                {
                    break;
                }

                // Queued only once the next client is connected, which keeps it ordered ahead of that
                // client's bytes and tells the consumer to reset its event reader first.
                Buffer.MarkConnectionBoundary();
            }
        }
        catch (IOException)
        {
            // The client broke the stream so we're done.
        }
        catch (ObjectDisposedException)
        {
            // The pipe was disposed.
        }
        catch (OperationCanceledException)
        {
            // The operation was canceled.
        }
        catch (SocketException) when (CancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            // Unix named pipes can surface cancellation/disposal of WaitForConnection as a socket error.
        }
        catch (InvalidOperationException)
        {
            // The pipe reached an invalid state while shutting down.
        }
        finally
        {
            // Add a final 0 (BinaryLogRecordKind.EndOfFile) into the stream in case the BuildEventArgsReader is waiting for a read.
            Buffer.TryWriteEndOfFile();
            Buffer.CompleteAdding();
        }
    }

    private static int GetBinaryLoggerFileFormatVersion()
    {
        var fileFormatVersionField = typeof(BinaryLogger).GetField(
                                         "FileFormatVersion",
                                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                     ?? throw new MissingFieldException(typeof(BinaryLogger).FullName ?? typeof(BinaryLogger).Name, "FileFormatVersion");

        var fileFormatVersion = fileFormatVersionField.GetValue(null);
        if (fileFormatVersion is not int version)
        {
            throw new InvalidOperationException(
                $"Field '{typeof(BinaryLogger).FullName}.FileFormatVersion' must be an integer.");
        }

        return version;
    }

    /// <inheritdoc/>
    public BuildEventArgs? Read()
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0 || Buffer.IsCompleted)
            {
                return null;
            }

            lock (_readLock)
            {
                try
                {
                    var args = _buildEventArgsReader.Read();
                    if (args is not null)
                    {
                        Dispatch(args);
                        return args;
                    }
                }
                catch (EndOfStreamException)
                {
                    // The stream may have been closed or otherwise stopped, or the current client
                    // disconnected. TryConsumeConnectionBoundary below tells the two apart.
                }
                catch (ObjectDisposedException)
                {
                    // The server was disposed while reading.
                }

                if (Volatile.Read(ref _disposed) != 0 || !Buffer.TryConsumeConnectionBoundary())
                {
                    return null;
                }

                // Every client writes with its own event writer, and that writer restarts the string
                // table it emits indexes into. Carrying the reader across a connection would silently
                // resolve the next client's indexes against the previous client's strings, so the
                // reader is rebuilt for each connection.
                ResetEventArgsReader();
            }
        }
    }

    /// <inheritdoc/>
    public void ReadAll()
    {
        while (Read() is not null)
        {
        }
    }

    /// <inheritdoc/>
    public void StopListening()
    {
        if (Interlocked.Exchange(ref _listeningStopped, 1) != 0)
        {
            return;
        }

        StopAcceptingConnections();
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellationRegistration.Dispose();
        RequestPipeShutdown();

        if (Volatile.Read(ref _started) != 0 && Thread.CurrentThread.ManagedThreadId != _readerThread.ManagedThreadId)
        {
            _readerThread.Join(ReaderShutdownTimeout);
        }

        lock (_readLock)
        {
            _buildEventArgsReader.Dispose();
            _binaryReader.Dispose();
            Buffer.Dispose();
        }
    }

    private BinaryReader CreateBinaryReader() =>
        // The buffer outlives the reader because a new reader is created for each connection, so the
        // reader must not close it.
        new(Buffer, Encoding.UTF8, leaveOpen: true);

    private void ResetEventArgsReader()
    {
        _buildEventArgsReader.Dispose();
        _binaryReader.Dispose();
        _binaryReader = CreateBinaryReader();
        _buildEventArgsReader = new BuildEventArgsReader(_binaryReader, _fileFormatVersion);
    }

    private void RequestPipeShutdown()
    {
        // Unblock readers immediately. Disposing PipeStream can block on some Unix pipe/socket
        // states, and CancellationToken.Cancel() runs callbacks synchronously, so the transport
        // dispose is intentionally moved to a dedicated background thread.
        Buffer.TryWriteEndOfFile();
        Buffer.CompleteAdding();

        if (Interlocked.Exchange(ref _pipeShutdownRequested, 1) != 0)
        {
            return;
        }

        var pipeDisposeThread = new Thread(DisposePipeStream)
        {
            IsBackground = true,
            Name = "MSBuild pipe logger transport disposer"
        };
        pipeDisposeThread.Start();
    }

    private void DisposePipeStream()
    {
        try
        {
            PipeStream.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
