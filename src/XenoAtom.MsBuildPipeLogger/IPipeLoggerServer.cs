// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using Microsoft.Build.Framework;

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
    BuildEventArgs? Read();

    /// <summary>
    /// Reads all events from the pipe and blocks until there are no more events or the pipe is closed.
    /// </summary>
    /// <remarks>
    /// A transport that serves several client connections (see <see cref="StopAcceptingConnections"/>) stays
    /// open between them, so this method only returns once the server stops accepting connections, is
    /// canceled, or is disposed.
    /// </remarks>
    void ReadAll();

    /// <summary>
    /// Stops accepting new client connections. Events that were already received, as well as the events of
    /// a client that is still connected, are still dispatched, after which <see cref="Read"/> returns
    /// <see langword="null"/> and <see cref="ReadAll"/> returns.
    /// </summary>
    /// <remarks>
    /// MSBuild connects one client per build submission, and a single command such as
    /// <c>dotnet build -f &lt;tfm&gt;</c> runs several submissions, so the server cannot tell the end of a
    /// submission from the end of the build. Call this method once the process being observed has exited.
    /// Disposing the server has the same unblocking effect but does not wait for buffered events.
    /// </remarks>
    void StopAcceptingConnections();
}