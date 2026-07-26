// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// A server for receiving MSBuild logging events over a named pipe.
/// </summary>
/// <remarks>
/// MSBuild attaches a logger once per build submission, so one child process can open several sequential
/// connections — <c>dotnet build</c> runs restore and build as separate submissions. By default this server
/// keeps accepting connections so every submission is observed, which means a read only ends once
/// <see cref="StopListening"/> is called. Pass <c>acceptMultipleConnections: false</c> when the caller
/// guarantees a single submission and wants the read to end when the client disconnects.
/// </remarks>
public class NamedPipeLoggerServer : PipeLoggerServer<NamedPipeServerStream>
{
    // How long to wait to establish that no more clients are queued once a stop has been requested. A
    // client that has already connected is accepted immediately, so this only has to cover scheduling the
    // completion, not waiting for anyone to arrive.
    private static readonly TimeSpan BacklogDrainTimeout = TimeSpan.FromMilliseconds(250);

    // Cancelling the accept is the entire stop mechanism. A CancellationToken already makes "observe the
    // stop" and "claim the wait" one atomic step: an accept started with a cancelled token never blocks,
    // and one already blocked is cancelled. So there is no window in which a stop goes unnoticed and no
    // need to wake the reader by connecting to ourselves.
    private readonly CancellationTokenSource _stopListeningSource = new();
    private readonly CancellationTokenRegistration _cancellationRegistration;

    /// <summary>
    /// Gets the named pipe name.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Gets a value indicating whether the server keeps accepting connections after a client disconnects.
    /// </summary>
    public bool AcceptsMultipleConnections { get; }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events from every build submission.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is longer
    /// than <see cref="PipeLoggerServer.GetMaximumPipeNameLength()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName)
        : this(pipeName, true, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events from every build submission.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is longer
    /// than <see cref="PipeLoggerServer.GetMaximumPipeNameLength()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName, CancellationToken cancellationToken)
        : this(pipeName, true, cancellationToken)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="acceptMultipleConnections">
    /// <see langword="true"/> to keep accepting connections so that every MSBuild build submission is
    /// observed, in which case a read ends only once <see cref="StopListening"/> is called;
    /// <see langword="false"/> to serve a single connection and end the read when that client disconnects.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is longer
    /// than <see cref="PipeLoggerServer.GetMaximumPipeNameLength()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName, bool acceptMultipleConnections)
        : this(pipeName, acceptMultipleConnections, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="acceptMultipleConnections">
    /// <see langword="true"/> to keep accepting connections so that every MSBuild build submission is
    /// observed, in which case a read ends only once <see cref="StopListening"/> is called;
    /// <see langword="false"/> to serve a single connection and end the read when that client disconnects.
    /// </param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is longer
    /// than <see cref="PipeLoggerServer.GetMaximumPipeNameLength()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName, bool acceptMultipleConnections, CancellationToken cancellationToken)
        : base(CreatePipe(pipeName, acceptMultipleConnections), cancellationToken, false)
    {
        PipeName = pipeName;
        AcceptsMultipleConnections = acceptMultipleConnections;
        StartReading();
        _cancellationRegistration = CancellationToken.Register(StopListening);
    }

    /// <inheritdoc/>
    public override void StopListening()
    {
        try
        {
            // Cancelling is idempotent, and a reader that is mid-drain finishes handing over that client's
            // events before it comes back to the accept and sees the cancellation.
            _stopListeningSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, so the reader has been stopped by other means.
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _cancellationRegistration.Dispose();
        StopListening();
        base.Dispose();

        // Only after the base has joined the reader thread, so nothing is still waiting on the token.
        _stopListeningSource.Dispose();
    }

    /// <inheritdoc/>
    protected override bool Connect() => WaitForNextConnection();

    /// <inheritdoc/>
    protected override bool TryAcceptNextConnection()
    {
        if (!AcceptsMultipleConnections)
        {
            return false;
        }

        // Re-accept on the same listener rather than creating a new one: re-creating it would leave a
        // window with nothing listening, and the next submission's connect would race the operating
        // system releasing the previous instance ("All pipe instances are busy").
        try
        {
            PipeStream.Disconnect();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Never connected, so there is nothing to disconnect. Note ObjectDisposedException derives
            // from this and is handled above.
        }

        return WaitForNextConnection();
    }

    private static NamedPipeServerStream CreatePipe(string pipeName, bool acceptMultipleConnections)
    {
        if (pipeName is null)
        {
            throw new ArgumentNullException(nameof(pipeName));
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("The pipe name cannot be empty or whitespace.", nameof(pipeName));
        }

        // On Unix the name becomes part of a domain socket path, and exceeding the platform limit
        // otherwise surfaces as an ArgumentOutOfRangeException about a 'path' the caller never supplied.
        var maximumLength = PipeLoggerServer.GetMaximumPipeNameLength();
        if (Encoding.UTF8.GetByteCount(pipeName) > maximumLength)
        {
            throw new ArgumentException(
                $"The pipe name '{pipeName}' is too long: this platform allows at most {maximumLength} characters " +
                $"because the name becomes part of a socket path under '{Path.GetTempPath()}'. Use " +
                $"{nameof(PipeLoggerServer)}.{nameof(PipeLoggerServer.CreateUniquePipeName)}() to get a name that fits.",
                nameof(pipeName));
        }

        // On Unix the instance count doubles as the listen backlog, so a submission that connects while the
        // previous one is still being drained needs room to queue instead of being refused.
        var maximumServerInstances = acceptMultipleConnections ? NamedPipeServerStream.MaxAllowedServerInstances : 1;

        // Overlapped, because both the accept and every read block a dedicated thread on an async call to
        // get a CancellationToken. On a non-overlapped Windows handle those calls are emulated by running
        // the blocking call on a pool thread, which parks a second thread per operation and, before .NET
        // Core, could not cancel one already in flight — the stop would be silently ignored.
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    /// <summary>
    /// Waits for the next client, unless the server has been told to stop listening.
    /// </summary>
    /// <returns><see langword="true"/> if a client connected and should be read.</returns>
    private bool WaitForNextConnection()
    {
        // A stop that arrives before we reach the accept must not discard clients that have already
        // connected. The build process can finish writing a submission and exit while the reader is still
        // busy, leaving that connection queued, and an accept handed an already-cancelled token returns
        // without taking anything off the queue. Mop the queue up before finishing.
        if (_stopListeningSource.IsCancellationRequested)
        {
            return TryAcceptQueuedConnection();
        }

        try
        {
            // Blocking the dedicated reader thread on the async accept is what lets the stop cancel it.
            // A client that connects before the cancellation is still accepted and drained in full.
            PipeStream.WaitForConnectionAsync(_stopListeningSource.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Being cancelled here does not mean nothing was queued. A client can connect while we are
            // blocked and have the cancellation win the race to complete the accept, which loses that whole
            // submission, so the queue has to be checked either way.
            return TryAcceptQueuedConnection();
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SocketException)
        {
            // Unix named pipes can surface disposal of the accept as a socket error.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Accepts a client that connected before the stop was requested, if one is still queued.
    /// </summary>
    /// <remarks>
    /// Accepting is immediate when the queue is not empty, so the timeout is only how long we are willing
    /// to wait to establish that it <em>is</em> empty. It bounds the end of a read rather than deciding
    /// whether a connected client is served.
    /// </remarks>
    private bool TryAcceptQueuedConnection()
    {
        using (var drainTimeout = new CancellationTokenSource(BacklogDrainTimeout))
        {
            try
            {
                PipeStream.WaitForConnectionAsync(drainTimeout.Token).GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                // Nothing left queued.
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
