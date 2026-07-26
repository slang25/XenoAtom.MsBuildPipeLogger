// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// The originally reported repro: <c>dotnet build -f &lt;tfm&gt;</c> runs restore and build as two MSBuild
/// submissions, so the logger connects twice from one child process. A server that accepted a single
/// connection left the second client blocked writing into a pipe nobody drained, and the build hung with
/// no error, no timeout and no diagnostic.
/// </summary>
[TestClass]
[DoNotParallelize]
public class DotNetBuildMultiSubmissionTests
{
    private const string TargetFramework = "net10.0";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(180);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DotNetBuild_CompletesAndObservesEverySubmission(bool withTargetFrameworkSelector)
    {
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"xa-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "TinyLib.csproj");
        File.WriteAllText(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">" + Environment.NewLine +
            "  <PropertyGroup>" + Environment.NewLine +
            $"    <TargetFramework>{TargetFramework}</TargetFramework>" + Environment.NewLine +
            "  </PropertyGroup>" + Environment.NewLine +
            "</Project>" + Environment.NewLine);
        File.WriteAllText(Path.Combine(projectDirectory, "C.cs"), "public class C { public int Add(int a, int b) => a + b; }");

        var pipeName = PipeLoggerServer.CreateUniquePipeName("xa-test");
        using var server = new NamedPipeLoggerServer(pipeName);
        var buildFinishedCount = 0;
        var eventCount = 0;
        server.AnyEventRaised += _ => Interlocked.Increment(ref eventCount);
        server.BuildFinished += _ => Interlocked.Increment(ref buildFinishedCount);
        var readTask = Task.Run(server.ReadAll);

        using var process = CreateDotNetBuildProcess(projectPath, pipeName, withTargetFrameworkSelector);
        var output = new ConcurrentQueue<string>();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.OutputDataReceived += (_, e) => EnqueueOutput(output, e.Data);
            process.ErrorDataReceived += (_, e) => EnqueueOutput(output, e.Data);

            Assert.IsTrue(process.Start(), "The dotnet build process should start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Before the fix this never returned when withTargetFrameworkSelector was true.
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
        TestContext?.WriteLine($"Exited in {stopwatch.Elapsed.TotalSeconds:F1}s with {eventCount} events and {buildFinishedCount} submissions.");
        Assert.AreEqual(0, process.ExitCode, processOutput);
        Assert.IsTrue(eventCount > 0, $"Expected build events. Output:{Environment.NewLine}{processOutput}");

        // Restore and build are separate submissions, so a -f build reports BuildFinished twice. Without
        // the accept loop only the first was ever observed.
        var expectedSubmissions = withTargetFrameworkSelector ? 2 : 1;
        Assert.AreEqual(
            expectedSubmissions,
            buildFinishedCount,
            $"Expected {expectedSubmissions} submission(s). Output:{Environment.NewLine}{processOutput}");
    }

    private static Process CreateDotNetBuildProcess(string projectPath, string pipeName, bool withTargetFrameworkSelector)
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
        if (withTargetFrameworkSelector)
        {
            // The only difference between a build that passed and a build that hung.
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add(TargetFramework);
        }

        process.StartInfo.ArgumentList.Add("/nodeReuse:false");
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
