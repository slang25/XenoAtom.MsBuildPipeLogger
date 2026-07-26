// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;
using System.Net.Sockets;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// A server for receiving MSBuild logging events over a named pipe.
/// </summary>
/// <remarks>
/// The server accepts client connections sequentially and stays open between them because MSBuild connects
/// one client per build submission: <c>dotnet build -f &lt;tfm&gt;</c>, for instance, runs a restore submission
/// followed by a build submission. Call <see cref="PipeLoggerServer{TPipeStream}.StopAcceptingConnections"/>
/// once the observed process has exited so that <see cref="PipeLoggerServer{TPipeStream}.ReadAll"/> returns.
/// </remarks>
public class NamedPipeLoggerServer : PipeLoggerServer<NamedPipeServerStream>
{
    private const int CancelConnectionTimeoutMilliseconds = 1000;

    private readonly CancellationTokenRegistration _cancellationRegistration;
    private int _connectionWaitCanceled;

    /// <summary>
    /// Gets the named pipe name.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is too long for this platform. See <see cref="PipeLoggerServer.CreateUniquePipeName()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName)
        : this(pipeName, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty or whitespace, or is too long for this platform. See <see cref="PipeLoggerServer.CreateUniquePipeName()"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName, CancellationToken cancellationToken)
        : base(CreatePipe(pipeName), cancellationToken, false)
    {
        PipeName = pipeName;
        StartReading();
        _cancellationRegistration = CancellationToken.Register(CancelConnectionWait);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _cancellationRegistration.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override void Connect() => PipeStream.WaitForConnection();

    /// <inheritdoc/>
    protected override bool TryDisconnect()
    {
        try
        {
            PipeStream.Disconnect();
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        if (pipeName is null)
        {
            throw new ArgumentNullException(nameof(pipeName));
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("The pipe name cannot be empty or whitespace.", nameof(pipeName));
        }

        try
        {
            // MSBuild connects one client per build submission, so a single instance is never enough:
            // a second client would sit unserved in the listener backlog and block the build it logs.
            return new NamedPipeServerStream(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances);
        }
        catch (ArgumentOutOfRangeException exception) when (exception.ParamName == "path")
        {
            // On Unix a named pipe is a domain socket whose whole path is length limited, which makes an
            // otherwise reasonable pipe name fail with an error that does not mention the pipe name.
            throw new ArgumentException(
                $"The pipe name '{pipeName}' is too long for this platform. Named pipe names are limited to " +
                $"{PipeLoggerServer.GetMaximumPipeNameLength()} characters here; use {nameof(PipeLoggerServer)}.{nameof(PipeLoggerServer.CreateUniquePipeName)}() to generate a name that fits.",
                nameof(pipeName),
                exception);
        }
    }

    /// <inheritdoc/>
    protected override void CancelConnectionWait()
    {
        if (Interlocked.Exchange(ref _connectionWaitCanceled, 1) != 0)
        {
            return;
        }

        // Connecting the dummy client can block for the whole connect timeout while a real client still
        // holds the pipe, so it runs on its own thread rather than stalling the caller.
        var connectionCancelThread = new Thread(ConnectCancelClient)
        {
            IsBackground = true,
            Name = "MSBuild pipe logger connection canceler"
        };
        connectionCancelThread.Start();
    }

    private void ConnectCancelClient()
    {
        // The connection has to be made, not merely attempted: the server treats it as the marker that
        // every client queued ahead of it has been served. A platform that refuses the connection while a
        // real client still holds the pipe is retried until the reader is done with the transport.
        while (IsReading)
        {
            try
            {
                // This stops WaitForConnection by connecting a dummy client. Checking IsConnected is not
                // reliable here because a quick connect/disconnect may never be observed as connected.
                using (var pipeStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipeStream.Connect(CancelConnectionTimeoutMilliseconds);
                }

                return;
            }
            catch (TimeoutException)
            {
                // The server is still busy with a real client.
            }
            catch (IOException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }
}