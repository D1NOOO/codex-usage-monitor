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

$buildRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.build'))
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
    (Join-Path $repoRoot 'src\Localization.cs'),
    $assemblyInfo
)

$exe = Join-Path $outputPath 'CodexRateMonitor.exe'
& $csc `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:anycpu `
    /win32icon:"$(Join-Path $repoRoot 'assets\app.ico')" `
    /out:"$exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    $sources
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'config\settings.default.json') -Destination (Join-Path $outputPath 'settings.json')
Copy-Item -LiteralPath (Join-Path $repoRoot 'style-examples') -Destination (Join-Path $outputPath 'style-examples') -Recurse
New-Item -ItemType Directory -Path (Join-Path $outputPath 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'assets\logo.png') -Destination (Join-Path $outputPath 'assets\logo.png')
foreach ($readme in @('README.md', 'README.zh-CN.md', 'README.zh-TW.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $readme) -Destination (Join-Path $outputPath $readme)
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repoRoot 'SECURITY.md') -Destination (Join-Path $outputPath 'SECURITY.md')

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
