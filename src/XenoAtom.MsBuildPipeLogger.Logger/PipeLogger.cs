// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Logger to send messages from the MSBuild logging system over an anonymous or named pipe.
/// </summary>
/// <remarks>
/// Heavily based on the work of Kirill Osenkov and the MSBuildStructuredLog project.
/// </remarks>
public class PipeLogger : Logger
{
    private const string TargetOutputLoggingVariable = "MSBUILDTARGETOUTPUTLOGGING";
    private const string LogImportsVariable = "MSBUILDLOGIMPORTS";

    private IEventSource? _eventSource;
    private string? _previousTargetOutputLogging;
    private string? _previousLogImports;
    private bool _environmentVariablesInitialized;

    /// <summary>
    /// Gets the active pipe writer after the logger has been initialized.
    /// </summary>
    protected IPipeWriter? Pipe { get; private set; }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="eventSource"/> is <see langword="null"/>.</exception>
    public override void Initialize(IEventSource eventSource)
    {
        if (eventSource is null)
        {
            throw new ArgumentNullException(nameof(eventSource));
        }

        InitializeEnvironmentVariables();

        try
        {
            Pipe = InitializePipeWriter();
        }
        catch (Exception)
        {
            // A logger that cannot reach its pipe must not tear down the build it is observing, for the
            // same reason a failed write does not. MSBuild turns an exception from Initialize into a hard
            // MSB4016 build failure, so the build continues unlogged instead.
            //
            // An anonymous pipe reaches this on any build submission after the first: Shutdown disposes the
            // client stream, which closes the inherited handle, and a handle cannot be reopened. Named pipes
            // reconnect per submission and are the transport to use when a build may run more than one.
            RestoreEnvironmentVariables();
            return;
        }

        InitializeEvents(eventSource);
    }

    /// <summary>
    /// Initializes environment variables that enable additional MSBuild logging data, remembering the
    /// previous values so that <see cref="RestoreEnvironmentVariables"/> can put them back.
    /// </summary>
    protected virtual void InitializeEnvironmentVariables()
    {
        _previousTargetOutputLogging = Environment.GetEnvironmentVariable(TargetOutputLoggingVariable);
        _previousLogImports = Environment.GetEnvironmentVariable(LogImportsVariable);
        _environmentVariablesInitialized = true;

        Environment.SetEnvironmentVariable(TargetOutputLoggingVariable, "true");
        Environment.SetEnvironmentVariable(LogImportsVariable, "1");
    }

    /// <summary>
    /// Restores the environment variables that <see cref="InitializeEnvironmentVariables"/> changed.
    /// </summary>
    /// <remarks>
    /// These are process-wide, and with MSBuild node reuse the process outlives the build. Leaving them set
    /// would raise the event volume of subsequent, unrelated builds that land on a reused node.
    /// </remarks>
    protected virtual void RestoreEnvironmentVariables()
    {
        if (!_environmentVariablesInitialized)
        {
            return;
        }

        _environmentVariablesInitialized = false;

        // Setting a variable to null removes it, which is the correct restore when it was not set before.
        Environment.SetEnvironmentVariable(TargetOutputLoggingVariable, _previousTargetOutputLogging);
        Environment.SetEnvironmentVariable(LogImportsVariable, _previousLogImports);
        _previousTargetOutputLogging = null;
        _previousLogImports = null;
    }

    /// <summary>
    /// Creates the pipe writer specified by the logger parameters.
    /// </summary>
    /// <returns>The initialized pipe writer.</returns>
    protected virtual IPipeWriter InitializePipeWriter() => ParameterParser.GetPipeFromParameters(Parameters ?? string.Empty);

    /// <summary>
    /// Subscribes to MSBuild events and forwards them to the active pipe writer.
    /// </summary>
    /// <param name="eventSource">The MSBuild event source.</param>
    /// <exception cref="ArgumentNullException"><paramref name="eventSource"/> is <see langword="null"/>.</exception>
    protected virtual void InitializeEvents(IEventSource eventSource)
    {
        if (eventSource is null)
        {
            throw new ArgumentNullException(nameof(eventSource));
        }

        _eventSource = eventSource;
        eventSource.AnyEventRaised += OnAnyEventRaised;
    }

    /// <inheritdoc/>
    public override void Shutdown()
    {
        base.Shutdown();
        if (_eventSource is not null)
        {
            _eventSource.AnyEventRaised -= OnAnyEventRaised;
            _eventSource = null;
        }

        Pipe?.Dispose();
        Pipe = null;
        RestoreEnvironmentVariables();
    }

    private void OnAnyEventRaised(object sender, BuildEventArgs e)
    {
        try
        {
            Pipe?.Write(e);
        }
        catch (Exception)
        {
            // Logging failures must not tear down the build that is being observed.
        }
    }
}
