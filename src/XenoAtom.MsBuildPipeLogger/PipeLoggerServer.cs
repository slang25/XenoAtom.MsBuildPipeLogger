// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
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

    private readonly BinaryReader _binaryReader;
    private readonly int _fileFormatVersion;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly object _readLock = new();
    private readonly Thread _readerThread;
    private BuildEventArgsReader _buildEventArgsReader;
    private int _disposed;
    private int _pipeShutdownRequested;
    private int _started;
    private int _stoppedAcceptingConnections;

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
        _binaryReader = new BinaryReader(Buffer);
        _fileFormatVersion = GetBinaryLoggerFileFormatVersion();
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
    /// Disconnects the client that just closed the transport so that <see cref="Connect"/> can accept
    /// the next one. Transports that only ever serve a single client return <see langword="false"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the server can wait for another client; otherwise <see langword="false"/>.</returns>
    protected virtual bool TryDisconnect() => false;

    /// <summary>
    /// Unblocks a pending <see cref="Connect"/> when the server is no longer interested in new clients.
    /// </summary>
    protected virtual void CancelConnectionWait()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the background reader is still serving the transport.
    /// </summary>
    protected bool IsReading => Volatile.Read(ref _started) != 0 && _readerThread.IsAlive;

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
            while (true)
            {
                Connect();
                var receivedData = false;
                while (Buffer.FillFromStream(PipeStream, CancellationToken))
                {
                    receivedData = true;
                }

                if (!ShouldAcceptAnotherConnection(receivedData) || !TryDisconnect())
                {
                    break;
                }

                // Every connection carries its own independent binary log record stream, so the
                // boundary has to reach the reader before the next client's bytes do.
                Buffer.EndConnection();
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

    private bool ShouldAcceptAnotherConnection(bool receivedData)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _pipeShutdownRequested) != 0
            || CancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (Volatile.Read(ref _stoppedAcceptingConnections) == 0)
        {
            return true;
        }

        // Stopping cannot simply drop out here: a client that connected before the stop may still be
        // waiting to be accepted, and its events would be lost. The connection opened to unblock Connect()
        // is queued behind every such client and sends nothing, so an empty connection is what marks the
        // point where they have all been drained.
        return receivedData;
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
        if (Volatile.Read(ref _disposed) != 0 || Buffer.IsCompleted)
        {
            return null;
        }

        try
        {
            lock (_readLock)
            {
                while (true)
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
                        // The stream may have been closed or otherwise stopped.
                    }

                    // A client disconnecting ends its record stream but not necessarily the transport:
                    // MSBuild connects a new client per build submission. Each one restarts the binary
                    // log string tables, so the events reader has to be restarted along with them.
                    if (!Buffer.TryStartNextConnection())
                    {
                        return null;
                    }

                    _buildEventArgsReader.Dispose();
                    _buildEventArgsReader = new BuildEventArgsReader(_binaryReader, _fileFormatVersion);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The server was disposed while reading.
        }

        return null;
    }

    /// <inheritdoc/>
    public void ReadAll()
    {
        while (Read() is not null)
        {
        }
    }

    /// <inheritdoc/>
    public void StopAcceptingConnections()
    {
        Volatile.Write(ref _stoppedAcceptingConnections, 1);
        CancelConnectionWait();
    }

    /// <inheritdoc/>
    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellationRegistration.Dispose();

        // The reader thread may be waiting for the next client, which no pipe shutdown reliably unblocks.
        StopAcceptingConnections();
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
