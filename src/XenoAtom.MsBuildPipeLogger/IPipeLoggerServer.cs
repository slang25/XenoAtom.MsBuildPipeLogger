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
    /// A server that accepts more than one connection has no way of knowing that the last client has
    /// been and gone, so this keeps waiting for the next one. Call <see cref="StopListening"/> once
    /// the build being observed has finished.
    /// </remarks>
    void ReadAll();

    /// <summary>
    /// Stops waiting for further clients and lets the events that have already been received finish
    /// being dispatched, after which <see cref="Read"/> and <see cref="ReadAll"/> return.
    /// </summary>
    /// <remarks>
    /// Call this once the build process being observed has exited. Unlike <see cref="IDisposable.Dispose"/>,
    /// which ends the transport at once and discards anything still buffered, this drains first and is
    /// therefore the lossless way to finish reading.
    /// </remarks>
    void StopListening();
}