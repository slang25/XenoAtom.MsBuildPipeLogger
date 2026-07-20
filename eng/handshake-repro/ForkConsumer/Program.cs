// Runs the CURRENT XenoAtom fork (logger + server) against a given MSBuild.exe and reports whether
// the full event stream is received. The fork's logger writes in the HOST MSBuild's binlog format,
// but the server deserializes with the CONSUMER's Microsoft.Build FileFormatVersion. When those
// differ, the reader desyncs. This harness makes that visible.
//
// Args: <msbuildExe> <label>
// Prints a machine-readable RESULT line; exits 0 (full stream) / 1 (incomplete).

using System.Diagnostics;
using Microsoft.Build.Framework;
using XenoAtom.MsBuildPipeLogger;

const string Marker = "Hello from XenoAtom handshake repro";

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ForkConsumer <msbuildExe> <label>");
    return 2;
}

string msbuildExe = args[0];
string label = args[1];

string pipe = "fork-handshake-" + Guid.NewGuid().ToString("N");
string dir = Path.Combine(Path.GetTempPath(), pipe);
Directory.CreateDirectory(dir);
string proj = Path.Combine(dir, "trivial.proj");
File.WriteAllText(
    proj,
    "<Project DefaultTargets=\"Go\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
    "  <Target Name=\"Go\">\r\n" +
    "    <Message Text=\"" + Marker + "\" Importance=\"High\" />\r\n" +
    "  </Target>\r\n" +
    "</Project>\r\n");

var events = new List<BuildEventArgs>();
string? readerError = null;
int exitCode;
string procOutput;

using (var server = new NamedPipeLoggerServer(pipe))
{
    server.AnyEventRaised += (_, e) => { lock (events) { events.Add(e); } };
    var readTask = Task.Run(() =>
    {
        try { server.ReadAll(); }
        catch (Exception ex) { readerError = ex.ToString(); }
    });

    var psi = new ProcessStartInfo
    {
        FileName = msbuildExe,
        WorkingDirectory = dir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    psi.ArgumentList.Add(proj);
    psi.ArgumentList.Add("/nologo");
    psi.ArgumentList.Add("/nr:false");
    psi.ArgumentList.Add("/verbosity:minimal");
    psi.ArgumentList.Add("/logger:" + PipeLoggerServer.GetLoggerSpecification($"name={pipe}"));

    var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(90_000))
    {
        try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
    }

    exitCode = process.HasExited ? process.ExitCode : -999;
    await Task.WhenAny(readTask, Task.Delay(15_000));
    procOutput = (await stdout) + Environment.NewLine + (await stderr);
}

bool started = events.OfType<BuildStartedEventArgs>().Any();
bool finished = events.OfType<BuildFinishedEventArgs>().Any(x => x.Succeeded);
bool marker = events.OfType<BuildMessageEventArgs>()
    .Any(x => x.Message?.Contains(Marker, StringComparison.Ordinal) == true);
bool pass = exitCode == 0 && started && finished && marker;

Console.WriteLine(
    $"RESULT\t{label}\t{(pass ? "PASS" : "FAIL")}\texit={exitCode}\tevents={events.Count}" +
    $"\tstarted={started}\tfinished={finished}\tmarker={marker}");

if (!pass)
{
    Console.WriteLine("----- MSBuild output (" + label + ") -----");
    Console.WriteLine(procOutput);
    if (readerError != null)
    {
        Console.WriteLine("----- reader exception -----");
        Console.WriteLine(readerError);
    }
}

return pass ? 0 : 1;
