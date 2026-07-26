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
        using var server = new NamedPipeLoggerServer(pipeName);
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

        server.StopListening();
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
        using var server = new NamedPipeLoggerServer(pipeName);
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

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "first-connection-text", "second-connection-text" }, messages);
    }

    [TestMethod]
    public async Task StopListening_DeliversEveryBufferedEvent()
    {
        // Dispose ends the transport at once and drops whatever the reader has not handed over yet,
        // which loses the tail of a build. StopListening has to drain instead. Repeated because the
        // loss depends on how far the reader happens to have got.
        const int messageCount = 500;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
            using var server = new NamedPipeLoggerServer(pipeName);
            var received = 0;
            server.MessageRaised += (_, _) => Interlocked.Increment(ref received);
            var readTask = Task.Run(server.ReadAll);

            using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
            {
                for (var index = 0; index < messageCount; index++)
                {
                    writer.Write(new BuildMessageEventArgs($"message-{index}", "help", "sender", MessageImportance.Normal));
                }
            }

            server.StopListening();
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

            Assert.AreEqual(messageCount, Volatile.Read(ref received), $"Events were dropped on attempt {attempt}.");
        }
    }

    [TestMethod]
    public async Task OptingOut_StopsAfterTheClientDisconnects()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: false);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            writer.Write(new BuildMessageEventArgs("only", "help", "sender", MessageImportance.Normal));
        }

        // With acceptMultipleConnections: false the transport ends with the client, so ReadAll
        // returns on its own without StopListening.
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task AcceptsMultipleConnections_DisposeUnblocksReadWhileWaitingForTheNextClient()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName);
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
        using var server = new NamedPipeLoggerServer(pipeName, tokenSource.Token);
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
