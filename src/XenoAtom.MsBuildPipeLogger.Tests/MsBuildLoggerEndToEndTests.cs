// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Tests;

[TestClass]
[DoNotParallelize]
public class MsBuildLoggerEndToEndTests
{
    private const string ExpectedMessage = "Hello from XenoAtom.MsBuildPipeLogger";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task DotNetMsBuild_LoadsLoggerAndStreamsBuildEvents()
    {
        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"xenoatom-msbuild-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(projectDirectory, "build.proj");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            projectPath,
            "<Project DefaultTargets=\"Build\">" + Environment.NewLine +
            "  <Target Name=\"Build\">" + Environment.NewLine +
            $"    <Message Text=\"{ExpectedMessage}\" Importance=\"High\" />" + Environment.NewLine +
            "  </Target>" + Environment.NewLine +
            "</Project>" + Environment.NewLine);

        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<BuildEventArgs>();
        server.AnyEventRaised += (_, e) => events.Add(e);
        var readTask = Task.Run(server.ReadAll);
        using var process = CreateDotNetMsBuildProcess(projectPath, pipeName);
        var output = new ConcurrentQueue<string>();

        try
        {
            process.OutputDataReceived += (_, e) => EnqueueOutput(output, e.Data);
            process.ErrorDataReceived += (_, e) => EnqueueOutput(output, e.Data);

            Assert.IsTrue(process.Start(), "The dotnet msbuild process should start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync().WaitAsync(TestTimeout).ConfigureAwait(false);
            server.StopListening();
            await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            server.Dispose();
            KillProcess(process);
            throw;
        }
        finally
        {
            TryDeleteDirectory(projectDirectory);
        }

        var processOutput = string.Join(Environment.NewLine, output);
        Assert.AreEqual(0, process.ExitCode, processOutput);
        Assert.IsTrue(events.OfType<BuildStartedEventArgs>().Any(), "MSBuild should raise a build-started event.");
        Assert.IsTrue(events.OfType<BuildFinishedEventArgs>().Any(x => x.Succeeded), "MSBuild should raise a successful build-finished event.");
        Assert.IsTrue(
            events.OfType<BuildMessageEventArgs>().Any(x => x.Message?.Contains(ExpectedMessage, StringComparison.Ordinal) == true),
            $"MSBuild should stream the expected target message. Output:{Environment.NewLine}{processOutput}");
    }

    private static Process CreateDotNetMsBuildProcess(string projectPath, string pipeName)
    {
        var loggerParameters = $"name={pipeName}";
        var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("/nologo");
        process.StartInfo.ArgumentList.Add("/nr:false");
        process.StartInfo.ArgumentList.Add($"/logger:{PipeLoggerServer.GetLoggerSpecification(loggerParameters)}");
        return process;
    }

    private static void EnqueueOutput(ConcurrentQueue<string> output, string? line)
    {
        if (line is not null)
        {
            output.Enqueue(line);
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
