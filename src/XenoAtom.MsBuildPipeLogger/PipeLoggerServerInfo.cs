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
    /// The prefix used by <see cref="CreateUniquePipeName()"/> when no other prefix is supplied.
    /// </summary>
    public const string DefaultPipeNamePrefix = "MsBuildPipeLogger-";

    // On Unix a named pipe is a domain socket at "<temp>/CoreFxPipe_<pipeName>" and the whole path,
    // including its terminator, must fit in sockaddr_un.sun_path. That is 104 bytes on macOS and 108 on
    // Linux, so 104 is used here to keep generated names portable.
    private const int UnixSocketPathLength = 104;
    private const string UnixPipePathPrefix = "CoreFxPipe_";

    // \\.\pipe\<pipeName> must fit in MAX_PATH.
    private const int WindowsPipePathLength = 256;
    private const string WindowsPipePathPrefix = @"\\.\pipe\";

    // A truncated GUID still has to keep collisions between concurrent builds out of reach.
    private const int MinimumUniqueSuffixLength = 12;

    /// <summary>
    /// Creates a unique pipe name that is short enough to be used on the current platform.
    /// </summary>
    /// <returns>A unique pipe name prefixed with <see cref="DefaultPipeNamePrefix"/>.</returns>
    public static string CreateUniquePipeName() => CreateUniquePipeName(DefaultPipeNamePrefix);

    /// <summary>
    /// Creates a unique pipe name with the specified prefix that is short enough to be used on the current platform.
    /// </summary>
    /// <param name="prefix">The prefix to prepend to the generated unique part. May be empty.</param>
    /// <returns>A unique pipe name starting with <paramref name="prefix"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is too long to leave room for a unique suffix.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="prefix"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// On Unix the name becomes part of a domain socket path with a hard length limit, which a name built from
    /// a full <see cref="Guid"/> and a descriptive prefix can easily exceed. The unique part is shortened as
    /// needed to fit.
    /// </remarks>
    public static string CreateUniquePipeName(string prefix)
    {
        if (prefix is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        var available = GetMaximumPipeNameLength() - Encoding.UTF8.GetByteCount(prefix);
        if (available < MinimumUniqueSuffixLength)
        {
            throw new ArgumentException(
                $"The prefix '{prefix}' leaves room for {available} characters, but at least {MinimumUniqueSuffixLength} " +
                $"are needed to make the pipe name unique. Pipe names are limited to {GetMaximumPipeNameLength()} characters on this platform.",
                nameof(prefix));
        }

        var unique = Guid.NewGuid().ToString("N");
        return prefix + (available < unique.Length ? unique.Substring(0, available) : unique);
    }

    /// <summary>
    /// Gets the maximum length of a named pipe name on the current platform.
    /// </summary>
    /// <returns>The maximum number of ASCII characters a pipe name may contain.</returns>
    /// <remarks>
    /// On Unix this depends on the temporary directory the pipe path is built from, so it is a runtime value
    /// rather than a constant. Names containing non-ASCII characters are limited further because the underlying
    /// limit applies to UTF-8 bytes.
    /// </remarks>
    public static int GetMaximumPipeNameLength()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsPipePathLength - WindowsPipePathPrefix.Length;
        }

        var fixedPath = Path.Combine(Path.GetTempPath(), UnixPipePathPrefix);

        // The path is null terminated, so one byte of the limit is never available to the name.
        return Math.Max(0, UnixSocketPathLength - 1 - Encoding.UTF8.GetByteCount(fixedPath));
    }

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
