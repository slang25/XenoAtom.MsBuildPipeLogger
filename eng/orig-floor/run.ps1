<#
Finds the MSBuild support floor of the ORIGINAL MsBuildPipeLogger.Logger (Dave Glick, 1.1.6)
by actually running it inside a matrix of real full-framework MSBuild.exe versions.

Each old MSBuild toolset is materialized from the Microsoft.Build.Runtime NuGet package (no VS
install needed). The runner's own current MSBuild is probed too, as the high-end datapoint.

Params:
  -LoggerDll     path to the original MsBuildPipeLogger.Logger.dll (netstandard2.0)
  -ConsumerExe   path to the built OrigConsumer.exe
  -CurrentMsBuild (optional) path to the runner's MSBuild.exe (from setup-msbuild)
#>
param(
    [Parameter(Mandatory = $true)][string]$LoggerDll,
    [Parameter(Mandatory = $true)][string]$ConsumerExe,
    [string]$CurrentMsBuild = ""
)

$ErrorActionPreference = 'Continue'
$repoRoot = Resolve-Path "$PSScriptRoot/../.."
$mbrProj  = Join-Path $PSScriptRoot 'mbr/mbr.csproj'
$toolsets = Join-Path $repoRoot 'toolsets'
New-Item -ItemType Directory -Force $toolsets | Out-Null

# Oldest first. 15.1 is BELOW the XenoAtom fork's 15.3 reflection floor.
$versions = @('15.1.548', '15.3.409', '15.5.180', '15.9.20', '16.11.0', '17.14.8')

$results = New-Object System.Collections.Generic.List[object]

function Probe([string]$label, [string]$msbuildExe) {
    if (-not (Test-Path $msbuildExe)) {
        Write-Host "::warning::[$label] MSBuild.exe not found ($msbuildExe)"
        $results.Add([pscustomobject]@{ Label = $label; Verdict = 'NO-TOOLSET'; Detail = '' })
        return
    }
    Write-Host "----- probing $label : $msbuildExe -----"
    $out = & $ConsumerExe $msbuildExe $LoggerDll $label 2>&1 | Out-String
    Write-Host $out
    $line = ($out -split "`n" | Where-Object { $_ -match '^RESULT\b' } | Select-Object -First 1)
    $verdict = if ($line -match 'PASS') { 'PASS' } elseif ($line -match 'FAIL') { 'FAIL' } else { 'NO-RESULT' }
    $results.Add([pscustomobject]@{ Label = $label; Verdict = $verdict; Detail = ($line -replace "`r", '') })
}

foreach ($v in $versions) {
    $outDir = Join-Path $toolsets $v
    Write-Host "::group::Assemble MSBuild $v toolset"
    dotnet build $mbrProj -c Release -p:MbrVersion=$v -o $outDir --nologo -v quiet 2>&1 | Out-String | Write-Host
    Write-Host "::endgroup::"
    $exe = Get-ChildItem $outDir -Recurse -Filter 'MSBuild.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $exe) {
        Write-Host "::warning::[$v] no MSBuild.exe produced by Microsoft.Build.Runtime $v"
        $results.Add([pscustomobject]@{ Label = $v; Verdict = 'NO-TOOLSET'; Detail = '' })
        continue
    }
    Probe $v $exe.FullName
}

if ($CurrentMsBuild -ne "") {
    $cur = $CurrentMsBuild
    if (Test-Path $cur -PathType Container) { $cur = Join-Path $cur 'MSBuild.exe' }
    Probe 'current (runner)' $cur
}

Write-Host ""
Write-Host "================ ORIGINAL LOGGER (1.1.6) SUPPORT MATRIX ================"
$results | Format-Table -AutoSize | Out-String | Write-Host

$passed = $results | Where-Object { $_.Verdict -eq 'PASS' -and $_.Label -match '^\d' }
$floor = if ($passed) { ($passed | Select-Object -First 1).Label } else { '(none passed)' }
Write-Host "Observed floor for the ORIGINAL logger: $floor"

# Step summary
if ($env:GITHUB_STEP_SUMMARY) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("### Original MsBuildPipeLogger.Logger 1.1.6 — MSBuild support matrix")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Each row ran the original (self-contained) logger inside a real full-framework MSBuild.exe.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| MSBuild | Verdict |")
    [void]$sb.AppendLine("|---|---|")
    foreach ($r in $results) {
        $icon = switch ($r.Verdict) { 'PASS' { '✅ PASS' } 'FAIL' { '❌ FAIL' } default { "⚠️ $($r.Verdict)" } }
        [void]$sb.AppendLine("| $($r.Label) | $icon |")
    }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("**Observed floor: $floor** (vs. the XenoAtom fork's 15.3).")
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $sb.ToString()
}

# Investigation job: always succeed; the matrix itself is the result.
exit 0
