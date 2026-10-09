[CmdletBinding()]
param([string]$MonitorPath = '', [string]$CaptureDirectory = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path (Join-Path $repoRoot '.build\overlay-surface-test') (Split-Path (Split-Path $monitorExe -Parent) -Leaf)
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'OverlaySurfaceSmoke.cs'
$testExe = Join-Path $testDirectory 'OverlaySurfaceSmoke.exe'
@'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class OverlaySurfaceSmoke
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private static Exception nativeError;
    private static object Get(object instance, string name) { return instance.GetType().GetProperty(name).GetValue(instance, null); }
    private static void Set(object instance, string name, object value) { instance.GetType().GetProperty(name).SetValue(instance, value, null); }
    private static object Call(object instance, string name, params object[] args) { return instance.GetType().GetMethod(name).Invoke(instance, args); }
    private static void Check(bool value, string message)
    {
        if (nativeError != null) throw new Exception("native window callback failed", nativeError);
        if (!value) throw new Exception(message);
    }
    private static Bitmap Render(Type renderer, object settings, object sample, object credits, int dpi)
    {
        return (Bitmap)renderer.GetMethod("CreateBitmap").Invoke(null, new object[] { settings, sample, credits, null, dpi });
    }
    private static void CheckAlpha(Bitmap bitmap, bool rounded)
    {
        Check(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A == 255, "interior lost opacity");
        if (rounded) Check(bitmap.GetPixel(0, 0).A == 0, "rounded corner is not transparent");
        int partial = 0, extent = Math.Min(bitmap.Height / 2, 45);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[extent * 4];
            foreach (bool bottom in new bool[] { false, true })
            foreach (bool right in new bool[] { false, true })
            for (int y = 0; y < extent; y++)
            {
                int rowIndex = bottom ? bitmap.Height - 1 - y : y;
                int x = right ? bitmap.Width - extent : 0;
                Marshal.Copy(IntPtr.Add(data.Scan0, rowIndex * data.Stride + x * 4), row, 0, row.Length);
                for (int offset = 0; offset < row.Length; offset += 4)
                {
                    byte alpha = row[offset + 3];
                    Check(row[offset] <= alpha && row[offset + 1] <= alpha && row[offset + 2] <= alpha,
                        "edge RGB is not premultiplied; desktop will show a halo");
                    if (alpha > 0 && alpha < 255) partial++;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        if (rounded) Check(partial >= 8, "rounded edge has no smooth alpha transition");
    }

    private sealed class BackdropForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
        }
    }
    private static void Pump()
    {
        Application.DoEvents();
        Thread.Sleep(80);
        Application.DoEvents();
        Check(true, "message pump failed");
    }
    private static void CheckComposition(Bitmap surface, Bitmap screen, Point origin, Color background, double opacity)
    {
        int compared = 0, partial = 0;
        for (int y = 0; y < Math.Min(35, surface.Height / 2); y++)
        for (int x = 0; x < Math.Min(35, surface.Width / 2); x++)
        {
            Color source = surface.GetPixel(x, y);
            // Check the outer edge, avoiding font rasterization and time-dependent content.
            if (source.A != 0 && source.A != 255 && partial++ > 30) continue;
            if (source.A == 255 && x > 2 && y > 2) continue;
            Color actual = screen.GetPixel(origin.X + x, origin.Y + y);
            double alpha = source.A / 255d * Math.Round(opacity * 255d) / 255d;
            int red = (int)Math.Round(source.R * alpha + background.R * (1 - alpha));
            int green = (int)Math.Round(source.G * alpha + background.G * (1 - alpha));
            int blue = (int)Math.Round(source.B * alpha + background.B * (1 - alpha));
            Check(Math.Abs(actual.R - red) <= 4 && Math.Abs(actual.G - green) <= 4 && Math.Abs(actual.B - blue) <= 4,
                "desktop alpha composition differs at " + x + "," + y + ": " + actual + " vs " + red + "," + green + "," + blue);
            compared++;
        }
        Check(compared > 30, "not enough edge pixels compared");
    }

    private static void Run(Assembly assembly, string captures)
    {
        Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
        Type renderer = assembly.GetType("CodexRateMonitorNative.OverlayRenderer", true);
        Type previewType = assembly.GetType("CodexRateMonitorNative.OverlayPreviewControl", true);
        Type overlayType = assembly.GetType("CodexRateMonitorNative.OverlayForm", true);
        object settings = Activator.CreateInstance(settingsType, true);
        Set(settings, "Language", "zh-CN");
        Set(settings, "OverlayMode", "desktop");
        object style = Get(settings, "Style");
        using (var preview = (Control)Activator.CreateInstance(previewType, true))
        {
            BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            object sample = previewType.GetField("sample", fields).GetValue(preview);
            object credits = previewType.GetField("sampleCredits", fields).GetValue(preview);
            int cases = 0;
            foreach (int dpi in new int[] { 96, 120, 144, 168, 192 })
            foreach (double scale in new double[] { 0.5, 0.75, 1d, 2d })
            foreach (double radius in new double[] { 0d, 9d, 20d })
            foreach (string lines in new string[] { "1", "2" })
            foreach (bool showCredits in new bool[] { false, true })
            {
                Set(style, "Scale", scale);
                Set(style, "CornerRadius", radius);
                Set(settings, "DisplayLines", lines);
                Set(settings, "ShowResetCredits", showCredits);
                using (Bitmap bitmap = Render(renderer, settings, sample, credits, dpi))
                    CheckAlpha(bitmap, radius > 0);
                cases++;
            }
            Console.WriteLine("PASS " + cases + " alpha surfaces: all four corners, premultiplied edges, DPI, 50/75/100/200% size, square/rounded corners and both layouts.");

            Set(style, "Scale", 1d);
            Set(style, "CornerRadius", 9d);
            Set(settings, "DisplayLines", "1");
            Set(settings, "ShowResetCredits", true);
            if (!string.IsNullOrEmpty(captures)) Directory.CreateDirectory(captures);
            int compositions = 0;
            foreach (bool darkSurface in new bool[] { false, true })
            foreach (double opacity in new double[] { 0.5, 0.97, 1d })
            {
                Set(style, "Background", darkSurface ? "#252A33" : "#F7F7F5");
                Set(style, "CardBackground", darkSurface ? "#343B48" : "#FFFFFF");
                Set(style, "Border", darkSurface ? "#606978" : "#D8D8D4");
                Set(style, "Text", darkSurface ? "#FFFFFF" : "#252525");
                Set(style, "Opacity", opacity);
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Point origin = new Point(area.Left + 45, area.Top + 45);
                Set(settings, "DesktopX", origin.X);
                Set(settings, "DesktopY", origin.Y);
                using (var backdrop = new BackdropForm())
                using (var overlay = (Form)Activator.CreateInstance(overlayType, new object[] { settings }))
                {
                    IntPtr handle = overlay.Handle;
                    Call(overlay, "SetSnapshot", sample);
                    Call(overlay, "SetResetCredits", credits);
                    int dpi = (int)Get(overlay, "OverlayDpi");
                    using (Bitmap bitmap = Render(renderer, settings, sample, credits, dpi))
                    {
                        backdrop.FormBorderStyle = FormBorderStyle.None;
                        backdrop.ShowInTaskbar = false;
                        backdrop.StartPosition = FormStartPosition.Manual;
                        backdrop.BackColor = darkSurface ? Color.FromArgb(240, 241, 244) : Color.FromArgb(28, 34, 42);
                        backdrop.Bounds = new Rectangle(area.Left + 15, area.Top + 15, bitmap.Width + 60, bitmap.Height + 60);
                        backdrop.TopMost = true;
                        backdrop.Show();
                        Call(overlay, "ShowDesktop");
                        Pump();
                        Check(overlay.Size == bitmap.Size, "native window size differs from alpha surface");
                        Check(overlay.Location == origin, "show changed saved location");
                        using (var screenshot = new Bitmap(backdrop.Width, backdrop.Height))
                        {
                            using (Graphics graphics = Graphics.FromImage(screenshot))
                                graphics.CopyFromScreen(backdrop.Location, Point.Empty, backdrop.Size);
                            CheckComposition(bitmap, screenshot, new Point(30, 30), backdrop.BackColor, opacity);
                            if (!string.IsNullOrEmpty(captures) && opacity == 0.97)
                                screenshot.Save(Path.Combine(captures, darkSurface ? "rounded-dark-desktop.png" : "rounded-light-desktop.png"));
                        }
                    }
                }
                compositions++;
            }
            Console.WriteLine("PASS " + compositions + " native desktop compositions: light/dark surfaces at 50/97/100% opacity.");
        }
    }
    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { nativeError = e.Exception; };
        try { Run(Assembly.LoadFrom(args[0]), args.Length > 1 ? args[1] : ""); }
        catch (Exception error) { Console.Error.WriteLine("FAIL " + error); Environment.ExitCode = 1; }
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
if ($LASTEXITCODE -ne 0) { throw 'Overlay surface test compilation failed.' }
& $testExe $monitorExe $CaptureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Overlay surface test failed.' }
