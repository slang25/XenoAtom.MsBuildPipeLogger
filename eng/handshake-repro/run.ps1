<#
Demonstrates (and, after the fix, verifies) the fork's binlog format-version coupling.

The fork's logger writes in the HOST MSBuild's binlog format; the server currently reads with the
CONSUMER's Microsoft.Build FileFormatVersion (25 for Microsoft.Build 18.4). We run the fork against
two full-framework hosts materialized from Microsoft.Build.Runtime:

  * 16.11.0  -> BinaryLogger.FileFormatVersion 13  (MISMATCH vs consumer v25)  -> expected broken today
  * 17.14.8  -> BinaryLogger.FileFormatVersion 25  (matches consumer v25)      -> control, should work

Param: -ConsumerExe path to the built ForkConsumer.exe
#>
param([Parameter(Mandatory = $true)][string]$ConsumerExe)

$ErrorActionPreference = 'Continue'
$repoRoot = Resolve-Path "$PSScriptRoot/../.."
$mbrProj  = Join-Path $repoRoot 'eng/orig-floor/mbr/mbr.csproj'
$toolsets = Join-Path $repoRoot 'toolsets-fork'
New-Item -ItemType Directory -Force $toolsets | Out-Null

function Add-BindingRedirects([string]$configPath, [string]$binDir) {
    if (-not (Test-Path $configPath)) { return }
    $ns = 'urn:schemas-microsoft-com:asm.v1'
    [xml]$cfg = Get-Content $configPath
    $runtime = $cfg.configuration.runtime
    if (-not $runtime) { $runtime = $cfg.CreateElement('runtime'); [void]$cfg.configuration.AppendChild($runtime) }
    $binding = $runtime.assemblyBinding
    if (-not $binding) { $binding = $cfg.CreateElement('assemblyBinding', $ns); [void]$runtime.AppendChild($binding) }
    $names = @(
        'System.Threading.Tasks.Dataflow', 'System.Collections.Immutable', 'System.Reflection.Metadata',
        'System.Runtime.CompilerServices.Unsafe', 'System.Memory', 'System.Numerics.Vectors', 'System.Buffers',
        'System.Runtime.InteropServices.RuntimeInformation'
    )
    foreach ($name in $names) {
        $dll = Get-ChildItem $binDir -Filter "$name.dll" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $dll) { continue }
        $an = [Reflection.AssemblyName]::GetAssemblyName($dll.FullName)
        $tokenBytes = $an.GetPublicKeyToken()
        if (-not $tokenBytes -or $tokenBytes.Length -eq 0) { continue }
        $token = ($tokenBytes | ForEach-Object { $_.ToString('x2') }) -join ''
        $dep = $cfg.CreateElement('dependentAssembly', $ns)
        $ident = $cfg.CreateElement('assemblyIdentity', $ns)
        $ident.SetAttribute('name', $name); $ident.SetAttribute('publicKeyToken', $token); $ident.SetAttribute('culture', 'neutral')
        $redir = $cfg.CreateElement('bindingRedirect', $ns)
        $redir.SetAttribute('oldVersion', '0.0.0.0-99.9.9.9'); $redir.SetAttribute('newVersion', $an.Version.ToString())
        [void]$dep.AppendChild($ident); [void]$dep.AppendChild($redir); [void]$binding.AppendChild($dep)
    }
    $cfg.Save($configPath)
}

$hosts = @(
    [pscustomobject]@{ Version = '16.11.0'; Fmt = 13; Kind = 'MISMATCH (host v13 vs consumer v25)' },
    [pscustomobject]@{ Version = '17.14.8'; Fmt = 25; Kind = 'match (host v25 == consumer v25)' }
)

$results = New-Object System.Collections.Generic.List[object]
foreach ($h in $hosts) {
    $v = $h.Version
    $outDir = Join-Path $toolsets $v
    Write-Host "::group::Assemble MSBuild $v toolset (FileFormatVersion $($h.Fmt))"
    dotnet publish $mbrProj -c Release -p:MbrVersion=$v -o $outDir --nologo -v quiet 2>&1 | Out-String | Write-Host
    Write-Host "::endgroup::"
    $exe = Get-ChildItem $outDir -Recurse -Filter 'MSBuild.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $exe) {
        Write-Host "::warning::[$v] no MSBuild.exe produced"
        $results.Add([pscustomobject]@{ Host = $v; Fmt = $h.Fmt; Kind = $h.Kind; Verdict = 'NO-TOOLSET' })
        continue
    }
    Add-BindingRedirects "$($exe.FullName).config" $exe.Directory.FullName

    Write-Host "----- fork consumer (Microsoft.Build 18.4, reader v25) vs host $v (writer v$($h.Fmt)) -----"
    $out = & $ConsumerExe $exe.FullName "$v (v$($h.Fmt))" 2>&1 | Out-String
    Write-Host $out
    $verdict = if ($out -match 'RESULT[^\n]*PASS') { 'PASS' } elseif ($out -match 'RESULT[^\n]*FAIL') { 'FAIL' } else { 'NO-RESULT' }
    $results.Add([pscustomobject]@{ Host = $v; Fmt = $h.Fmt; Kind = $h.Kind; Verdict = $verdict })
}

Write-Host ""
Write-Host "================ FORK vs HOST FORMAT-VERSION MATRIX ================"
$results | Format-Table -AutoSize | Out-String | Write-Host

if ($env:GITHUB_STEP_SUMMARY) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("### Fork format-version coupling")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Consumer: fork server + Microsoft.Build 18.4 (binlog reader **v25**).")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Host MSBuild | Writer format | vs consumer | Result |")
    [void]$sb.AppendLine("|---|---|---|---|")
    foreach ($r in $results) {
        $icon = switch ($r.Verdict) { 'PASS' { 'full stream' } 'FAIL' { 'events dropped' } default { $r.Verdict } }
        [void]$sb.AppendLine("| $($r.Host) | v$($r.Fmt) | $($r.Kind) | $icon |")
    }
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $sb.ToString()
}

exit 0
