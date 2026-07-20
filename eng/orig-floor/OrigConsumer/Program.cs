// Runs the ORIGINAL MsBuildPipeLogger.Logger (Dave Glick, 1.1.6) inside a given MSBuild.exe,
// building a trivial project, and checks whether events actually stream back through the
// original Server. Because the original logger bundles its own serializer (rather than
// reflecting into the host's BuildEventArgsWriter like the XenoAtom fork), this is how we find
// how OLD an MSBuild it still works on.
//
// Args: <msbuildExe> <loggerDll> <label>
// Prints a single machine-readable RESULT line and exits 0 (PASS) / 1 (FAIL).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using MsBuildPipeLogger;

internal static class Program
{
    private const string Marker = "Hello from MsBuildPipeLogger floor probe";

    private static int Main(string[] argv)
    {
        if (argv.Length < 3)
        {
            Console.Error.WriteLine("usage: OrigConsumer <msbuildExe> <loggerDll> <label>");
            return 2;
        }

        string msbuildExe = argv[0];
        string loggerDll = argv[1];
        string label = argv[2];

        string pipe = "orig-floor-" + Guid.NewGuid().ToString("N");
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
        string readerError = null;
        int exitCode;
        string procOutput;

        using (var server = new NamedPipeLoggerServer(pipe))
        {
            server.AnyEventRaised += (s, e) => { lock (events) { events.Add(e); } };
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
                Arguments = "\"" + proj + "\" /nologo /nr:false /v:m " +
                            "\"/logger:MsBuildPipeLogger.PipeLogger," + loggerDll + ";name=" + pipe + "\""
            };

            var process = Process.Start(psi);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90_000))
            {
                try { process.Kill(); } catch { /* ignore */ }
            }

            exitCode = process.HasExited ? process.ExitCode : -999;
            Task.WaitAll(new Task[] { readTask }, 15_000);
            procOutput = stdout.Result + Environment.NewLine + stderr.Result;
        }

        bool started = events.OfType<BuildStartedEventArgs>().Any();
        bool finished = events.OfType<BuildFinishedEventArgs>().Any(x => x.Succeeded);
        bool marker = events.OfType<BuildMessageEventArgs>()
            .Any(x => x.Message != null && x.Message.IndexOf(Marker, StringComparison.Ordinal) >= 0);
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
    }
}
