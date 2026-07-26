# XenoAtom.MsBuildPipeLogger User Guide

XenoAtom.MsBuildPipeLogger lets one process run MSBuild with a custom pipe logger while another process receives MSBuild logging events in real time.

Install the single package in the receiving/host process:

```sh
dotnet add package XenoAtom.MsBuildPipeLogger
```

The package contains:

- `XenoAtom.MsBuildPipeLogger.dll` - the receiving/server library used by your host process.
- `XenoAtom.MsBuildPipeLogger/XenoAtom.MsBuildPipeLogger.Logger.dll` - the bundled MSBuild logger copied to an isolated output subfolder.

The logger assembly is intentionally isolated in its own output folder. MSBuild task/logger loading can probe assemblies from the logger directory, so the package keeps unrelated host-process assemblies out of that folder.

## Using Microsoft.Build APIs in the host process

`XenoAtom.MsBuildPipeLogger` dispatches normal MSBuild `BuildEventArgs` types. If your host application also references `Microsoft.Build` assemblies or uses MSBuild APIs directly, load those assemblies through [Microsoft.Build.Locator](https://www.nuget.org/packages/Microsoft.Build.Locator) before touching any `Microsoft.Build` type. This keeps your process aligned with the MSBuild instance installed with the .NET SDK.

Reference MSBuild packages for compile-time only, keep `Microsoft.Build.Locator` as a runtime dependency, and avoid copying `Microsoft.Build*.dll` to your output folder:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Build" Version="18.4.0" ExcludeAssets="runtime" />
  <PackageReference Include="Microsoft.Build.Utilities.Core" Version="18.4.0" ExcludeAssets="runtime" />
  <PackageReference Include="Microsoft.Build.Locator" Version="1.11.2" />
  <PackageReference Include="XenoAtom.MsBuildPipeLogger" Version="..." />
</ItemGroup>
```

Call `MSBuildLocator.RegisterDefaults()` from startup code that does not directly reference MSBuild types, then call the code that creates `NamedPipeLoggerServer`, reads `BuildEventArgs`, or otherwise uses MSBuild:

```csharp
using Microsoft.Build.Locator;

static int Main(string[] args)
{
    MSBuildLocator.RegisterDefaults();
    return Run(args);
}

static int Run(string[] args)
{
    // Safe to use XenoAtom.MsBuildPipeLogger and Microsoft.Build types here.
    return 0;
}
```

The bundled logger assembly is loaded by MSBuild itself from the isolated `XenoAtom.MsBuildPipeLogger/` subfolder; the locator setup only applies to your host process.

## Transports

The bundled `netstandard2.0` logger currently supports:

- Anonymous pipes: pass the server's client handle as the logger parameter.
- Named pipes: pass `name=<pipeName>` and optionally `server=<serverName>`.

Unix domain sockets are not exposed because the logger must remain a single `netstandard2.0` assembly and the required socket endpoint API is not available there without reflection-based workarounds.

## Passing the bundled logger to MSBuild

Use `PipeLoggerServer.GetLoggerSpecification(...)` to build the `type,assembly;parameters` string expected by MSBuild's logger option. This uses `AppContext.BaseDirectory` and the package's isolated logger subfolder.

```csharp
using System.Diagnostics;
using XenoAtom.MsBuildPipeLogger;

var pipeName = PipeLoggerServer.CreateUniquePipeName();
using var server = new NamedPipeLoggerServer(pipeName);
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

using var process = new Process();
process.StartInfo.FileName = "dotnet";
process.StartInfo.ArgumentList.Add("msbuild");
process.StartInfo.ArgumentList.Add("MyProject.csproj");
process.StartInfo.ArgumentList.Add("/nologo");
process.StartInfo.ArgumentList.Add("/nr:false");
var loggerSpecification = PipeLoggerServer.GetLoggerSpecification($"name={pipeName}");
process.StartInfo.ArgumentList.Add($"/logger:{loggerSpecification}");
process.StartInfo.UseShellExecute = false;

var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();
server.StopAcceptingConnections();
readTask.Wait();
```

If you only need the logger assembly path, use `PipeLoggerServer.GetLoggerAssemblyPath()`.

## Build submissions and the server lifetime

MSBuild attaches the logger once per *build submission*: it calls `Initialize` at the start and `Shutdown`
at the end, and the logger connects and disconnects the pipe along with it. A single command can run
several submissions - `dotnet build -f <tfm>` runs restore as its own submission because restore has to
run without a `TargetFramework` global property - so a named pipe server sees several sequential client
connections for one build.

`NamedPipeLoggerServer` therefore keeps listening after a client disconnects, and it starts a fresh event
stream for each connection. Two consequences for the receiving process:

- A client disconnecting no longer means the build is over, so `ReadAll()` does not return there. Call
  `StopAcceptingConnections()` once the process you are observing has exited; events already received are
  still dispatched, and `Read()`/`ReadAll()` return afterwards. Disposing the server also unblocks the
  reader, but it does not wait for buffered events first.
- `BuildFinishedEventArgs` marks the end of a submission, not the end of the build, and you will see one
  per submission.

`AnonymousPipeLoggerServer` serves a single client by construction, so `ReadAll()` returns when the child
process closes its handle.

## Anonymous pipe example

```csharp
using System.Diagnostics;
using XenoAtom.MsBuildPipeLogger;

using var server = new AnonymousPipeLoggerServer();
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

var loggerSpecification = PipeLoggerServer.GetLoggerSpecification(server.GetClientHandle());

using var process = new Process();
process.StartInfo.FileName = "dotnet";
process.StartInfo.ArgumentList.Add("msbuild");
process.StartInfo.ArgumentList.Add("MyProject.csproj");
process.StartInfo.ArgumentList.Add($"/logger:{loggerSpecification}");
process.StartInfo.UseShellExecute = false;

var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();
readTask.Wait();
```

## Named pipe example

```csharp
using System.Diagnostics;
using XenoAtom.MsBuildPipeLogger;

var pipeName = PipeLoggerServer.CreateUniquePipeName();
using var server = new NamedPipeLoggerServer(pipeName);
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

var loggerSpecification = PipeLoggerServer.GetLoggerSpecification($"name={pipeName}");

using var process = new Process();
process.StartInfo.FileName = "dotnet";
process.StartInfo.ArgumentList.Add("build");
process.StartInfo.ArgumentList.Add("MyProject.csproj");
process.StartInfo.ArgumentList.Add($"/logger:{loggerSpecification}");
process.StartInfo.UseShellExecute = false;

var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();
server.StopAcceptingConnections();
readTask.Wait();
```

## Pipe names

On Unix a named pipe is a Unix domain socket at `<temp>/CoreFxPipe_<pipeName>`, and the whole path must fit
in 104 bytes. macOS's per-user `TMPDIR` is long enough that this leaves only around 43 characters for the
name, so an otherwise reasonable `$"myapp-build-{Guid.NewGuid():N}"` fails at construction.

`PipeLoggerServer.CreateUniquePipeName()` mints a unique name that fits on the current platform, shortening
the unique part as needed. `PipeLoggerServer.CreateUniquePipeName(prefix)` does the same with your own
prefix, and `PipeLoggerServer.GetMaximumPipeNameLength()` reports the limit if you generate names yourself.

## Logger parameters

The logger accepts a small semicolon-separated parameter set:

- `<handle>` or `handle=<handle>`: connect to an anonymous pipe client handle.
- `name=<pipeName>`: connect to a local named pipe.
- `name=<pipeName>;server=<serverName>`: connect to a named pipe on a specific server.

`Read()` blocks until an event is available, the transport closes, or cancellation/disposal unblocks the server. `ReadAll()` keeps dispatching events until the server runs out of clients to serve; see [Build submissions and the server lifetime](#build-submissions-and-the-server-lifetime).
