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
    private const int UnblockConnectionTimeoutMilliseconds = 1000;

    // Guards the two-step "observe the stop, then claim the connection wait" so that a stop can never land
    // in between and leave the reader blocked on an accept with nothing left to wake it.
    private readonly object _connectionLock = new();
    private readonly CancellationTokenRegistration _cancellationRegistration;

    private bool _stopListening;
    private bool _waitingForConnection;

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
        bool unblock;
        lock (_connectionLock)
        {
            if (_stopListening)
            {
                return;
            }

            _stopListening = true;

            // Only a reader parked in WaitForConnection has to be woken. One that is still draining a
            // client will observe the flag itself when it comes back for the next connection.
            unblock = _waitingForConnection;
        }

        if (unblock)
        {
            UnblockConnectionWait();
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _cancellationRegistration.Dispose();
        StopListening();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override void Connect() => WaitForNextConnection();

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
        return new NamedPipeServerStream(pipeName, PipeDirection.In, maximumServerInstances);
    }

    /// <summary>
    /// Waits for the next client, unless the server has been told to stop listening.
    /// </summary>
    /// <returns><see langword="true"/> if a client connected and should be read.</returns>
    private bool WaitForNextConnection()
    {
        lock (_connectionLock)
        {
            if (_stopListening)
            {
                return false;
            }

            _waitingForConnection = true;
        }

        try
        {
            PipeStream.WaitForConnection();

            // Deliberately not re-checking the stop flag: a client that connected before the stop still
            // has events to hand over, and the connection opened to unblock this wait sends nothing, so
            // draining it simply returns and the loop then observes the flag.
            return true;
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
            // Unix named pipes can surface disposal of WaitForConnection as a socket error.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            lock (_connectionLock)
            {
                _waitingForConnection = false;
            }
        }
    }

    private void UnblockConnectionWait()
    {
        try
        {
            // Connecting a dummy client is what stops WaitForConnection. Checking IsConnected is not
            // reliable here because a quick connect/disconnect may never be observed as connected.
            using (var pipeStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                pipeStream.Connect(UnblockConnectionTimeoutMilliseconds);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        catch (TimeoutException)
        {
        }
    }
}
