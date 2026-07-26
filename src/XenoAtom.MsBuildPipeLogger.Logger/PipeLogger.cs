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
    private readonly Dictionary<string, string?> _originalEnvironmentVariables = new(StringComparer.Ordinal);

    private IEventSource? _eventSource;

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
        Pipe = InitializePipeWriter();
        InitializeEvents(eventSource);
    }

    /// <summary>
    /// Initializes environment variables that enable additional MSBuild logging data.
    /// </summary>
    /// <remarks>
    /// Set variables through <see cref="SetEnvironmentVariable"/> so that <see cref="Shutdown"/> can
    /// put them back. MSBuild reuses its nodes between builds by default, so a variable left behind
    /// here would keep raising the event volume of later, unrelated builds in the same process.
    /// </remarks>
    protected virtual void InitializeEnvironmentVariables()
    {
        SetEnvironmentVariable("MSBUILDTARGETOUTPUTLOGGING", "true");
        SetEnvironmentVariable("MSBUILDLOGIMPORTS", "1");
    }

    /// <summary>
    /// Sets an environment variable for the duration of the build and remembers its previous value so
    /// that <see cref="Shutdown"/> can restore it.
    /// </summary>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value to set, or <see langword="null"/> to remove the variable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    protected void SetEnvironmentVariable(string name, string? value)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (!_originalEnvironmentVariables.ContainsKey(name))
        {
            _originalEnvironmentVariables[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Restores every environment variable that was set through <see cref="SetEnvironmentVariable"/>.
    /// </summary>
    protected virtual void RestoreEnvironmentVariables()
    {
        foreach (var variable in _originalEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }

        _originalEnvironmentVariables.Clear();
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
