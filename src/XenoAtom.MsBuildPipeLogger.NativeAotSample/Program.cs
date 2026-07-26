// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

// This sample proves that the XenoAtom.MsBuildPipeLogger *receiver* works from a
// Native AOT-compiled host. The receiver has no dependency on any Microsoft.Build
// assembly and uses no reflection, so it needs neither Microsoft.Build.Locator nor a
// JIT to load anything on demand -- everything it touches is statically reachable.
//
// The host below never loads MSBuild itself. It starts a named-pipe server, spawns
// `dotnet msbuild` with the bundled logger, and receives strongly-typed events over
// the pipe as XenoAtom-owned PipeBuildEventArgs types.
using System.Diagnostics;
using XenoAtom.MsBuildPipeLogger;

const string ExpectedMessage = "Hello from a Native AOT host";

Console.WriteLine("XenoAtom.MsBuildPipeLogger - Native AOT sample");
Console.WriteLine("Host binary: " + Environment.ProcessPath);
Console.WriteLine("Receiver loaded with no Microsoft.Build dependency and no MSBuildLocator.");
Console.WriteLine();

// Write a tiny project that emits a single high-importance message.
var projectDirectory = Path.Combine(Path.GetTempPath(), "xenoatom-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(projectDirectory);
var projectPath = Path.Combine(projectDirectory, "build.proj");
File.WriteAllText(
    projectPath,
    "<Project DefaultTargets=\"Build\">" + Environment.NewLine +
    "  <Target Name=\"Build\">" + Environment.NewLine +
    "    <Message Text=\"" + ExpectedMessage + "\" Importance=\"High\" />" + Environment.NewLine +
    "  </Target>" + Environment.NewLine +
    "</Project>" + Environment.NewLine);

var pipeName = PipeLoggerServer.CreateUniquePipeName("xenoatom-aot");

var sawBuildStarted = false;
var buildSucceeded = false;
var sawExpectedMessage = false;

try
{
    using var server = new NamedPipeLoggerServer(pipeName);
    server.BuildStarted += _ => sawBuildStarted = true;
    server.BuildFinished += e => buildSucceeded = e.Succeeded;
    server.MessageRaised += e =>
    {
        if (e.Message is not null && e.Message.Contains(ExpectedMessage, StringComparison.Ordinal))
        {
            sawExpectedMessage = true;
        }
    };
    server.AnyEventRaised += e => Console.WriteLine("  event: " + e.GetType().Name + " -> " + e.Message);

    // The server keeps accepting connections (one MSBuild build can run several submissions), so ReadAll
    // returns only once StopListening() is called after the observed process has exited.
    var readTask = Task.Run(server.ReadAll);

    using var process = new Process();
    process.StartInfo.FileName = "dotnet";
    process.StartInfo.UseShellExecute = false;
    process.StartInfo.CreateNoWindow = true;
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;
    process.StartInfo.ArgumentList.Add("msbuild");
    process.StartInfo.ArgumentList.Add(projectPath);
    process.StartInfo.ArgumentList.Add("/nologo");
    process.StartInfo.ArgumentList.Add("/nr:false");
    process.StartInfo.ArgumentList.Add("/logger:" + PipeLoggerServer.GetLoggerSpecification("name=" + pipeName));

    process.Start();
    var buildOutput = process.StandardOutput.ReadToEnd();
    var buildError = process.StandardError.ReadToEnd();
    process.WaitForExit();

    // The build process has exited, so stop accepting further connections and let ReadAll drain and return.
    server.StopListening();

    if (!readTask.Wait(TimeSpan.FromSeconds(60)))
    {
        Console.Error.WriteLine("Timed out waiting for build events.");
        return 1;
    }

    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine("dotnet msbuild exited with code " + process.ExitCode + ".");
        Console.Error.WriteLine(buildOutput);
        Console.Error.WriteLine(buildError);
        return 1;
    }
}
finally
{
    try
    {
        Directory.Delete(projectDirectory, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

Console.WriteLine();
Console.WriteLine("BuildStarted received : " + sawBuildStarted);
Console.WriteLine("BuildFinished success : " + buildSucceeded);
Console.WriteLine("Target message piped  : " + sawExpectedMessage);
Console.WriteLine();

var success = sawBuildStarted && buildSucceeded && sawExpectedMessage;
Console.WriteLine(success
    ? "SUCCESS: MSBuild events were received over the pipe by a Native AOT host."
    : "FAILURE: expected build events were not received.");
return success ? 0 : 1;
