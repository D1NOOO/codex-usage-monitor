[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutputDirectory = '',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'version.txt')).Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use MAJOR.MINOR.PATCH format; received '$Version'."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\CodexRateMonitor'
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $outputPath.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must stay under '$artifactsRoot'."
}

# Keep build intermediates separate from running regression-test executables.
$buildRoot = [IO.Path]::GetFullPath((Join-Path (Join-Path $repoRoot '.build') (Split-Path $outputPath -Leaf)))
# Check before deleting any resources: a running EXE can otherwise leave a
# partially cleared output directory when recursive cleanup reaches it.
$existingExe = Join-Path $outputPath 'CodexRateMonitor.exe'
if (Test-Path -LiteralPath $existingExe) {
    try {
        $outputProbe = [IO.File]::Open($existingExe, [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $outputProbe.Dispose()
    } catch {
        throw "Output executable is in use or cannot be replaced. Use a separate OutputDirectory. Existing files were preserved: '$existingExe'."
    }
}
foreach ($path in @($outputPath, $buildRoot)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path | Out-Null
}

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) {
    throw '.NET Framework 4.8 C# compiler was not found.'
}

$assemblyInfo = Join-Path $buildRoot 'GeneratedAssemblyInfo.cs'
@"
using System.Reflection;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyTitle("Codex Rate Monitor")]
[assembly: AssemblyDescription("Display Codex 5-hour and 7-day usage on Windows.")]
[assembly: AssemblyProduct("Codex Rate Monitor")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
namespace CodexRateMonitorNative
{
    internal static class BuildVersion
    {
        public const string Value = "$Version";
    }
}
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8

$sources = @(
    (Join-Path $repoRoot 'src\CodexRateMonitor.cs'),
    (Join-Path $repoRoot 'src\AppearanceSettingsForm.cs'),
    (Join-Path $repoRoot 'src\DpiAwareDialog.cs'),
    (Join-Path $repoRoot 'src\OverlayRenderer.cs'),
    (Join-Path $repoRoot 'src\UsageRefreshScheduler.cs'),
    (Join-Path $repoRoot 'src\DiagnosticLog.cs'),
    (Join-Path $repoRoot 'src\Localization.cs'),
    (Join-Path $repoRoot 'src\UpdateChecker.cs'),
    (Join-Path $repoRoot 'src\UpdateForm.cs'),
    $assemblyInfo
)

$exe = Join-Path $outputPath 'CodexRateMonitor.exe'
& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /win32icon:"$(Join-Path $repoRoot 'assets\app.ico')" `
    /win32manifest:"$(Join-Path $repoRoot 'src\app.manifest')" `
    /out:"$exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    $sources
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'src\app.config') -Destination ($exe + '.config')
Copy-Item -LiteralPath (Join-Path $repoRoot 'config\settings.default.json') -Destination (Join-Path $outputPath 'settings.json')
Copy-Item -LiteralPath (Join-Path $repoRoot 'style-examples') -Destination (Join-Path $outputPath 'style-examples') -Recurse
New-Item -ItemType Directory -Path (Join-Path $outputPath 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets\logo.png') -Destination (Join-Path $outputPath 'assets\logo.png')
foreach ($readme in @('README.md', 'README.zh-CN.md', 'README.zh-TW.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $readme) -Destination (Join-Path $outputPath $readme)
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md') -Destination (Join-Path $outputPath 'SECURITY.md')
New-Item -ItemType Directory -Path (Join-Path $outputPath 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\usage-refresh.md') -Destination (Join-Path $outputPath 'docs\usage-refresh.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\issue-6-dpi-regression.md') -Destination (Join-Path $outputPath 'docs\issue-6-dpi-regression.md')
foreach ($screenshot in @(
    'appearance-settings-reset-credits-zh-cn.png',
    'overlay-oneline-credits-zh-cn.png',
    'overlay-multirow-credits-zh-cn.png',
    'tray-tooltip-zh-cn.png'
)) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $repoRoot 'docs') $screenshot) `
        -Destination (Join-Path (Join-Path $outputPath 'docs') $screenshot)
}

$hash = Get-FileHash -LiteralPath $exe -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  CodexRateMonitor.exe" |
    Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ASCII

if ($Package) {
    $zipPath = Join-Path $artifactsRoot "CodexRateMonitor-$Version-windows-x64.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path $outputPath -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    "$($zipHash.Hash.ToLowerInvariant())  $(Split-Path $zipPath -Leaf)" |
        Set-Content -LiteralPath (Join-Path $artifactsRoot 'SHA256SUMS.txt') -Encoding ASCII
}

Write-Host "Built Codex Rate Monitor $Version"
Write-Host "Output: $outputPath"
