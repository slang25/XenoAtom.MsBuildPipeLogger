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
server.StopListening();
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
server.StopListening();
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

The logger connects on `Initialize` and disconnects on `Shutdown`, and MSBuild runs that lifecycle once per build submission. A command that produces more than one submission therefore produces more than one connection.

`dotnet build -f <tfm>` is the common case: restore has to run without a `TargetFramework` global property, so it cannot share a submission with the build pass. `dotnet msbuild` is a single submission.

`NamedPipeLoggerServer` handles this by default - it keeps one listener open across connections, so every submission is received in order. The consequence is that **the server cannot tell that the last submission has been and gone**, so it keeps waiting. You end the read, once the build process you are observing has exited:

```csharp
var pipeName = NamedPipeLoggerServer.CreatePipeName();
using var server = new NamedPipeLoggerServer(pipeName);
server.AnyEventRaised += (_, e) => Console.WriteLine(e.Message);

var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();

server.StopListening();   // no more submissions are coming
readTask.Wait();          // dispatches what is left, then returns
```

Use `StopListening()` rather than `Dispose()` to end the read. `Dispose()` ends the transport immediately and discards anything the reader has not handed over yet, which silently loses the tail of the build; `StopListening()` stops waiting for new clients and drains what has already arrived first. Dispose afterwards as usual, which the `using` above does.

Re-creating the server in your own code is not a safe substitute for the built-in behavior: the operating system still holds the previous instance of the pipe, so the new listener can fail with `All pipe instances are busy`, leaving a window in which nothing is listening and the build hangs. Keeping the listener open inside the server avoids that window entirely.

### Opting out

If you control the invocation and know it is a single submission, `acceptMultipleConnections: false` restores the simpler lifetime, where the transport ends with the client and `ReadAll()` returns on its own:

```csharp
using var server = new NamedPipeLoggerServer(pipeName, acceptMultipleConnections: false);
var readTask = Task.Run(server.ReadAll);
process.Start();
process.WaitForExit();
readTask.Wait();          // returns without StopListening
```

Only do this when a single submission is guaranteed - `dotnet msbuild`, or `dotnet build --no-restore` with restore driven separately. If a second submission does connect, it will not be served and the build will hang.

### Upgrading from 1.x

Multiple connections used to be unsupported, and `ReadAll()` returned at the first `BuildFinishedEventArgs`. Two changes to be aware of:

- Code that ends with `process.WaitForExit(); readTask.Wait();` now needs `server.StopListening()` between the two lines, or it waits forever. This shows up immediately and every time, not intermittently.
- `ReadAll()` no longer stops at `BuildFinishedEventArgs`, so a consumer that relied on that to bound the read should call `StopListening()` too, or subscribe to `BuildFinished`.

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
