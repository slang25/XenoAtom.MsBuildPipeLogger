// Copyright (c) Dave Glick, Alexandre Mutel.
// Licensed under the MIT license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace XenoAtom.MsBuildPipeLogger.Tests;

/// <summary>
/// End-to-end coverage against the full-framework <c>MSBuild.exe</c> (from a Visual Studio /
/// Build Tools install) building a legacy, non-SDK-style .NET Framework project. This exercises
/// the real MSBuild binary-log serializer the logger reflects into, on the classic MSBuild engine
/// rather than the .NET SDK one covered by <see cref="MsBuildLoggerEndToEndTests"/>.
///
/// The test only runs on Windows when the <c>FULLFRAMEWORK_MSBUILD</c> environment variable points
/// at an <c>MSBuild.exe</c> (or the directory containing it). CI sets it via microsoft/setup-msbuild.
/// Elsewhere it reports inconclusive so the normal cross-platform test run is unaffected.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FullFrameworkMsBuildEndToEndTests
{
    private const string ExpectedMessage = "Hello from XenoAtom.MsBuildPipeLogger";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(120);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public async Task FullFrameworkMsBuild_BuildsLegacyProjectAndStreamsBuildEvents()
    {
        var msbuildExe = ResolveMsBuildExe();
        if (msbuildExe is null)
        {
            Assert.Inconclusive(
                "Skipped: set FULLFRAMEWORK_MSBUILD to a full-framework MSBuild.exe (or its directory) on Windows to run this test.");
            return;
        }

        var pipeName = $"xenoatom-msbuild-fx-{Guid.NewGuid():N}";
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"xenoatom-msbuild-fx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "Legacy.csproj");
        File.WriteAllText(projectPath, LegacyProjectXml);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Class1.cs"),
            "namespace LegacyLib { public class Class1 { public string Hello() { return \"Hi\"; } } }" + Environment.NewLine);

        TestContext?.WriteLine($"MSBuild.exe: {msbuildExe}");
        TestContext?.WriteLine($"MSBuild -version:{Environment.NewLine}{RunMsBuildVersion(msbuildExe)}");

        using var server = new NamedPipeLoggerServer(pipeName);
        var events = new List<BuildEventArgs>();
        server.AnyEventRaised += (_, e) => events.Add(e);
        var readTask = Task.Run(server.ReadAll);
        using var process = CreateMsBuildProcess(msbuildExe, projectPath, pipeName);
        var output = new ConcurrentQueue<string>();

        try
        {
            process.OutputDataReceived += (_, e) => EnqueueOutput(output, e.Data);
            process.ErrorDataReceived += (_, e) => EnqueueOutput(output, e.Data);

            Assert.IsTrue(process.Start(), "The MSBuild.exe process should start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await Task.WhenAll(readTask, process.WaitForExitAsync()).WaitAsync(TestTimeout).ConfigureAwait(false);
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
        Assert.IsTrue(
            events.OfType<BuildStartedEventArgs>().Any(),
            $"MSBuild should raise a build-started event. Output:{Environment.NewLine}{processOutput}");
        Assert.IsTrue(
            events.OfType<BuildFinishedEventArgs>().Any(x => x.Succeeded),
            $"MSBuild should raise a successful build-finished event. Output:{Environment.NewLine}{processOutput}");
        Assert.IsTrue(
            events.OfType<BuildMessageEventArgs>().Any(x => x.Message?.Contains(ExpectedMessage, StringComparison.Ordinal) == true),
            $"MSBuild should stream the expected target message. Output:{Environment.NewLine}{processOutput}");
    }

    // A classic, non-SDK-style .NET Framework project (old xmlns + Microsoft.CSharp.targets import),
    // with a marker Message so the streamed event set is easy to assert against.
    private const string LegacyProjectXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Project ToolsVersion=\"Current\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <PropertyGroup>\r\n" +
        "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>\r\n" +
        "    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>\r\n" +
        "    <OutputType>Library</OutputType>\r\n" +
        "    <RootNamespace>LegacyLib</RootNamespace>\r\n" +
        "    <AssemblyName>LegacyLib</AssemblyName>\r\n" +
        "    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>\r\n" +
        "    <OutputPath>bin\\</OutputPath>\r\n" +
        "    <IntermediateOutputPath>obj\\</IntermediateOutputPath>\r\n" +
        "  </PropertyGroup>\r\n" +
        "  <ItemGroup>\r\n" +
        "    <Reference Include=\"System\" />\r\n" +
        "  </ItemGroup>\r\n" +
        "  <ItemGroup>\r\n" +
        "    <Compile Include=\"Class1.cs\" />\r\n" +
        "  </ItemGroup>\r\n" +
        "  <Target Name=\"EmitMarker\" BeforeTargets=\"Build\">\r\n" +
        "    <Message Text=\"" + ExpectedMessage + "\" Importance=\"High\" />\r\n" +
        "  </Target>\r\n" +
        "  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />\r\n" +
        "</Project>\r\n";

    private static string? ResolveMsBuildExe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable("FULLFRAMEWORK_MSBUILD");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim().Trim('"');
        if (Directory.Exists(value))
        {
            var candidate = Path.Combine(value, "MSBuild.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        return File.Exists(value) ? value : null;
    }

    private static string RunMsBuildVersion(string msbuildExe)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = msbuildExe;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.ArgumentList.Add("-version");
            process.StartInfo.ArgumentList.Add("-nologo");
            process.Start();
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return text.Trim();
        }
        catch (Exception ex)
        {
            return $"(unable to read MSBuild version: {ex.Message})";
        }
    }

    private static Process CreateMsBuildProcess(string msbuildExe, string projectPath, string pipeName)
    {
        var loggerParameters = $"name={pipeName}";
        var process = new Process();
        process.StartInfo.FileName = msbuildExe;
        process.StartInfo.WorkingDirectory = Path.GetDirectoryName(projectPath)!;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.ArgumentList.Add(projectPath);
        process.StartInfo.ArgumentList.Add("/nologo");
        process.StartInfo.ArgumentList.Add("/nr:false");
        process.StartInfo.ArgumentList.Add("/verbosity:minimal");
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
