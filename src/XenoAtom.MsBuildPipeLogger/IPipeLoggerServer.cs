// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

namespace XenoAtom.MsBuildPipeLogger;

/// <summary>
/// Receives serialized MSBuild events from a logger transport.
/// </summary>
public interface IPipeLoggerServer : IDisposable
{
    /// <summary>
    /// Reads a single event from the pipe. This method blocks until an event is received,
    /// there are no more events, or the pipe is closed.
    /// </summary>
    /// <returns>The read event or <see langword="null"/> if there are no more events or the pipe is closed.</returns>
    PipeBuildEventArgs? Read();

    /// <summary>
    /// Reads all events from the pipe and blocks until there are no more events or the pipe is closed.
    /// </summary>
    /// <remarks>
    /// This drains the whole transport rather than stopping at the first <see cref="PipeBuildFinishedEventArgs"/>,
    /// because MSBuild raises one of those per build submission and a single build can run several
    /// (<c>dotnet build</c> runs restore and build as separate submissions). For a transport that serves
    /// multiple connections, call <see cref="StopListening"/> once the observed build process has exited,
    /// otherwise this keeps waiting for the next client.
    /// </remarks>
    void ReadAll();

    /// <summary>
    /// Stops accepting new client connections and lets the events already received drain, so that a
    /// pending <see cref="Read"/> or <see cref="ReadAll"/> can return.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this once the build process being observed has exited. Prefer it over <see cref="IDisposable.Dispose"/>
    /// to finish reading: <see cref="IDisposable.Dispose"/> tears the transport down immediately and can
    /// discard events that have been received but not yet handed to the caller. It is safe to call more
    /// than once and from any thread.
    /// </para>
    /// <para>
    /// This only does something on a transport that accepts more than one connection, which is what makes a
    /// read outlive any single client. On a single-connection transport such as
    /// <see cref="AnonymousPipeLoggerServer"/> the read already ends by itself when the client goes away, so
    /// this is a no-op and the pattern above still works; ending a read <em>early</em> there means
    /// <see cref="IDisposable.Dispose"/>, with the event loss that implies.
    /// </para>
    /// </remarks>
    void StopListening();
}