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

var pipeName = NamedPipeLoggerServer.CreatePipeName("build-events-");
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
readTask.Wait();
```

If you only need the logger assembly path, use `PipeLoggerServer.GetLoggerAssemblyPath()`.

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

var pipeName = NamedPipeLoggerServer.CreatePipeName("build-events-");
using var server = new NamedPipeLoggerServer(pipeName);
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

var loggerSpecification = PipeLoggerServer.GetLoggerSpecification($"name={pipeName}");

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

## Logger parameters

The logger accepts a small semicolon-separated parameter set:

- `<handle>` or `handle=<handle>`: connect to an anonymous pipe client handle.
- `name=<pipeName>`: connect to a local named pipe.
- `name=<pipeName>;server=<serverName>`: connect to a named pipe on a specific server.

`Read()` blocks until an event is available, the transport closes, or cancellation/disposal unblocks the server. `ReadAll()` keeps dispatching events until the transport closes.

`BuildFinishedEventArgs` marks the end of an MSBuild *submission*, not the end of the transport, so it does not stop either method. A build can raise several of them - see [Builds with more than one MSBuild submission](#builds-with-more-than-one-msbuild-submission).

## Builds with more than one MSBuild submission

A `NamedPipeLoggerServer` serves one client connection by default, and the logger connects on `Initialize` and disconnects on `Shutdown`. MSBuild runs that lifecycle once per build submission, so a command that produces more than one submission produces more than one connection.

`dotnet build -f <tfm>` is the common case: restore has to run without a `TargetFramework` global property, so it cannot share a submission with the build pass. With a single-connection server the second connection is never served, and the child process blocks writing into a pipe nobody is draining - with no error and no timeout.

Pass `acceptMultipleConnections: true` so the server keeps the same listener open across connections:

```csharp
var pipeName = NamedPipeLoggerServer.CreatePipeName();
using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: true);
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();

// The server is still listening for another submission, so end it once the build process has exited.
server.Dispose();
readTask.Wait();
```

Two things change when this is enabled:

- `Read()` and `ReadAll()` no longer return when a client disconnects, because another one may still arrive. Dispose the server, or trigger its cancellation token, once the build process you are observing has exited.
- Each connection is decoded independently, so events are dispatched in order across submissions.

Re-creating the server in your own code is not a safe substitute: the operating system still holds the previous instance of the pipe, so the new listener can fail with `All pipe instances are busy`, leaving a window in which nothing is listening and the build hangs. Keeping the listener open inside the server avoids that window entirely.

If you would rather keep a single connection, guarantee a single submission - use `dotnet msbuild`, or `dotnet build --no-restore` with restore driven separately.

## Pipe names

On Unix a named pipe is a domain socket at `$TMPDIR/CoreFxPipe_<name>`, and the whole path has to fit in `sockaddr_un`. macOS uses a long per-user `TMPDIR`, which can leave as little as about 40 characters for the name itself, so a name that looks unremarkable can fail:

```
System.ArgumentException: The pipe name 'myapp-build-<guid>' is 45 characters long but must be
at most 43 characters on this platform. Use NamedPipeLoggerServer.CreatePipeName() to create a
name that always fits.
```

Use the helpers rather than composing a name by hand:

```csharp
var pipeName = NamedPipeLoggerServer.CreatePipeName();           // always fits
var prefixed = NamedPipeLoggerServer.CreatePipeName("myapp-");   // fits, and stays recognizable
var limit = NamedPipeLoggerServer.MaxPipeNameLength;             // the platform limit
```
