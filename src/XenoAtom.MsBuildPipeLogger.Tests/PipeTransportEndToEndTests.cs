// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Framework;

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

        await WaitForReadAllAsync(readTask, server).ConfigureAwait(false);
        BuildEventAssertions.AssertEvents(events, messageCount);
    }

    // ReadAll no longer stops at the first BuildFinished, because that marks the end of a submission
    // rather than the end of the transport. See NamedPipeMultipleSubmissionTests for the current contract.

    [TestMethod]
    public async Task Server_DispatchesTypedEvents()
    {
        var pipeName = CreatePipeName();
        var buildStartedCount = 0;
        var messageCount = 0;
        var buildFinishedCount = 0;
        using var server = new NamedPipeLoggerServer(pipeName);
        server.BuildStarted += _ => buildStartedCount++;
        server.MessageRaised += _ => messageCount++;
        server.BuildFinished += _ => buildFinishedCount++;
        var readTask = Task.Run(server.ReadAll);

        using (var writer = ParameterParser.GetPipeFromParameters($"name={pipeName}"))
        {
            BuildEventAssertions.WriteEvents(writer, messageCount: 3, includeBuildFinished: true);
        }

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

    private static string CreatePipeName() => PipeLoggerServer.CreateUniquePipeName("xa-test");

    private static List<PipeBuildEventArgs> SubscribeAnyEvents(PipeEventDispatcher server)
    {
        var events = new List<PipeBuildEventArgs>();
        server.AnyEventRaised += e => events.Add(e);
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
        // Every caller has finished writing by this point. A server that accepts multiple connections
        // would otherwise keep waiting for the next submission.
        server.StopListening();

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

            // The observed process has exited, so no further submission can connect.
            server.StopListening();
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
