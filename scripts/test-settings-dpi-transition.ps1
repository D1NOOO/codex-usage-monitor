[CmdletBinding()]
param([string]$MonitorPath = '', [string]$CaptureDirectory = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path $repoRoot '.build\settings-dpi-transition-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'SettingsDpiTransition.cs'
$testExe = Join-Path $testDirectory 'SettingsDpiTransition.exe'
@'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class SettingsDpiTransition
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void ChangeDpi(Form form, int dpi)
    {
        float factor = dpi / (float)form.DeviceDpi;
        Rect rect = new Rect { Left = form.Left, Top = form.Top,
            Right = form.Left + (int)Math.Round(form.Width * factor),
            Bottom = form.Top + (int)Math.Round(form.Height * factor) };
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Rect)));
        try
        {
            Marshal.StructureToPtr(rect, buffer, false);
            SendMessage(form.Handle, 0x02E0, new IntPtr(dpi | (dpi << 16)), buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
        MethodInfo setPreview = form.GetType().GetMethod("SetPreviewDpi");
        if (setPreview != null) setPreview.Invoke(form, new object[] { dpi });
        // Run the delayed layout after native child DPI notifications finish.
        Application.DoEvents();
        form.PerformLayout();
        Check(form.DeviceDpi == dpi, "DPI message did not reach the dialog");
        Check(form.MinimumSize.IsEmpty, "DPI transition left a stale minimum window size");
    }

    private static void Configure(Form form)
    {
        Type type = form.GetType();
        ((NumericUpDown)type.GetField("scale", PrivateInstance).GetValue(form)).Value = 135;
        ((NumericUpDown)type.GetField("fontSize", PrivateInstance).GetValue(form)).Value = 18;
        ((ComboBox)type.GetField("fontFamily", PrivateInstance).GetValue(form)).Text = "Segoe UI";
        ((RadioButton)type.GetField("twoLinesRadio", PrivateInstance).GetValue(form)).Checked = true;
        ((CheckBox)type.GetField("showResetCredits", PrivateInstance).GetValue(form)).Checked = false;
    }

    private static void VerifySettings(Form form)
    {
        Type type = form.GetType();
        Check(((NumericUpDown)type.GetField("scale", PrivateInstance).GetValue(form)).Value == 135, "DPI transition changed manual scale");
        Check(((NumericUpDown)type.GetField("fontSize", PrivateInstance).GetValue(form)).Value == 18, "DPI transition changed font size");
        Check(((ComboBox)type.GetField("fontFamily", PrivateInstance).GetValue(form)).Text == "Segoe UI", "DPI transition changed selected font");
        Check(((RadioButton)type.GetField("twoLinesRadio", PrivateInstance).GetValue(form)).Checked, "DPI transition changed display lines");
        Check(!((CheckBox)type.GetField("showResetCredits", PrivateInstance).GetValue(form)).Checked, "DPI transition changed credits toggle");
    }

    private sealed class Geometry
    {
        private readonly Control control;
        private readonly Size size, minimum;
        private readonly Padding padding, margin;
        private readonly float font;
        public Geometry(Control control)
        {
            this.control = control;
            size = control.Size;
            minimum = control.MinimumSize;
            padding = control.Padding;
            margin = control.Margin;
            font = control.Font.Size;
        }
        public void Compare(Control current)
        {
            string name = current.GetType().Name + " '" + current.Text + "'";
            Check(current.GetType() == control.GetType(), "control tree changed across DPI transitions");
            Check(Math.Abs(current.Width - size.Width) <= 2 && Math.Abs(current.Height - size.Height) <= 2,
                name + " did not return to initial size: " + current.Size + " vs " + size);
            Check(current.Padding == padding && current.Margin == margin && current.MinimumSize == minimum,
                name + " did not restore baseline spacing");
            Check(Math.Abs(current.Font.Size - font) < 0.001f, name + " font did not return to initial size");
        }
    }

    private static void Capture(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path);
        }
    }

    private static void Run(Assembly assembly, string captures)
    {
        Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
        Type formType = assembly.GetType("CodexRateMonitorNative.AppearanceSettingsForm", true);
        Type i18n = assembly.GetType("CodexRateMonitorNative.I18n", true);
        if (!string.IsNullOrEmpty(captures)) Directory.CreateDirectory(captures);
        int cases = 0;
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        foreach (string mode in new string[] { "desktop", "attach" })
        {
            i18n.GetMethod("SetLanguage").Invoke(null, new object[] { language });
            object settings = Activator.CreateInstance(settingsType, true);
            settingsType.GetProperty("Language").SetValue(settings, language, null);
            settingsType.GetProperty("OverlayMode").SetValue(settings, mode, null);
            using (var form = (Form)Activator.CreateInstance(formType, new object[] { settings, null, null, null }))
            {
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                int hostDpi = form.DeviceDpi;
                Configure(form);
                ChangeDpi(form, hostDpi);
                Control[] controls = Descendants(form).ToArray();
                Geometry[] baseline = controls.Select(delegate(Control control) { return new Geometry(control); }).ToArray();
                Size coldSize = form.Size;
                if (language == "zh-CN" && mode == "desktop" && !string.IsNullOrEmpty(captures))
                    Capture(form, Path.Combine(captures, "settings-cold.png"));
                foreach (int dpi in new int[] { 192, 120, 168, 120, 192, 120, 96, hostDpi })
                {
                    ChangeDpi(form, dpi);
                    Check(Math.Abs(form.ClientSize.Width - 820d * dpi / 96d) <= 1,
                        "dialog width retained the previous DPI: " + form.ClientSize);
                    using (var expectedFont = new Font(form.Font.FontFamily,
                        9.5f * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel))
                        Check(Math.Abs(form.Font.GetHeight(hostDpi) - expectedFont.GetHeight(hostDpi)) < 0.01,
                            "dialog's rendered font retained the previous DPI");
                    VerifySettings(form);
                    if (dpi == hostDpi)
                    {
                        Check(Math.Abs(form.Width - coldSize.Width) <= 2 && Math.Abs(form.Height - coldSize.Height) <= 2,
                            "dialog did not return to its cold-open size");
                        Control[] current = Descendants(form).ToArray();
                        Check(current.Length == baseline.Length, "DPI transition changed the control tree");
                        for (int i = 0; i < baseline.Length; i++) baseline[i].Compare(current[i]);
                    }
                    cases++;
                }
                if (language == "zh-CN" && mode == "desktop" && !string.IsNullOrEmpty(captures))
                    Capture(form, Path.Combine(captures, "settings-after-200-to-125.png"));
                // An independent new window must have identical geometry at the
                // same DPI, even after this process has handled higher DPI.
                using (var fresh = (Form)Activator.CreateInstance(formType, new object[] { settings, null, null, null }))
                {
                    fresh.ShowInTaskbar = false;
                    fresh.Opacity = 0;
                    fresh.Show();
                    Configure(fresh);
                    ChangeDpi(fresh, hostDpi);
                    Control[] reopened = Descendants(fresh).ToArray();
                    Check(reopened.Length == baseline.Length, "reopening changed the control tree");
                    for (int i = 0; i < baseline.Length; i++) baseline[i].Compare(reopened[i]);
                }
            }
        }
        Console.WriteLine("PASS " + cases + " live dialog DPI transitions, including 200% -> 125%, 3 languages and 2 modes.");
        Console.WriteLine("PASS window/control sizes, fonts, spacing and edited inputs match an independently reopened window; no accumulated scaling.");
    }

    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Run(Assembly.LoadFrom(args[0]), args.Length > 1 ? args[1] : "");
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error.Message); Environment.ExitCode = 1; }
        finally { SetForegroundWindow(foreground); }
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework compiler was not found.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\app.config') -Destination ($testExe + '.config') -Force
& $csc /nologo /target:exe /win32manifest:"$(Join-Path $repoRoot 'src\app.manifest')" /out:"$testExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $testSource
if ($LASTEXITCODE -ne 0) { throw 'DPI transition test compilation failed.' }
& $testExe $monitorExe $CaptureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Settings DPI transition test failed.' }
