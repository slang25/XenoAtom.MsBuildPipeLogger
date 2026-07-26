# XenoAtom.MsBuildPipeLogger [![ci](https://github.com/XenoAtom/XenoAtom.MsBuildPipeLogger/actions/workflows/ci.yml/badge.svg)](https://github.com/XenoAtom/XenoAtom.MsBuildPipeLogger/actions/workflows/ci.yml)  [![NuGet](https://img.shields.io/nuget/v/XenoAtom.MsBuildPipeLogger.svg)](https://www.nuget.org/packages/XenoAtom.MsBuildPipeLogger/)

<img align="right" width="160px" height="160px" src="https://raw.githubusercontent.com/XenoAtom/XenoAtom.MsBuildPipeLogger/main/img/XenoAtom.MsBuildPipeLogger.png">

A single package that lets one process run MSBuild with a bundled pipe logger while another process receives MSBuild event data in real time.

## ✨ Features

- `XenoAtom.MsBuildPipeLogger`: receiver APIs that deserialize events and dispatch the normal MSBuild logging callbacks.
- Includes the MSBuild logger assembly as isolated copy-to-output content under `XenoAtom.MsBuildPipeLogger/`.
- Helper APIs return the bundled logger path/specification to pass directly to MSBuild.
- Supports anonymous pipes and named pipes from a `netstandard2.0` logger assembly.
- Optionally observes a whole build across several MSBuild submissions, such as `dotnet build -f <tfm>`.
- Nullable-enabled projects with package metadata/readme/icon configured for the publishable package.

## 📦 Package

Install the single package:

```sh
dotnet add package XenoAtom.MsBuildPipeLogger
```

The package copies `XenoAtom.MsBuildPipeLogger.Logger.dll` to an isolated `XenoAtom.MsBuildPipeLogger` output subfolder so MSBuild does not probe unrelated application assemblies from the logger directory.

If your host process references `Microsoft.Build` assemblies directly, use `Microsoft.Build.Locator` and mark direct `Microsoft.Build*` package references with `ExcludeAssets="runtime"` so MSBuild is loaded from the installed .NET SDK. See the user guide for details.

## 📖 User Guide

For more details on how to use XenoAtom.MsBuildPipeLogger, please visit the [user guide](https://github.com/XenoAtom/XenoAtom.MsBuildPipeLogger/blob/main/doc/readme.md).

## 🤗 Author

Alexandre Mutel aka [XenoAtom](https://xoofx.github.io).

## 🪪 License and attribution

This software is released under the [MIT license](https://opensource.org/licenses/MIT), matching the upstream project license so the fork does not require relicensing.

Copyright (c) 2017 Dave Glick<br>
Copyright (c) 2026 Alexandre Mutel

XenoAtom.MsBuildPipeLogger is a fork of Dave Glick's [MsBuildPipeLogger](https://github.com/daveaglick/MsBuildPipeLogger). Special thanks to Dave Glick for creating and sharing the original project.

> [!NOTE]
> This fork packages the receiver APIs and bundled `netstandard2.0` MSBuild logger as a single XenoAtom package, keeps the logger assembly isolated in the output to avoid MSBuild probing unrelated assemblies, and adds helper APIs and nullable-enabled package metadata for current .NET usage.
