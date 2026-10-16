[CmdletBinding()]
param([string]$MonitorPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path (Join-Path $repoRoot '.build\fullscreen-overlay-test') (Split-Path (Split-Path $monitorExe -Parent) -Leaf)
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'FullscreenOverlayRegression.cs'
$testExe = Join-Path $testDirectory 'FullscreenOverlayRegression.exe'
@'
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class FullscreenOverlayRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly assembly;
    private static MethodInfo detect, geometry;
    private static int assertions;
    private static Exception nativeError;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect value);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point value);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom;
        public override string ToString() { return Left + "," + Top + "," + Right + "," + Bottom; } }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,
        int attribute, ref int value, int size);

    private sealed class Fixture : Form
    {
        private readonly int extended;
        public Fixture(Rectangle bounds, bool framed = false, int extended = 0)
        {
            this.extended = extended;
            Text = "Fullscreen adaptation regression";
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = framed ? FormBorderStyle.Sizable : FormBorderStyle.None;
            Bounds = bounds;
            ShowInTaskbar = false;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams value = base.CreateParams; value.ExStyle |= extended; return value; }
        }
        public void Raise()
        {
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40);
            Pump();
        }
    }

    private static void Check(bool value, string message)
    {
        if (nativeError != null) throw new Exception("native callback failed", nativeError);
        if (!value) throw new Exception(message);
        assertions++;
    }
    private static object New(string name)
    {
        return Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative." + name, true), true);
    }
    private static object Call(object target, string method, params object[] args)
    {
        return target.GetType().GetMethod(method, Instance).Invoke(target, args);
    }
    private static void Set(object target, string property, object value)
    {
        target.GetType().GetProperty(property, Instance).SetValue(target, value, null);
    }
    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, Instance).GetValue(target);
    }
    private static void Field(object target, string name, object value)
    {
        target.GetType().GetField(name, Instance).SetValue(target, value);
    }
    private static bool Full(Form overlay)
    {
        return (bool)detect.Invoke(null, new object[] { overlay.Handle });
    }
    private static bool Fits(Rectangle frame, Rectangle client, Rectangle screen, bool maximized = false)
    {
        return (bool)geometry.Invoke(null, new object[] { frame, client, screen, maximized });
    }
    private static void Pump()
    {
        Application.DoEvents();
        DwmFlush();
    }
    private static void Restore(Form overlay, string reason)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            Call(overlay, "EnsureDesktopVisible");
            Pump();
            if (IsWindowVisible(overlay.Handle)) return;
            Thread.Sleep(25);
        } while (timer.ElapsedMilliseconds < 1500);
        Check(false, "overlay did not restore: " + reason);
    }

    private static void GeometryAndDebounce()
    {
        foreach (Rectangle monitor in new Rectangle[] {
            new Rectangle(0, 0, 1920, 1080), new Rectangle(-2560, -400, 2560, 1440),
            new Rectangle(1920, 200, 3840, 2160), new Rectangle(-1200, -1920, 1200, 1920) })
        {
            Check(Fits(monitor, monitor, monitor), "fullscreen geometry rejected: " + monitor);
            Rectangle work = Rectangle.FromLTRB(monitor.Left, monitor.Top, monitor.Right, monitor.Bottom - 48);
            Check(!Fits(work, work, monitor), "working area treated as fullscreen");
            Rectangle titled = Rectangle.FromLTRB(monitor.Left, monitor.Top + 30, monitor.Right, monitor.Bottom);
            Check(!Fits(monitor, titled, monitor), "maximized title bar treated as fullscreen");
            Check(!Fits(monitor, monitor, monitor, true), "framed maximized/auto-hide taskbar treated as fullscreen");
            Rectangle offByOne = Rectangle.FromLTRB(monitor.Left + 1, monitor.Top + 1, monitor.Right - 1, monitor.Bottom - 1);
            Check(Fits(offByOne, offByOne, monitor), "rounding tolerance rejected");
            Check(!Fits(monitor, monitor, new Rectangle(monitor.Right, monitor.Top, monitor.Width, monitor.Height)),
                "other monitor geometry treated as fullscreen");
        }
        Check(!Fits(Rectangle.Empty, Rectangle.Empty, Rectangle.Empty), "empty geometry accepted");
        object state = New("FullscreenVisibility");
        long frequency = Stopwatch.Frequency;
        Call(state, "Observe", true, 0L);
        Check((bool)state.GetType().GetProperty("Suppressed").GetValue(state, null), "fullscreen did not suppress immediately");
        Call(state, "Observe", false, frequency / 4);
        Check((bool)state.GetType().GetProperty("Suppressed").GetValue(state, null), "brief transition restored too early");
        Call(state, "Observe", true, frequency / 4);
        Call(state, "Observe", false, frequency / 2);
        Check((bool)state.GetType().GetProperty("Suppressed").GetValue(state, null), "fullscreen reentry lost debounce");
        Call(state, "Observe", false, frequency);
        Check(!(bool)state.GetType().GetProperty("Suppressed").GetValue(state, null), "stable exit did not restore");
        Call(state, "Observe", true, frequency);
        Call(state, "Reset");
        Check(!(bool)state.GetType().GetProperty("Suppressed").GetValue(state, null), "mode change did not reset suppression");
    }

    private static void TickWhileHidden(object settings, Form overlay)
    {
        // Exercise the real tick beyond its visibility branch without starting
        // an app-server, reading credentials or performing network requests.
        object context = FormatterServices.GetUninitializedObject(assembly.GetType("CodexRateMonitorNative.MonitorContext", true));
        object client = New("AppServerClient");
        object snapshot = New("RateSnapshot");
        Set(snapshot, "Primary", New("WindowUsage"));
        using (var tray = new NotifyIcon())
        {
            try
            {
                Field(context, "overlay", overlay);
                Field(context, "settings", settings);
                Field(context, "trayIcon", tray);
                Field(context, "appServer", client);
                Field(context, "refreshScheduler", New("UsageRefreshScheduler"));
                Field(context, "nextWindowCheck", double.MaxValue);
                Field(context, "nextStartAttempt", double.MaxValue);
                Field(context, "lastSnapshot", snapshot);
                Field(context, "lastSnapshotAt", DateTime.Now);
                Call(context, "OnTimerTick", null, EventArgs.Empty);
                Check(!IsWindowVisible(overlay.Handle), "background tick showed suppressed overlay");
                Check((double)Field(context, "nextUsageHintAt") > 0 && tray.Text.Length > 0,
                    "hidden overlay stopped tick processing after the visibility branch");
                Call(overlay, "SetSnapshot", snapshot);
                Check(object.ReferenceEquals(Field(overlay, "snapshot"), snapshot), "hidden overlay stopped accepting quota updates");
                Check(!IsWindowVisible(overlay.Handle), "quota update showed suppressed overlay");
            }
            finally { ((IDisposable)client).Dispose(); }
        }
    }

    private static void NativeTransitions(Screen screen)
    {
        Rectangle bounds = screen.Bounds;
        Rectangle normalBounds = new Rectangle(bounds.Left + 40, bounds.Top + 40,
            Math.Min(600, bounds.Width / 2), Math.Min(400, bounds.Height / 2));
        object settings = New("MonitorSettings");
        Set(settings, "ShowResetCredits", false);
        Set(settings, "DesktopX", bounds.Left + 80);
        Set(settings, "DesktopY", bounds.Top + 80);
        using (var normal = new Fixture(normalBounds, true))
        using (var fullscreen = new Fixture(bounds))
        using (var overlay = (Form)Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.OverlayForm", true), new object[] { settings }))
        {
            normal.Raise();
            Call(overlay, "EnsureDesktopVisible");
            Check(IsWindowVisible(overlay.Handle), "normal desktop did not show overlay");
            // Keep an unsaved location, including when saved defaults are zero.
            Set(settings, "DesktopX", 0);
            Set(settings, "DesktopY", 0);
            overlay.Location = new Point(bounds.Left + 100, bounds.Top + 120);
            Point original = overlay.Location;
            fullscreen.Raise();
            Rect frameRect, clientRect;
            Point clientOrigin = Point.Empty;
            int frameResult = DwmGetWindowAttribute(fullscreen.Handle, 9, out frameRect, Marshal.SizeOf(typeof(Rect)));
            GetClientRect(fullscreen.Handle, out clientRect);
            ClientToScreen(fullscreen.Handle, ref clientOrigin);
            if (!Full(overlay))
            {
                int shown = 0;
                for (IntPtr candidate = GetTopWindow(IntPtr.Zero); candidate != IntPtr.Zero && shown < 8; candidate = GetWindow(candidate, 2))
                {
                    if (!(bool)assembly.GetType("CodexRateMonitorNative.FullscreenDetector").GetMethod("IsApplicationWindow", Static).Invoke(null, new object[] { candidate })) continue;
                    Rect diagnosticRect;
                    DwmGetWindowAttribute(candidate, 9, out diagnosticRect, Marshal.SizeOf(typeof(Rect)));
                    Console.WriteLine("Candidate " + candidate + " fixture=" + (candidate == fullscreen.Handle) + " frame=" + diagnosticRect +
                        " monitor=" + MonitorFromWindow(candidate, 0) + " owner=" + GetWindow(candidate, 4));
                    shown++;
                }
                Console.WriteLine("Overlay monitor=" + MonitorFromWindow(overlay.Handle, 2));
            }
            Check(Full(overlay), "borderless fullscreen not detected: bounds=" + fullscreen.Bounds +
                " screen=" + bounds + " style=" + GetWindowLong(fullscreen.Handle, -16).ToString("X") +
                " extended=" + GetWindowLong(fullscreen.Handle, -20).ToString("X") +
                " eligible=" + assembly.GetType("CodexRateMonitorNative.FullscreenDetector").GetMethod("IsApplicationWindow", Static).Invoke(null, new object[] { fullscreen.Handle }) +
                " owner=" + GetWindow(fullscreen.Handle, 4) + " frame=" + frameRect + " hr=" + frameResult +
                " client=" + clientRect + " origin=" + clientOrigin + " overlay=" + overlay.Bounds);
            IntPtr foreground = GetForegroundWindow();
            Call(overlay, "EnsureDesktopVisible");
            Check(!IsWindowVisible(overlay.Handle), "fullscreen did not hide overlay");
            for (int i = 0; i < 5; i++)
            {
                Call(overlay, "ShowDesktop");
                Call(overlay, "ReassertTopmost");
                Call(overlay, "ApplySettings", settings);
                Call(overlay, "SetStatus", "updated while fullscreen");
                Call(overlay, "ClearSnapshot", "updated while fullscreen");
                Check(!IsWindowVisible(overlay.Handle), "show/topmost/settings/update path resurrected overlay");
            }
            TickWhileHidden(settings, overlay);
            Check(GetForegroundWindow() == foreground, "overlay changed foreground focus");
            Check(overlay.Location == original, "hiding or updating moved overlay");

            using (var tool = new Fixture(bounds, false, 0x80))
            using (var passive = new Fixture(bounds, false, 0x08000000))
            using (var dialog = new Fixture(normalBounds, true))
            {
                tool.Raise();
                Check(Full(overlay), "tool window obscured fullscreen detection");
                passive.Raise();
                Check(Full(overlay), "no-activate window obscured fullscreen detection");
                dialog.Owner = fullscreen;
                dialog.Raise();
                Check(Full(overlay), "owned dialog lost fullscreen owner state");
            }

            using (var dialog = new Fixture(normalBounds, true))
            {
                dialog.Owner = normal;
                dialog.Raise();
                Check(!Full(overlay), "ordinary application's owned dialog inherited unrelated fullscreen state");
            }

            normal.Raise();
            Check(!Full(overlay), "covered/background fullscreen suppressed normal desktop work");
            Call(overlay, "EnsureDesktopVisible");
            Check(!IsWindowVisible(overlay.Handle), "restore debounce did not hold");
            Restore(overlay, "normal application above fullscreen");
            Check(overlay.Location == original, "restoration lost unsaved location");
            Check(GetForegroundWindow() == foreground, "restoration activated overlay");

            fullscreen.Raise();
            Call(overlay, "EnsureDesktopVisible");
            ShowWindow(fullscreen.Handle, 7);
            Pump();
            Check(!Full(overlay), "minimized fullscreen suppressed overlay");
            Restore(overlay, "minimized fullscreen");
            ShowWindow(fullscreen.Handle, 4);
            fullscreen.WindowState = FormWindowState.Normal;
            fullscreen.Bounds = bounds;
            fullscreen.Raise();
            int cloak = 1;
            int cloakResult = DwmSetWindowAttribute(fullscreen.Handle, 13, ref cloak, sizeof(int));
            if (cloakResult == 0)
            {
                Pump();
                Check(!Full(overlay), "cloaked fullscreen suppressed overlay");
                cloak = 0;
                DwmSetWindowAttribute(fullscreen.Handle, 13, ref cloak, sizeof(int));
                Pump();
            }
            else Console.WriteLine("SKIP native cloaking unavailable: " + cloakResult);
            fullscreen.Raise();
            Call(overlay, "EnsureDesktopVisible");
            fullscreen.Bounds = normalBounds;
            Pump();
            Check(!Full(overlay), "exiting fullscreen retained fullscreen detection");
            Restore(overlay, "fullscreen resized to normal");
            Check(overlay.Location == original, "exit fullscreen moved overlay");

            // Simulate a saved HWND position invalidated by display removal.
            int desktopRight = bounds.Right;
            foreach (Screen connected in Screen.AllScreens) desktopRight = Math.Max(desktopRight, connected.Bounds.Right);
            overlay.Location = new Point(desktopRight + 2000, bounds.Top + 120);
            Call(overlay, "ShowDesktop");
            bool onScreen = false;
            foreach (Screen connected in Screen.AllScreens) onScreen |= connected.Bounds.IntersectsWith(overlay.Bounds);
            Check(onScreen, "invalidated monitor position was not recovered");

            using (var maximized = new Fixture(normalBounds, true))
            {
                maximized.WindowState = FormWindowState.Maximized;
                maximized.Raise();
                Check(!Full(overlay), "normal maximized window triggered fullscreen hiding");
            }
            fullscreen.Bounds = bounds;
            fullscreen.Raise();
            Check(Full(overlay), "restored fullscreen fixture did not regain fullscreen state");
            Call(overlay, "EnsureDesktopVisible");
            Set(settings, "OverlayMode", "attach");
            Call(overlay, "ApplySettings", settings);
            Call(overlay, "AttachTo", normal.Handle);
            Check(IsWindowVisible(overlay.Handle), "fullscreen state leaked into attachment mode");
            Set(settings, "OverlayMode", "desktop");
            Call(overlay, "ApplySettings", settings);
            Call(overlay, "ShowDesktop");
            Check(!IsWindowVisible(overlay.Handle), "mode switch flashed overlay over fullscreen");
        }
        // Also start directly under fullscreen, before the overlay was ever shown.
        using (var fullscreen = new Fixture(bounds))
        using (var overlay = (Form)Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.OverlayForm", true), new object[] { settings }))
        {
            fullscreen.Raise();
            Call(overlay, "ShowDesktop");
            Check(!IsWindowVisible(overlay.Handle), "startup during fullscreen showed overlay");
            fullscreen.Dispose();
            Restore(overlay, "fullscreen application closed");
        }
    }

    private static void MultipleMonitors()
    {
        if (Screen.AllScreens.Length < 2)
        {
            Console.WriteLine("SKIP physical multi-monitor/focus checks: only one monitor. Offset/negative/4K geometry passed.");
            return;
        }
        Screen first = Screen.AllScreens[0], second = Screen.AllScreens[1];
        object settings = New("MonitorSettings");
        Set(settings, "DesktopX", first.Bounds.Left + 80);
        Set(settings, "DesktopY", first.Bounds.Top + 80);
        using (var fullscreen = new Fixture(first.Bounds))
        using (var other = new Fixture(new Rectangle(second.Bounds.Left + 40, second.Bounds.Top + 40, 400, 300), true))
        using (var overlay = (Form)Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.OverlayForm", true), new object[] { settings }))
        {
            fullscreen.Raise();
            other.Raise();
            SetForegroundWindow(other.Handle);
            Call(overlay, "EnsureDesktopVisible");
            Check(!IsWindowVisible(overlay.Handle), "other-monitor focus exposed overlay above fullscreen");
            overlay.Location = new Point(second.Bounds.Left + 80, second.Bounds.Top + 80);
            Restore(overlay, "moved overlay to monitor without fullscreen");
        }
    }

    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { nativeError = e.Exception; };
            // WinForms initializes PerMonitorV2 lazily when the first control
            // is created. Do this before Screen caches any monitor geometry.
            using (var bootstrap = new Form()) { IntPtr ignored = bootstrap.Handle; }
            assembly = Assembly.LoadFrom(args[0]);
            Type detector = assembly.GetType("CodexRateMonitorNative.FullscreenDetector", true);
            detect = detector.GetMethod("IsFullscreenOnMonitor", Static);
            geometry = detector.GetMethod("IsFullscreenGeometry", Static);
            GeometryAndDebounce();
            foreach (Screen screen in Screen.AllScreens) NativeTransitions(screen);
            MultipleMonitors();
            Console.WriteLine("PASS " + assertions + " fullscreen assertions: geometry, debounce, Z order, visibility, focus, position, updates and modes.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            Environment.ExitCode = 1;
        }
        finally { if (GetForegroundWindow() != foreground) SetForegroundWindow(foreground); }
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
if ($LASTEXITCODE -ne 0) { throw 'Fullscreen overlay test compilation failed.' }
& $testExe $monitorExe
if ($LASTEXITCODE -ne 0) { throw 'Fullscreen overlay test failed.' }
