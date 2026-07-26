// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Runtime.InteropServices;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// MSBuild attaches a logger once per build submission, so a single child process can open several
/// sequential pipe connections. These tests cover a server that has to survive that.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NamedPipeMultipleSubmissionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    [DataRow(2)]
    [DataRow(5)]
    public async Task Server_ServesEverySequentialSubmission(int submissionCount)
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        // Each iteration is one MSBuild submission: connect, log, disconnect. Before the fix the
        // second connection was never accepted, so this blocked forever.
        for (var submission = 0; submission < submissionCount; submission++)
        {
            using var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}");
            BuildEventAssertions.WriteEvents(writer, messageCount: 5, includeBuildFinished: true);
        }

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        Assert.AreEqual(submissionCount, events.OfType<PipeBuildFinishedEventArgs>().Count());
        Assert.AreEqual(submissionCount, events.OfType<PipeBuildStartedEventArgs>().Count());
        Assert.AreEqual(submissionCount * 5, events.OfType<PipeBuildMessageEventArgs>().Count());
    }

    [TestMethod]
    public async Task ReadAll_KeepsDrainingAfterBuildFinished()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 1, includeBuildFinished: true, includeMessageAfterBuildFinished: true);
        }

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        // BuildFinished ends a submission, not the transport, so the event after it must still arrive.
        Assert.AreEqual(2, events.OfType<PipeBuildMessageEventArgs>().Count());
    }

    [TestMethod]
    public async Task SingleConnectionServer_EndsReadWhenTheClientDisconnects()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: false);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 3, includeBuildFinished: true);
        }

        // Opting out of multiple connections means the caller does not have to call StopListening.
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        Assert.AreEqual(3, events.OfType<PipeBuildMessageEventArgs>().Count());
    }

    [TestMethod]
    public async Task StopListening_UnblocksAReaderWaitingForItsFirstClient()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var readTask = Task.Run(server.ReadAll);

        server.StopListening();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StopListening_UnblocksTheReaderWhateverTheTiming()
    {
        // The stop can land before the reader reaches the accept, while it is blocked in the accept, or
        // while it is draining a client. Claiming the wait and observing the stop have to be one atomic
        // step or a stop in the first window finds nothing to unblock and the reader waits forever.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var pipeName = CreatePipeName();
            using var server = new NamedPipeLoggerServer(pipeName);
            var readTask = Task.Run(server.ReadAll);

            if (attempt % 2 == 0)
            {
                using var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}");
                BuildEventAssertions.WriteEvents(writer, messageCount: 1, includeBuildFinished: true);
            }

            if (attempt % 4 >= 2)
            {
                await Task.Delay(attempt % 3).ConfigureAwait(false);
            }

            server.StopListening();

            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task StopListening_DrainsEventsAlreadyReceived()
    {
        // Dispose tears the transport down at once and can discard buffered events; StopListening has to
        // stop accepting clients but still hand over everything already read.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var pipeName = CreatePipeName();
            using var server = new NamedPipeLoggerServer(pipeName);
            var events = new List<PipeBuildEventArgs>();
            server.AnyEventRaised += e => events.Add(e);
            var readTask = Task.Run(server.ReadAll);

            using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
            {
                BuildEventAssertions.WriteEvents(writer, messageCount: 500, includeBuildFinished: true);
            }

            server.StopListening();
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

            Assert.AreEqual(500, events.OfType<PipeBuildMessageEventArgs>().Count(), $"Attempt {attempt} lost events.");
        }
    }

    [TestMethod]
    public async Task StopListening_WorksWhenTheServerCannotBeConnectedTo()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("The socket path is a Unix implementation detail.");
            return;
        }

        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var readTask = Task.Run(server.ReadAll);

        // Give the listener time to reach the accept, then make the pipe unreachable for new clients.
        await Task.Delay(200).ConfigureAwait(false);
        var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        Assert.IsTrue(File.Exists(socketPath), $"Expected a socket at '{socketPath}'.");
        File.Delete(socketPath);

        // Stopping must not depend on being able to connect to ourselves: an implementation that wakes the
        // accept by opening a client connection hangs here forever, because that connection can no longer
        // be made and the failure is invisible.
        server.StopListening();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StopListening_FromManyThreadsAtOnce_IsSafe()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var readTask = Task.Run(server.ReadAll);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(server.StopListening))).ConfigureAwait(false);

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task StopListening_IsIdempotent()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var readTask = Task.Run(server.ReadAll);

        server.StopListening();
        server.StopListening();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        server.StopListening();
    }

    private static string CreatePipeName() => PipeLoggerServer.CreateUniquePipeName("xa-test");
}
