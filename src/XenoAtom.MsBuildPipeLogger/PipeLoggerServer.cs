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
    private BuildEventArgsReader? _buildEventArgsReader;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly object _readLock = new();
    private readonly Thread _readerThread;
    private int _disposed;
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
        // The BuildEventArgsReader is created lazily on the first read, once the logger's
        // format-version header has been received (see EnsureReaderInitialized).
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
            while (Buffer.FillFromStream(PipeStream, CancellationToken))
            {
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
        if (Volatile.Read(ref _disposed) != 0 || Buffer.IsCompleted)
        {
            return null;
        }

        try
        {
            lock (_readLock)
            {
                var reader = EnsureReaderInitialized();
                if (reader is null)
                {
                    return null;
                }

                var args = reader.Read();
                if (args is not null)
                {
                    Dispatch(args);
                    return args;
                }
            }
        }
        catch (EndOfStreamException)
        {
            // The stream may have been closed or otherwise stopped.
        }
        catch (ObjectDisposedException)
        {
            // The server was disposed while reading.
        }

        return null;
    }

    /// <summary>
    /// Creates the <see cref="BuildEventArgsReader"/> on first use, reading the binary-log format
    /// version the logger wrote as a header so events are deserialized with the version they were
    /// written in rather than this process's MSBuild version.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The observed build used a newer binary-log format than the MSBuild loaded in this process can read.
    /// </exception>
    private BuildEventArgsReader? EnsureReaderInitialized()
    {
        if (_buildEventArgsReader is not null)
        {
            return _buildEventArgsReader;
        }

        // Blocks until the 4-byte header arrives; throws EndOfStreamException if the stream ends
        // first (e.g. a build that produced no events), which the caller treats as end-of-stream.
        var hostFormatVersion = _binaryReader.ReadInt32();
        var consumerFormatVersion = GetBinaryLoggerFileFormatVersion();
        if (hostFormatVersion > consumerFormatVersion)
        {
            throw new NotSupportedException(
                $"The observed build was produced with MSBuild binary log format version {hostFormatVersion}, " +
                $"but the MSBuild loaded in this process only reads up to version {consumerFormatVersion}. " +
                "Load a newer Microsoft.Build in the consuming process (for example via Microsoft.Build.Locator).");
        }

        _buildEventArgsReader = new BuildEventArgsReader(_binaryReader, hostFormatVersion);
        return _buildEventArgsReader;
    }

    /// <inheritdoc/>
    public void ReadAll()
    {
        var args = Read();
        while (args is not null)
        {
            if (args is BuildFinishedEventArgs)
            {
                return;
            }

            args = Read();
        }
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
            _buildEventArgsReader?.Dispose();
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
