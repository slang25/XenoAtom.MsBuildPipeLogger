// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.IO.Pipes;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// A client that dies part-way through writing a record leaves a length prefix promising bytes that never
/// arrive. Because one reader spans every connection, that prefix must not be allowed to consume the next
/// client's bytes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TruncatedRecordTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // A Message record kind, a length prefix claiming 200 bytes, and only three bytes of payload.
    private static readonly byte[] TruncatedRecord = { 11, 200, 1, 1, 1 };

    // A record kind with nothing at all after it, so even the length prefix is incomplete.
    private static readonly byte[] TruncatedVarint = { 11 };

    [TestMethod]
    [DynamicData(nameof(TruncationCases))]
    public async Task TruncatedClient_DoesNotConsumeTheNextSubmission(string name, byte[] truncated)
    {
        var pipeName = PipeLoggerServer.CreateUniquePipeName("xa-test");
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        WriteRawAndDisconnect(pipeName, truncated);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 4, includeBuildFinished: true);
        }

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        // The following submission has to survive intact: the truncated record is discarded on its own.
        Assert.AreEqual(4, events.OfType<PipeBuildMessageEventArgs>().Count(), name);
        Assert.AreEqual(1, events.OfType<PipeBuildStartedEventArgs>().Count(), name);
        Assert.AreEqual(1, events.OfType<PipeBuildFinishedEventArgs>().Count(), name);
    }

    [TestMethod]
    [DynamicData(nameof(TruncationCases))]
    public async Task TruncatedClient_AfterAGoodSubmission_KeepsTheEarlierEvents(string name, byte[] truncated)
    {
        var pipeName = PipeLoggerServer.CreateUniquePipeName("xa-test");
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 3, includeBuildFinished: true);
        }

        WriteRawAndDisconnect(pipeName, truncated);

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        // A truncated final client must not cost the events that were already complete, and must not hang.
        Assert.AreEqual(3, events.OfType<PipeBuildMessageEventArgs>().Count(), name);
    }

    [TestMethod]
    public async Task TruncatedClient_BetweenTwoGoodSubmissions_LosesOnlyItself()
    {
        var pipeName = PipeLoggerServer.CreateUniquePipeName("xa-test");
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 2, includeBuildFinished: true);
        }

        WriteRawAndDisconnect(pipeName, TruncatedRecord);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 6, includeBuildFinished: true);
        }

        server.StopListening();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        Assert.AreEqual(8, events.OfType<PipeBuildMessageEventArgs>().Count());
        Assert.AreEqual(2, events.OfType<PipeBuildFinishedEventArgs>().Count());
    }

    public static IEnumerable<object[]> TruncationCases =>
        new[]
        {
            new object[] { "truncated payload", TruncatedRecord },
            new object[] { "truncated length prefix", TruncatedVarint },
        };

    private static void WriteRawAndDisconnect(string pipeName, byte[] bytes)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        client.Connect(5000);
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
    }
}
