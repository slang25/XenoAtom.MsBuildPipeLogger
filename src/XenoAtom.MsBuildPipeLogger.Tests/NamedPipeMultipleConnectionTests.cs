// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// Covers a server that observes several MSBuild submissions, each of which connects, writes, and
/// disconnects on its own.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NamedPipeMultipleConnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task AcceptsMultipleConnections_ReadsEveryConnection()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true);
        var events = new List<BuildEventArgs>();
        server.AnyEventRaised += (_, e) => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        for (var connection = 0; connection < 3; connection++)
        {
            using var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}");
            writer.Write(new BuildStartedEventArgs($"started-{connection}", "help"));
            writer.Write(new BuildMessageEventArgs($"message-{connection}", "help", "sender", MessageImportance.Normal));
            writer.Write(new BuildFinishedEventArgs($"finished-{connection}", "help", true));
        }

        await WaitForAsync(() => events.Count >= 9).ConfigureAwait(false);
        server.Dispose();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        Assert.AreEqual(9, events.Count);
        for (var connection = 0; connection < 3; connection++)
        {
            Assert.AreEqual($"started-{connection}", events[(connection * 3) + 0].Message);
            Assert.AreEqual($"message-{connection}", events[(connection * 3) + 1].Message);
            Assert.AreEqual($"finished-{connection}", events[(connection * 3) + 2].Message);
        }
    }

    [TestMethod]
    public async Task AcceptsMultipleConnections_DoesNotResolveStringsAgainstThePreviousConnection()
    {
        // Every client writes with a fresh event writer whose string table restarts from scratch. If
        // the server carried one reader across connections, the second connection's string indexes
        // would resolve against the first connection's strings and silently return its text.
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true);
        var messages = new List<string?>();
        server.MessageRaised += (_, e) => messages.Add(e.Message);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("first-connection-text", "help", "sender", MessageImportance.Normal));
        }

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("second-connection-text", "help", "sender", MessageImportance.Normal));
        }

        await WaitForAsync(() => messages.Count >= 2).ConfigureAwait(false);
        server.Dispose();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "first-connection-text", "second-connection-text" }, messages);
    }

    [TestMethod]
    public async Task SingleConnectionServer_StopsAfterTheClientDisconnects()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("only", "help", "sender", MessageImportance.Normal));
        }

        // Without acceptMultipleConnections the transport still ends with the client, so ReadAll
        // returns on its own rather than waiting for a disposal.
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AcceptsMultipleConnections_DisposeUnblocksReadWhileWaitingForTheNextClient()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true);
        var messages = new List<string?>();
        server.MessageRaised += (_, e) => messages.Add(e.Message);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("only", "help", "sender", MessageImportance.Normal));
        }

        await WaitForAsync(() => messages.Count >= 1).ConfigureAwait(false);
        server.Dispose();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AcceptsMultipleConnections_CancellationUnblocksReadWhileWaitingForTheNextClient()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var tokenSource = new CancellationTokenSource();
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true, tokenSource.Token);
        var messages = new List<string?>();
        server.MessageRaised += (_, e) => messages.Add(e.Message);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("only", "help", "sender", MessageImportance.Normal));
        }

        await WaitForAsync(() => messages.Count >= 1).ConfigureAwait(false);
        tokenSource.Cancel();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected events did not arrive before the timeout.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
