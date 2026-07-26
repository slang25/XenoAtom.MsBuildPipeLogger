// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;
using System.Text;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Provides helpers for locating and configuring the MSBuild pipe logger that is bundled with this package.
/// </summary>
public static class PipeLoggerServer
{
    /// <summary>
    /// The prefix used by <see cref="CreateUniquePipeName()"/>. Deliberately short: on Unix the whole
    /// socket path has to fit in a small fixed-size field, so every character spent here is one fewer
    /// available to the caller.
    /// </summary>
    private const string DefaultPipeNamePrefix = "msbpipe";

    /// <summary>
    /// <see cref="System.IO.Pipes"/> maps a Unix pipe name to a domain socket at
    /// <c>&lt;temp&gt;/CoreFxPipe_&lt;name&gt;</c>, and the whole path must fit the platform's
    /// <c>sockaddr_un.sun_path</c> field: 104 bytes on macOS and 108 on Linux. The smaller of the two
    /// is used so a name minted on one platform stays valid on the other.
    /// </summary>
    private const int UnixMaximumSocketPathLength = 104;

    /// <summary>
    /// The file name prefix <see cref="System.IO.Pipes"/> gives the domain socket backing a Unix pipe.
    /// </summary>
    private const string UnixPipePathPrefix = "CoreFxPipe_";

    /// <summary>
    /// Windows named pipes live under <c>\\.\pipe\</c> and the name may be up to 256 characters.
    /// </summary>
    private const int WindowsMaximumPipeNameLength = 256;

    /// <summary>
    /// Gets the longest pipe name that <see cref="NamedPipeLoggerServer"/> can use on the current platform.
    /// </summary>
    /// <remarks>
    /// On Unix a pipe name becomes part of a domain socket path whose total length is capped by the
    /// operating system, so the limit depends on the length of the temporary directory and can be far
    /// shorter than callers expect — a per-user <c>TMPDIR</c> such as macOS's leaves only about 44
    /// characters. Prefer <see cref="CreateUniquePipeName()"/>, which always returns a name that fits.
    /// The length is measured in UTF-8 bytes, which is the same as the character count for the ASCII
    /// names that pipe names should use anyway.
    /// </remarks>
    /// <returns>The maximum supported pipe name length, or <c>0</c> if no name can fit.</returns>
    public static int GetMaximumPipeNameLength()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsMaximumPipeNameLength;
        }

        var prefixLength = Encoding.UTF8.GetByteCount(Path.Combine(Path.GetTempPath(), UnixPipePathPrefix));
        var available = UnixMaximumSocketPathLength - prefixLength;
        return available > 0 ? available : 0;
    }

    /// <summary>
    /// Creates a unique pipe name that is guaranteed to fit within <see cref="GetMaximumPipeNameLength()"/>
    /// on the current platform.
    /// </summary>
    /// <returns>A unique pipe name suitable for <see cref="NamedPipeLoggerServer"/>.</returns>
    /// <exception cref="InvalidOperationException">The platform cannot fit any unique pipe name, because the
    /// temporary directory path is too long.</exception>
    public static string CreateUniquePipeName() => CreateUniquePipeName(DefaultPipeNamePrefix);

    /// <summary>
    /// Creates a unique pipe name with the specified prefix that is guaranteed to fit within
    /// <see cref="GetMaximumPipeNameLength()"/> on the current platform.
    /// </summary>
    /// <param name="prefix">A prefix to make the name recognizable, for example the host application name.
    /// It is truncated, or dropped entirely, when the platform cannot fit it alongside the unique portion
    /// of the name.</param>
    /// <returns>A unique pipe name suitable for <see cref="NamedPipeLoggerServer"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> contains a directory separator.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The platform cannot fit any unique pipe name, because the
    /// temporary directory path is too long.</exception>
    public static string CreateUniquePipeName(string prefix)
    {
        if (prefix is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        if (prefix.IndexOf('/') >= 0 || prefix.IndexOf('\\') >= 0)
        {
            throw new ArgumentException("The pipe name prefix cannot contain a directory separator.", nameof(prefix));
        }

        // The unique portion is what keeps concurrent builds from colliding, so it is never shortened;
        // the prefix is cosmetic and gets whatever room is left over.
        var unique = CreateUniqueToken();
        var maximum = GetMaximumPipeNameLength();
        if (maximum < unique.Length)
        {
            throw new InvalidOperationException(
                $"A unique pipe name needs {unique.Length} characters but this platform allows only {maximum}, " +
                $"because the temporary directory path '{Path.GetTempPath()}' is too long. Set the TMPDIR " +
                "environment variable to a shorter directory.");
        }

        var prefixRoom = maximum - unique.Length - 1;
        if (prefix.Length == 0 || prefixRoom <= 0)
        {
            return unique;
        }

        return string.Concat(prefix.Substring(0, Math.Min(prefix.Length, prefixRoom)), "-", unique);
    }

    /// <summary>
    /// Creates a 22 character unique token: a GUID's 128 bits in base64url, which is 10 characters shorter
    /// than the usual <c>N</c> format and safe to use in a file name.
    /// </summary>
    private static string CreateUniqueToken() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Substring(0, 22)
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// The directory, relative to <see cref="AppContext.BaseDirectory"/>, that contains the bundled logger assembly.
    /// </summary>
    public const string LoggerDirectoryName = "XenoAtom.MsBuildPipeLogger";

    /// <summary>
    /// The file name of the bundled logger assembly.
    /// </summary>
    public const string LoggerAssemblyFileName = "XenoAtom.MsBuildPipeLogger.Logger.dll";

    /// <summary>
    /// The fully qualified MSBuild logger type name.
    /// </summary>
    public const string LoggerTypeName = "XenoAtom.MsBuildPipeLogger.PipeLogger";

    /// <summary>
    /// Gets the expected location of the bundled logger assembly under <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    /// <returns>The absolute path to the bundled logger assembly.</returns>
    public static string GetLoggerAssemblyPath() => GetLoggerAssemblyPath(AppContext.BaseDirectory);

    /// <summary>
    /// Gets the expected location of the bundled logger assembly under the specified base directory.
    /// </summary>
    /// <param name="baseDirectory">The application base directory that contains the logger content directory.</param>
    /// <returns>The absolute path to the bundled logger assembly.</returns>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="baseDirectory"/> is <see langword="null"/>.</exception>
    public static string GetLoggerAssemblyPath(string baseDirectory)
    {
        if (baseDirectory is null)
        {
            throw new ArgumentNullException(nameof(baseDirectory));
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("The base directory cannot be empty or whitespace.", nameof(baseDirectory));
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, LoggerDirectoryName, LoggerAssemblyFileName));
    }

    /// <summary>
    /// Gets an MSBuild logger specification that can be passed to MSBuild's logger command-line option.
    /// </summary>
    /// <param name="loggerParameters">The pipe logger parameters, such as an anonymous pipe handle or <c>name=&lt;pipeName&gt;</c>.</param>
    /// <returns>An MSBuild logger specification in the form <c>type,assembly;parameters</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="loggerParameters"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="loggerParameters"/> is <see langword="null"/>.</exception>
    public static string GetLoggerSpecification(string loggerParameters) =>
        GetLoggerSpecification(AppContext.BaseDirectory, loggerParameters);

    /// <summary>
    /// Gets an MSBuild logger specification that can be passed to MSBuild's logger command-line option.
    /// </summary>
    /// <param name="baseDirectory">The application base directory that contains the logger content directory.</param>
    /// <param name="loggerParameters">The pipe logger parameters, such as an anonymous pipe handle or <c>name=&lt;pipeName&gt;</c>.</param>
    /// <returns>An MSBuild logger specification in the form <c>type,assembly;parameters</c>.</returns>
    /// <exception cref="ArgumentException"><paramref name="baseDirectory"/> or <paramref name="loggerParameters"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="baseDirectory"/> or <paramref name="loggerParameters"/> is <see langword="null"/>.</exception>
    public static string GetLoggerSpecification(string baseDirectory, string loggerParameters)
    {
        if (loggerParameters is null)
        {
            throw new ArgumentNullException(nameof(loggerParameters));
        }

        if (string.IsNullOrWhiteSpace(loggerParameters))
        {
            throw new ArgumentException("The logger parameters cannot be empty or whitespace.", nameof(loggerParameters));
        }

        return $"{LoggerTypeName},{GetLoggerAssemblyPath(baseDirectory)};{loggerParameters}";
    }
}
