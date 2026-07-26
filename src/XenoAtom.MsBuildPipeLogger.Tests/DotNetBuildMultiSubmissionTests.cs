// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// <c>dotnet build -f &lt;tfm&gt;</c> runs restore and build as two separate MSBuild submissions, and the
/// logger connects once per submission. A server that serves a single connection leaves the second
/// one unread, which blocks the child process writing into it with no error and no timeout.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DotNetBuildMultiSubmissionTests
{
    private const string TargetFramework = "net10.0";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(180);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task DotNetBuildWithTargetFramework_CompletesAndStreamsEverySubmission()
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), NamedPipeLoggerServer.CreatePipeName("xa-build-"));
        var projectPath = Path.Combine(projectDirectory, "TinyLib.csproj");
        CreateProject(projectDirectory, projectPath);

        var pipeName = NamedPipeLoggerServer.CreatePipeName("xa-");
        using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true);
        var buildStartedCount = 0;
        var buildFinishedCount = 0;
        server.BuildStarted += (_, _) => Interlocked.Increment(ref buildStartedCount);
        server.BuildFinished += (_, _) => Interlocked.Increment(ref buildFinishedCount);
        var readTask = Task.Run(server.ReadAll);

        using var process = CreateBuildProcess(projectPath, pipeName);
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => EnqueueOutput(output, e.Data);
        process.ErrorDataReceived += (_, e) => EnqueueOutput(output, e.Data);

        try
        {
            Assert.IsTrue(process.Start(), "The dotnet build process should start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync().WaitAsync(TestTimeout).ConfigureAwait(false);
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

        // The reader keeps listening for another submission, so the consumer ends it once the build
        // process it was observing has exited.
        server.Dispose();
        await readTask.WaitAsync(TestTimeout).ConfigureAwait(false);

        var processOutput = string.Join(Environment.NewLine, output);
        TestContext?.WriteLine(processOutput);
        Assert.AreEqual(0, process.ExitCode, processOutput);
        Assert.IsTrue(
            Volatile.Read(ref buildStartedCount) >= 2,
            $"Expected a build-started event per submission but saw {Volatile.Read(ref buildStartedCount)}.");
        Assert.IsTrue(
            Volatile.Read(ref buildFinishedCount) >= 2,
            $"Expected a build-finished event per submission but saw {Volatile.Read(ref buildFinishedCount)}.");
    }

    private static void CreateProject(string projectDirectory, string projectPath)
    {
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">" + Environment.NewLine +
            "  <PropertyGroup>" + Environment.NewLine +
            $"    <TargetFramework>{TargetFramework}</TargetFramework>" + Environment.NewLine +
            "  </PropertyGroup>" + Environment.NewLine +
            "</Project>" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(projectDirectory, "C.cs"),
            "public class C { public int Add(int a, int b) => a + b; }" + Environment.NewLine);

        // Keep the build isolated from any Directory.Build.props above the temporary directory.
        File.WriteAllText(Path.Combine(projectDirectory, "Directory.Build.props"), "<Project />" + Environment.NewLine);
        File.WriteAllText(Path.Combine(projectDirectory, "Directory.Build.targets"), "<Project />" + Environment.NewLine);
    }

    private static Process CreateBuildProcess(string projectPath, string pipeName)
    {
        var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add("build");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("Debug");

        // The target framework selector is what splits restore and build into two submissions.
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add(TargetFramework);
        process.StartInfo.ArgumentList.Add("/nologo");
        process.StartInfo.ArgumentList.Add("/nr:false");
        process.StartInfo.ArgumentList.Add($"/logger:{PipeLoggerServer.GetLoggerSpecification($"name={pipeName}")}");
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
