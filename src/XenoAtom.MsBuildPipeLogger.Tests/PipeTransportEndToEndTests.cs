// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
[DoNotParallelize]
public class PipeTransportEndToEndTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(100000)]
    public async Task NamedPipe_TransportsEventsInProcess(int messageCount)
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount);
        }

        server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);
        BuildEventAssertions.AssertEvents(events, messageCount);
    }

    [TestMethod]
    public async Task ReadAll_ContinuesAfterBuildFinishedEvent()
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 1, includeBuildFinished: true, includeMessageAfterBuildFinished: true);
        }

        server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);

        // BuildFinished ends a submission, not the transport, so it must not stop the reader.
        Assert.AreEqual(4, events.Count);
        Assert.AreEqual("After finish", events[3].Message);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task NamedPipe_TransportsEventsFromSequentialConnections(int connectionCount)
    {
        // MSBuild connects one client per build submission, and `dotnet build -f <tfm>` runs a restore
        // submission followed by a build submission against the same server.
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var readTask = Task.Run(server.ReadAll);

        for (var connection = 0; connection < connectionCount; connection++)
        {
            using var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}");
            writer.Write(new BuildStartedEventArgs($"Submission {connection}", "help"));
            writer.Write(new BuildMessageEventArgs($"Message {connection}", "help", "sender", MessageImportance.Normal));
            writer.Write(new BuildFinishedEventArgs($"Submission {connection} finished", "help", true));
        }

        server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);

        Assert.AreEqual(connectionCount * 3, events.Count);
        for (var connection = 0; connection < connectionCount; connection++)
        {
            // Each connection restarts the binary log string tables, so a shared events reader would
            // resolve these strings back to the ones sent by the first connection.
            Assert.AreEqual($"Submission {connection}", events[(connection * 3) + 0].Message);
            Assert.AreEqual($"Message {connection}", events[(connection * 3) + 1].Message);
            Assert.AreEqual($"Submission {connection} finished", events[(connection * 3) + 2].Message);
        }
    }

    [TestMethod]
    public async Task NamedPipe_SecondClientConnectsWhileTheFirstIsStillConnected()
    {
        // The failure this covers is a build hanging rather than an error: a client that the server never
        // accepts still connects into the listener backlog and then blocks forever writing into it.
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var readTask = Task.Run(server.ReadAll);

        var first = ParameterParser.GetPipeFromParameters($"name={pipeName}");
        first.Write(new BuildStartedEventArgs("First", "help"));

        var second = await Task.Run(() => ParameterParser.GetPipeFromParameters($"name={pipeName}"))
            .WaitAsync(TestTimeout).ConfigureAwait(false);

        first.Dispose();
        second.Write(new BuildStartedEventArgs("Second", "help"));
        second.Dispose();

        server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "First", "Second" }, events.Select(x => x.Message).ToArray());
    }

    [TestMethod]
    public async Task StopAcceptingConnections_StillDrainsAClientThatWasAlreadyWaiting()
    {
        if (OperatingSystem.IsWindows())
        {
            // Only Unix queues a connection the server has not accepted yet, so this is the only place a
            // client can be waiting with buffered events at the moment the server is asked to stop.
            Assert.Inconclusive("Windows blocks the second client until the server accepts it.");
        }

        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var readTask = Task.Run(server.ReadAll);

        using var first = ParameterParser.GetPipeFromParameters($"name={pipeName}");
        first.Write(new BuildStartedEventArgs("First", "help"));

        // The server is still serving the first client, so this one only reaches the listener backlog.
        using var second = ParameterParser.GetPipeFromParameters($"name={pipeName}");
        second.Write(new BuildStartedEventArgs("Second", "help"));

        server.StopAcceptingConnections();
        first.Dispose();
        second.Dispose();

        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "First", "Second" }, events.Select(x => x.Message).ToArray());
    }

    [TestMethod]
    public async Task StopAcceptingConnections_UnblocksReadAllWhileWaitingForAClient()
    {
        using var server = new NamedPipeLoggerServer(CreatePipeName());
        var readTask = Task.Run(server.ReadAll);

        server.StopAcceptingConnections();

        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Server_DispatchesTypedEvents()
    {
        var pipeName = CreatePipeName();
        var buildStartedCount = 0;
        var messageCount = 0;
        var buildFinishedCount = 0;
        using var server = new NamedPipeLoggerServer(pipeName);
        server.BuildStarted += (_, _) => buildStartedCount++;
        server.MessageRaised += (_, _) => messageCount++;
        server.BuildFinished += (_, _) => buildFinishedCount++;
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 3, includeBuildFinished: true);
        }

        server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);
        Assert.AreEqual(1, buildStartedCount);
        Assert.AreEqual(3, messageCount);
        Assert.AreEqual(1, buildFinishedCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(10000)]
    public async Task PipeLogger_ForwardsEventsToNamedPipeServer(int messageCount)
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);
        var eventSource = new TestEventSource();
        var logger = new PipeLogger { Parameters = $"name={pipeName}" };
        var readTask = Task.Run(server.ReadAll);

        try
        {
            logger.Initialize(eventSource);
            RaiseBuildEvents(eventSource, messageCount, includeBuildFinished: true);
            logger.Shutdown();

            server.StopAcceptingConnections();
        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);
            BuildEventAssertions.AssertEvents(events, messageCount, includeBuildFinished: true);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(100000)]
    public async Task AnonymousPipe_TransportsEventsFromChildProcess(int messageCount)
    {
        using var server = new AnonymousPipeLoggerServer();
        var events = SubscribeAnyEvents(server);

        var exitCode = await RunClientProcessAsync(server, server.GetClientHandle(), messageCount, includeBuildFinished: true).ConfigureAwait(false);

        Assert.AreEqual(0, exitCode);
        BuildEventAssertions.AssertEvents(events, messageCount, includeBuildFinished: true);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(100000)]
    public async Task NamedPipe_TransportsEventsFromChildProcess(int messageCount)
    {
        var pipeName = CreatePipeName();
        using var server = new NamedPipeLoggerServer(pipeName);
        var events = SubscribeAnyEvents(server);

        var exitCode = await RunClientProcessAsync(server, $"name={pipeName}", messageCount, includeBuildFinished: true).ConfigureAwait(false);

        Assert.AreEqual(0, exitCode);
        BuildEventAssertions.AssertEvents(events, messageCount, includeBuildFinished: true);
    }

    private static string CreatePipeName() => PipeLoggerServer.CreateUniquePipeName("xenoatom-");

    private static List<BuildEventArgs> SubscribeAnyEvents(EventArgsDispatcher server)
    {
        var events = new List<BuildEventArgs>();
        server.AnyEventRaised += (_, e) => events.Add(e);
        return events;
    }

    private static void RaiseBuildEvents(TestEventSource eventSource, int messageCount, bool includeBuildFinished)
    {
        eventSource.RaiseAnyEvent(new BuildStartedEventArgs("Testing", "help"));
        for (var index = 0; index < messageCount; index++)
        {
            eventSource.RaiseAnyEvent(new BuildMessageEventArgs($"Testing {index}", "help", "sender", MessageImportance.Normal));
        }

        if (includeBuildFinished)
        {
            eventSource.RaiseAnyEvent(new BuildFinishedEventArgs("Finished", "help", true));
        }
    }

    private static async Task WaitForReadAllAsync(Task readTask, IPipeLoggerServer server)
    {
        try
        {
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            server.Dispose();
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> RunClientProcessAsync(IPipeLoggerServer server, string loggerParameters, int messageCount, bool includeBuildFinished)
    {
        using var process = CreateClientProcess(loggerParameters, messageCount, includeBuildFinished);
        var readTask = Task.Run(server.ReadAll);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start the test client process.");
            }

            WriteLine($"Started process {process.Id}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync().WaitAsync(TestTimeout).ConfigureAwait(false);
            WriteLine($"Exited process {process.Id} with code {process.ExitCode}");
            server.StopAcceptingConnections();
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (TimeoutException)
        {
            server.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private Process CreateClientProcess(string loggerParameters, int messageCount, bool includeBuildFinished)
    {
        var testDirectory = Path.GetDirectoryName(typeof(PipeTransportEndToEndTests).Assembly.Location)
                            ?? throw new InvalidOperationException("Could not locate test assembly directory.");
        var clientDirectory = testDirectory.Replace(
            "XenoAtom.MsBuildPipeLogger.Tests",
            "XenoAtom.MsBuildPipeLogger.Tests.Client",
            StringComparison.Ordinal);
        var clientAssemblyPath = Path.Combine(clientDirectory, "XenoAtom.MsBuildPipeLogger.Tests.Client.dll");

        var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.WorkingDirectory = clientDirectory;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add(clientAssemblyPath);
        process.StartInfo.ArgumentList.Add(loggerParameters);
        process.StartInfo.ArgumentList.Add(messageCount.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add(includeBuildFinished.ToString());
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                WriteLine(e.Data);
            }
        };
        return process;
    }

    private void WriteLine(string message)
    {
        TestContext?.WriteLine(message);
    }

    private sealed class TestEventSource : IEventSource
    {
        public event BuildMessageEventHandler? MessageRaised { add { } remove { } }

        public event BuildErrorEventHandler? ErrorRaised { add { } remove { } }

        public event BuildWarningEventHandler? WarningRaised { add { } remove { } }

        public event BuildStartedEventHandler? BuildStarted { add { } remove { } }

        public event BuildFinishedEventHandler? BuildFinished { add { } remove { } }

        public event ProjectStartedEventHandler? ProjectStarted { add { } remove { } }

        public event ProjectFinishedEventHandler? ProjectFinished { add { } remove { } }

        public event TargetStartedEventHandler? TargetStarted { add { } remove { } }

        public event TargetFinishedEventHandler? TargetFinished { add { } remove { } }

        public event TaskStartedEventHandler? TaskStarted { add { } remove { } }

        public event TaskFinishedEventHandler? TaskFinished { add { } remove { } }

        public event CustomBuildEventHandler? CustomEventRaised { add { } remove { } }

        public event BuildStatusEventHandler? StatusEventRaised { add { } remove { } }

        public event AnyEventHandler? AnyEventRaised;

        public void RaiseAnyEvent(BuildEventArgs eventArgs)
        {
            AnyEventRaised?.Invoke(this, eventArgs);
        }
    }
}
