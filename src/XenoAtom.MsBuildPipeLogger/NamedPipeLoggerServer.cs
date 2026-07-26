// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// A server for receiving MSBuild logging events over a named pipe.
/// </summary>
public class NamedPipeLoggerServer : PipeLoggerServer<NamedPipeServerStream>
{
    private const int CancelConnectionTimeoutMilliseconds = 1000;

    /// <summary>
    /// The maximum length of the full <c>\\.\pipe\&lt;name&gt;</c> path on Windows.
    /// </summary>
    private const int WindowsMaxPipePathLength = 256;

    private const string WindowsPipePathPrefix = @"\\.\pipe\";

    /// <summary>
    /// The size of <c>sockaddr_un.sun_path</c>, one byte of which is the terminator. It is 104 on
    /// macOS and 108 on Linux; the smaller value is used so a name that is valid on one Unix platform
    /// is valid on the other.
    /// </summary>
    private const int UnixSocketPathBufferLength = 104;

    /// <summary>
    /// The prefix .NET puts in front of a named pipe when it maps it onto a Unix domain socket.
    /// </summary>
    private const string UnixPipeFilePrefix = "CoreFxPipe_";

    /// <summary>
    /// The length of the base64 encoding of a 16-byte GUID, excluding the two padding characters.
    /// </summary>
    private const int UniqueSuffixLength = 22;

    private static readonly int MaxPipeNameLengthValue = CalculateMaxPipeNameLength();

    private readonly InterlockedBool _connected = new(false);
    private readonly CancellationTokenRegistration _cancellationRegistration;

    /// <summary>
    /// Gets the named pipe name.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Gets a value indicating whether the server keeps listening after a client disconnects.
    /// </summary>
    public bool AcceptsMultipleConnections { get; }

    /// <summary>
    /// Gets the longest pipe name that can be used on the current platform.
    /// </summary>
    /// <remarks>
    /// On Unix a named pipe is a domain socket under the temporary directory, and the whole socket
    /// path is capped by the operating system. Because macOS uses a long per-user temporary
    /// directory, the space left for the name itself can be as little as about 40 characters.
    /// </remarks>
    public static int MaxPipeNameLength => MaxPipeNameLengthValue;

    /// <summary>
    /// Creates a unique pipe name that is guaranteed to fit within <see cref="MaxPipeNameLength"/>.
    /// </summary>
    /// <returns>A unique pipe name.</returns>
    public static string CreatePipeName() => CreatePipeName("mbpl-");

    /// <summary>
    /// Creates a unique pipe name with the specified prefix.
    /// </summary>
    /// <param name="prefix">A prefix that makes the pipe recognizable, for example in <c>lsof</c> output.</param>
    /// <returns>A unique pipe name starting with <paramref name="prefix"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="prefix"/> is too long to be combined with a unique suffix without exceeding
    /// <see cref="MaxPipeNameLength"/>.
    /// </exception>
    public static string CreatePipeName(string prefix)
    {
        if (prefix is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        var maxPrefixLength = MaxPipeNameLength - UniqueSuffixLength;
        if (prefix.Length > maxPrefixLength)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The pipe name prefix '{0}' is {1} characters long but must be at most {2} characters on this platform to leave room for a unique suffix.",
                    prefix,
                    prefix.Length,
                    maxPrefixLength),
                nameof(prefix));
        }

        return prefix + CreateUniqueSuffix();
    }

    /// <summary>
    /// Encodes a GUID as 22 URL-safe characters. Every character counts against the Unix socket path
    /// limit, so this is preferred over the 32-character hexadecimal form.
    /// </summary>
    private static string CreateUniqueSuffix()
    {
        var encoded = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Substring(0, UniqueSuffixLength)
            .Replace('+', '-')
            .Replace('/', '_');
        return encoded;
    }

    /// <summary>
    /// Creates a named pipe server that receives MSBuild logging events from a single client.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty, whitespace, or longer than <see cref="MaxPipeNameLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName)
        : this(pipeName, false, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a named pipe server that receives MSBuild logging events from a single client.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty, whitespace, or longer than <see cref="MaxPipeNameLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    public NamedPipeLoggerServer(string pipeName, CancellationToken cancellationToken)
        : this(pipeName, false, cancellationToken)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="acceptMultipleConnections">
    /// <see langword="true"/> to keep listening after a client disconnects so that a build made up of
    /// several MSBuild submissions can be observed through one server; otherwise <see langword="false"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty, whitespace, or longer than <see cref="MaxPipeNameLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// When <paramref name="acceptMultipleConnections"/> is <see langword="true"/>, the transport no
    /// longer ends when a client disconnects, so <see cref="PipeLoggerServer{TPipeStream}.ReadAll"/>
    /// and <see cref="PipeLoggerServer{TPipeStream}.Read"/> keep waiting for the next client. Dispose
    /// the server (or trigger its cancellation token) once the observed build process has exited.
    /// </remarks>
    public NamedPipeLoggerServer(string pipeName, bool acceptMultipleConnections)
        : this(pipeName, acceptMultipleConnections, CancellationToken.None)
    {
    }

    /// <summary>
    /// Creates a named pipe server for receiving MSBuild logging events.
    /// </summary>
    /// <param name="pipeName">The name of the pipe to create.</param>
    /// <param name="acceptMultipleConnections">
    /// <see langword="true"/> to keep listening after a client disconnects so that a build made up of
    /// several MSBuild submissions can be observed through one server; otherwise <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that will cancel read operations if triggered.</param>
    /// <exception cref="ArgumentException"><paramref name="pipeName"/> is empty, whitespace, or longer than <see cref="MaxPipeNameLength"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pipeName"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// When <paramref name="acceptMultipleConnections"/> is <see langword="true"/>, the transport no
    /// longer ends when a client disconnects, so <see cref="PipeLoggerServer{TPipeStream}.ReadAll"/>
    /// and <see cref="PipeLoggerServer{TPipeStream}.Read"/> keep waiting for the next client. Dispose
    /// the server (or trigger its cancellation token) once the observed build process has exited.
    /// </remarks>
    public NamedPipeLoggerServer(string pipeName, bool acceptMultipleConnections, CancellationToken cancellationToken)
        : base(CreatePipe(pipeName), cancellationToken, false)
    {
        PipeName = pipeName;
        AcceptsMultipleConnections = acceptMultipleConnections;
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
    protected override void Connect()
    {
        _connected.Unset();
        PipeStream.WaitForConnection();
        _connected.Set();
    }

    /// <inheritdoc/>
    protected override bool Reconnect()
    {
        if (!AcceptsMultipleConnections || IsShutdownRequested)
        {
            return false;
        }

        try
        {
            // Reusing the same listener is what makes this race-free: disposing it and creating a new
            // one leaves a window in which a client can arrive with nobody listening.
            PipeStream.Disconnect();
            Connect();
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
        catch (SocketException)
        {
            return false;
        }

        // A cancelled wait is unblocked by a dummy client, so re-check instead of trusting the connection.
        return !IsShutdownRequested;
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

        if (pipeName.Length > MaxPipeNameLength)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The pipe name '{0}' is {1} characters long but must be at most {2} characters on this platform. Use {3}.{4}() to create a name that always fits.",
                    pipeName,
                    pipeName.Length,
                    MaxPipeNameLength,
                    nameof(NamedPipeLoggerServer),
                    nameof(CreatePipeName)),
                nameof(pipeName));
        }

        return new NamedPipeServerStream(pipeName, PipeDirection.In);
    }

    private static int CalculateMaxPipeNameLength()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsMaxPipePathLength - WindowsPipePathPrefix.Length;
        }

        // .NET maps a named pipe to Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName), and
        // the whole socket path has to fit in sockaddr_un.
        var directory = Path.GetTempPath();
        if (directory.Length > 0 && directory[directory.Length - 1] != Path.DirectorySeparatorChar)
        {
            directory += Path.DirectorySeparatorChar;
        }

        var available = UnixSocketPathBufferLength - 1 - directory.Length - UnixPipeFilePrefix.Length;
        return available > 0 ? available : 0;
    }

    private void CancelConnectionWait()
    {
        if (_connected.Set())
        {
            return;
        }

        try
        {
            // This stops WaitForConnection by connecting a dummy client. Checking IsConnected is not
            // reliable here because a quick connect/disconnect may never be observed as connected.
            using (var pipeStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                pipeStream.Connect(CancelConnectionTimeoutMilliseconds);
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
