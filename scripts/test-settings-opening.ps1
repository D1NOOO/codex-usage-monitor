[CmdletBinding()]
param(
    [string]$MonitorPath = '',
    [string]$BaselineMonitorPath = '',
    [double]$MaximumWarmOpenMilliseconds = 0,
    [double]$MaximumBaselineRatio = 0.75
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
if ($MaximumWarmOpenMilliseconds -lt 0 -or $MaximumBaselineRatio -le 0) {
    throw 'The timing budget must be nonnegative and the baseline ratio must be positive.'
}

$testDirectory = Join-Path $repoRoot '.build\settings-opening-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'SettingsOpening.cs'
$testExe = Join-Path $testDirectory 'SettingsOpening.exe'
@'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class SettingsOpening
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Assembly assembly = Assembly.LoadFrom(args[0]);
            Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
            Type formType = assembly.GetType("CodexRateMonitorNative.AppearanceSettingsForm", true);
            Type i18n = assembly.GetType("CodexRateMonitorNative.I18n", true);
            foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
            foreach (string mode in new string[] { "desktop", "attach" })
            for (int iteration = 0; iteration < 4; iteration++)
            {
                i18n.GetMethod("SetLanguage").Invoke(null, new object[] { language });
                object settings = Activator.CreateInstance(settingsType, true);
                settingsType.GetProperty("Language").SetValue(settings, language, null);
                settingsType.GetProperty("OverlayMode").SetValue(settings, mode, null);
                var watch = Stopwatch.StartNew();
                using (var form = (Form)Activator.CreateInstance(formType,
                    new object[] { settings, null, null, null }))
                {
                    double construct = watch.Elapsed.TotalMilliseconds;
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    formType.GetMethod("SetUsageSnapshot").Invoke(form, new object[] { null });
                    int dpi = (int)i18n.Assembly.GetType("CodexRateMonitorNative.DisplayDpi", true)
                        .GetMethod("ForWindow").Invoke(null, new object[] { IntPtr.Zero });
                    formType.GetMethod("SetPreviewDpi").Invoke(form, new object[] { dpi });
                    form.Show();
                    double show = watch.Elapsed.TotalMilliseconds;
                    // Include a complete first paint, not just a visible HWND.
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    double total = watch.Elapsed.TotalMilliseconds;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "SAMPLE {0} {1} {2} {3:F1} {4:F1} {5:F1} {6:F1}",
                        language, mode, iteration, construct, show - construct, total - show, total));
                    form.Close();
                }
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            Environment.ExitCode = 1;
        }
        finally { SetForegroundWindow(foreground); }
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\app.config') -Destination ($testExe + '.config') -Force
& $compiler /nologo /target:exe /win32manifest:"$(Join-Path $repoRoot 'src\app.manifest')" /out:"$testExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $testSource
if ($LASTEXITCODE -ne 0) { throw 'Opening timing test compilation failed.' }

function Measure-Opening([string]$Executable, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Executable)) { throw "Monitor executable not found: $Executable" }
    $samples = @(& $testExe $Executable)
    if ($LASTEXITCODE -ne 0) { throw "$Label opening timing test failed." }
    $measurements = @(
        foreach ($sample in $samples) {
            if ($sample -notmatch '^SAMPLE (\S+) (\S+) (\d+) ([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)$') {
                throw "Unexpected timing output: $sample"
            }
            [pscustomobject]@{
                Language = $Matches[1]
                Mode = $Matches[2]
                Iteration = [int]$Matches[3]
                Milliseconds = [double]::Parse($Matches[7], [Globalization.CultureInfo]::InvariantCulture)
            }
        }
    )
    if ($measurements.Count -ne 24) { throw "$Label did not complete all 24 opening samples." }
    $warm = @($measurements | Where-Object { $_.Iteration -gt 0 } | Sort-Object Milliseconds)
    $median = ($warm[8].Milliseconds + $warm[9].Milliseconds) / 2
    $worst = $warm[-1].Milliseconds
    Write-Host ("{0}: first open {1:N0} ms; warm median {2:N0} ms; warm max {3:N0} ms (3 languages, 2 modes, first paint included)." -f $Label, $measurements[0].Milliseconds, $median, $worst)
    [pscustomobject]@{ Median = $median; Worst = $worst }
}

# Each binary runs in its own process so equal assembly identities cannot cause
# the baseline and candidate to resolve to the same loaded code.
$baseline = if (-not [string]::IsNullOrWhiteSpace($BaselineMonitorPath)) {
    Measure-Opening ([IO.Path]::GetFullPath($BaselineMonitorPath)) 'Baseline'
}
$candidate = Measure-Opening $monitorExe 'Candidate'
if ($MaximumWarmOpenMilliseconds -gt 0 -and $candidate.Worst -gt $MaximumWarmOpenMilliseconds) {
    throw "Warm opening exceeded the $MaximumWarmOpenMilliseconds ms budget."
}
if ($null -ne $baseline) {
    $ratio = $candidate.Median / $baseline.Median
    Write-Host ('Warm median improvement: {0:P0}.' -f (1 - $ratio))
    if ($ratio -gt $MaximumBaselineRatio) { throw "Candidate/baseline ratio $ratio exceeds $MaximumBaselineRatio." }
}
Write-Host 'PASS settings opening timing.'
