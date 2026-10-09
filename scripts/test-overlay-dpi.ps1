[CmdletBinding()]
param([string]$MonitorPath = '', [string]$CaptureDirectory = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path (Join-Path $repoRoot '.build\overlay-dpi-test') (Split-Path (Split-Path $monitorExe -Parent) -Leaf)
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'OverlayDpiSmoke.cs'
$testExe = Join-Path $testDirectory 'OverlayDpiSmoke.exe'
@'
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class OverlayDpiSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Exception nativeError;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static object Get(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    private static object Call(object instance, string name, params object[] args)
    {
        try { return instance.GetType().GetMethod(name).Invoke(instance, args); }
        catch (TargetInvocationException error) { throw new Exception(name + " failed", error.InnerException); }
    }
    private static void Check(bool value, string message)
    {
        if (nativeError != null) throw new Exception("native window callback failed", nativeError);
        if (!value) throw new Exception(message);
    }

    // Deliver the real WinForms DPI message without changing Windows settings.
    private static void SendDpi(Form form, int dpi, int offset = 0)
    {
        Rect rect = new Rect { Left = form.Left + offset, Top = form.Top + offset,
            Right = form.Right + offset, Bottom = form.Bottom + offset };
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Rect)));
        try
        {
            Marshal.StructureToPtr(rect, buffer, false);
            SendMessage(form.Handle, 0x02E0, new IntPtr(dpi | (dpi << 16)), buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Bitmap Render(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        return bitmap;
    }

    private static void Compare(Form overlay, Control preview, Rectangle bounds, string capture)
    {
        using (Bitmap actual = Render(overlay))
        using (Bitmap example = Render(preview))
        {
            int compared = 0, different = 0;
            // Exclude the antialiased outer region and the time-dependent credit
            // progress bar. Text, usage bars and card geometry must match.
            for (int y = 12; y < actual.Height - 12; y++)
            for (int x = 12; x < actual.Width - 12; x++)
            {
                compared++;
                Color a = actual.GetPixel(x, y), b = example.GetPixel(bounds.Left + x, bounds.Top + y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 12) different++;
            }
            Check(different <= Math.Max(4, compared / 200), "preview and overlay rendering differ: " + different + "/" + compared);
            if (!string.IsNullOrEmpty(capture))
            {
                actual.Save(Path.Combine(capture, "overlay-200.png"));
                example.Save(Path.Combine(capture, "preview-200.png"));
            }
        }
    }

    private static void Run(Assembly assembly, string captures)
    {
        Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
        Type overlayType = assembly.GetType("CodexRateMonitorNative.OverlayForm", true);
        Type previewType = assembly.GetType("CodexRateMonitorNative.OverlayPreviewControl", true);
        if (!string.IsNullOrEmpty(captures)) Directory.CreateDirectory(captures);
        int cases = 0, paintings = 0;
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        foreach (string lines in new string[] { "1", "2" })
        foreach (bool credits in new bool[] { false, true })
        foreach (double scale in new double[] { 0.5, 1.0, 2.0 })
        {
            object settings = Activator.CreateInstance(settingsType, true);
            Set(settings, "Language", language);
            Set(settings, "DisplayLines", lines);
            Set(settings, "ShowResetCredits", credits);
            Set(settings, "OverlayMode", "desktop");
            object style = Get(settings, "Style");
            Set(style, "Scale", scale);
            Set(style, "Opacity", 1d);
            using (var overlay = (Form)Activator.CreateInstance(overlayType, new object[] { settings }))
            using (var preview = (Control)Activator.CreateInstance(previewType, true))
            {
                IntPtr handle = overlay.Handle;
                Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(handle), new IntPtr(-4)), "window is not PerMonitorV2 aware");
                int hostDpi = (int)GetDpiForWindow(handle);
                int width = lines == "2" ? 252 : (credits ? 622 : 470);
                int height = lines == "2" ? (credits ? 106 : 78) : 40;
                Set(preview, "Settings", settings);
                object sample = previewType.GetField("sample", PrivateInstance).GetValue(preview);
                object sampleCredits = previewType.GetField("sampleCredits", PrivateInstance).GetValue(preview);
                Call(overlay, "SetSnapshot", sample);
                Call(overlay, "SetResetCredits", credits ? sampleCredits : null);
                Check(overlay.Size == new Size((int)Math.Round(width * (float)(scale * 0.85 * hostDpi / 96d)),
                    (int)Math.Round(height * (float)(scale * 0.85 * hostDpi / 96d))), "startup size ignores monitor DPI: " +
                    overlay.Size + ", dpi=" + Get(overlay, "OverlayDpi") + ", host=" + hostDpi +
                    ", scale=" + scale + ", lines=" + lines + ", credits=" + credits);
                foreach (int dpi in new int[] { 96, 120, 144, 168, 192, 120, 192, 96, 120 })
                {
                    SendDpi(overlay, dpi);
                    Size expected = new Size((int)Math.Round(width * (float)(scale * 0.85 * dpi / 96d)),
                        (int)Math.Round(height * (float)(scale * 0.85 * dpi / 96d)));
                    Check(overlay.Size == expected, "DPI resize drift: " + dpi + ": " + overlay.Size + " vs " + expected);
                    Check((int)Get(overlay, "OverlayDpi") == dpi, "DPI change did not reach the overlay");
                    Check(overlay.Region == null, "outer antialiasing is clipped by a binary window region");
                    Check((GetWindowLong(overlay.Handle, -20) & 0x80000) != 0, "per-pixel layered window style is missing");
                    Set(preview, "TargetDpi", dpi);
                    preview.Size = new Size(expected.Width + 80, expected.Height + 128);
                    Rectangle bounds = (Rectangle)Call(preview, "GetOverlayBounds");
                    Check(bounds.Size == expected, "preview is not 1:1 with the overlay");
                    if (scale == 1d && (dpi == 96 || dpi == 192) && cases % 9 < 5)
                    {
                        Compare(overlay, preview, bounds,
                            language == "zh-CN" && lines == "1" && credits && dpi == 192 ? captures : "");
                        paintings++;
                    }
                    preview.Width = Math.Max(40, expected.Width / 2);
                    Rectangle reduced = (Rectangle)Call(preview, "GetOverlayBounds");
                    Check(reduced.Width < expected.Width && reduced.Right <= preview.Width, "preview cannot shrink into a narrow area");
                    cases++;
                }
                // A style refresh must preserve a user-placed overlay.
                Rectangle screen = Screen.PrimaryScreen.Bounds;
                overlay.Location = new Point(screen.Left + 100, screen.Top + 100);
                Point placed = overlay.Location;
                Call(overlay, "ApplySettings", settings);
                Check(overlay.Location == placed, "style refresh moved the desktop overlay");
                SendDpi(overlay, 144, 20);
                Check(overlay.Location == placed, "DPI change moved a placed overlay within its monitor");
                Check((GetWindowLong(overlay.Handle, -20) & 0x20) == 0, "desktop mode became click-through");
                Set(settings, "OverlayMode", "attach");
                Call(overlay, "ApplySettings", settings);
                Check((GetWindowLong(overlay.Handle, -20) & 0x20) != 0, "attach mode lost click-through");
                using (var target = new Form())
                {
                    // Keep native show/attach checks away from the user's desktop.
                    target.Bounds = new Rectangle(-20000, -20000, 1000, 700);
                    IntPtr ignored = target.Handle;
                    Set(settings, "Position", "bottom-right");
                    Call(overlay, "AttachTo", target.Handle);
                    int inset = (int)Math.Round(12d * (int)Get(overlay, "OverlayDpi") / 96d);
                    Check(overlay.Right == target.Right - inset && overlay.Bottom == target.Bottom - inset,
                        "attach anchor does not scale its margin");
                    Set(settings, "Position", "top");
                    Call(overlay, "AttachTo", target.Handle);
                    Check(overlay.Left == target.Left + (target.Width - overlay.Width) / 2, "top attach is not centered");
                }
            }
        }
        Console.WriteLine("PASS " + cases + " overlay DPI transitions; 3 languages, 1/2 lines, credits, 50/100/200% manual scale, compact baseline, 1:1 preview, shrink, anchors and click-through.");
        Console.WriteLine("PASS " + paintings + " preview/overlay pixel comparisons.");
        // Exercise the updater's actual copy path, keeping runtime settings.
        string fixture = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "update-payload");
        string source = Path.Combine(fixture, "source"), destination = Path.Combine(fixture, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "CodexRateMonitor.exe"), "test payload");
        File.Copy(assembly.Location + ".config", Path.Combine(source, "CodexRateMonitor.exe.config"), true);
        File.WriteAllText(Path.Combine(source, "settings.json"), "package defaults");
        File.WriteAllText(Path.Combine(destination, "settings.json"), "existing settings");
        assembly.GetType("CodexRateMonitorNative.UpdateInstaller", true)
            .GetMethod("CopyPayload", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { source, destination });
        Check(File.ReadAllText(Path.Combine(destination, "CodexRateMonitor.exe.config")) ==
            File.ReadAllText(assembly.Location + ".config"), "updater omitted DPI configuration");
        Check(File.ReadAllText(Path.Combine(destination, "settings.json")) == "existing settings", "updater replaced runtime settings");
        Console.WriteLine("PASS update payload includes DPI configuration and preserves existing settings.");
    }

    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Report native rendering failures to the test runner, without a
            // modal WinForms exception dialog on the user's desktop.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                nativeError = e.Exception;
            };
            Run(Assembly.LoadFrom(args[0]), args.Length > 1 ? args[1] : "");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            for (Exception inner = error.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine(inner.GetType().Name + ": " + inner.Message);
            Environment.ExitCode = 1;
        }
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
if ($LASTEXITCODE -ne 0) { throw 'Overlay test compilation failed.' }
& $testExe $monitorExe $CaptureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Overlay DPI test failed.' }
