[CmdletBinding()]
param([string]$MonitorPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path (Join-Path $repoRoot '.build\window-attachment-test') (Split-Path (Split-Path $monitorExe -Parent) -Leaf)
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'WindowAttachmentRegression.cs'
$testExe = Join-Path $testDirectory 'WindowAttachmentRegression.exe'
@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class WindowAttachmentRegression
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly HashSet<uint> ProcessIds = new HashSet<uint> { (uint)Process.GetCurrentProcess().Id };
    private static MethodInfo findMainWindow, isCandidate;
    private static int assertions;
    private static Exception nativeError;
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static readonly WindowProcedure Procedure = DefWindowProc;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string MenuName, ClassName;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);

    // Real native popup handles, kept offscreen and shown without activation.
    private sealed class Popup : IDisposable
    {
        public IntPtr Handle;
        public Popup(string className, int extendedStyle, IntPtr owner, int offset)
        {
            Handle = CreateWindowEx(extendedStyle, className, "attachment regression popup",
                unchecked((int)0x80000000), -18000 + offset, -18000 + offset,
                1400, 900, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            Check(Handle != IntPtr.Zero, "native popup creation failed: " + className + ": " + Marshal.GetLastWin32Error());
            Check(SetWindowPos(Handle, new IntPtr(-1), -18000 + offset, -18000 + offset,
                1400, 900, 0x10 | 0x40), "native popup positioning failed");
            Check(IsWindowVisible(Handle), "popup fixture is not visible");
        }
        public void Dispose() { if (Handle != IntPtr.Zero) DestroyWindow(Handle); Handle = IntPtr.Zero; }
    }

    private static void Check(bool condition, string message)
    {
        if (nativeError != null) throw new Exception("native window callback failed", nativeError);
        if (!condition) throw new Exception(message);
        assertions++;
    }
    private static void Register(string name)
    {
        var windowClass = new WindowClass { Procedure = Procedure, Instance = GetModuleHandle(null), ClassName = name };
        Check(RegisterClass(ref windowClass) != 0, "fixture class registration failed: " + name);
    }
    private static IntPtr Choose(IntPtr previous, IntPtr foreground)
    {
        return (IntPtr)findMainWindow.Invoke(null, new object[] { ProcessIds, previous, foreground });
    }
    private static bool Eligible(IntPtr window)
    {
        return (bool)isCandidate.Invoke(null, new object[] { window, ProcessIds });
    }
    private static void Set(object instance, string property, object value)
    {
        instance.GetType().GetProperty(property).SetValue(instance, value, null);
    }
    private static Form MainWindow(int width, int height, int offset)
    {
        var form = new Form { StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(-20000 + offset, -20000 + offset, width, height) };
        ShowWindow(form.Handle, 4);
        Check(IsWindowVisible(form.Handle), "main fixture is not visible");
        return form;
    }

    private static void Run(Assembly assembly)
    {
        Type locator = assembly.GetType("CodexRateMonitorNative.WindowLocator", true);
        findMainWindow = locator.GetMethod("FindMainWindow", PrivateStatic);
        isCandidate = locator.GetMethod("IsMainWindowCandidate", PrivateStatic);
        Check(findMainWindow != null && isCandidate != null, "main-window selection helpers not found");
        Register("Chrome_WidgetWin_2");
        Register("tooltips_class32");
        Register("Issue8ToolWindow");

        using (Form main = MainWindow(1000, 700, 0))
        using (Form secondary = MainWindow(600, 400, 200))
        {
            Check(Eligible(main.Handle), "main window was rejected");
            Check(Choose(IntPtr.Zero, IntPtr.Zero) == main.Handle, "initial discovery did not choose the largest main window");
            Check(Choose(main.Handle, secondary.Handle) == secondary.Handle, "foreground main-window switch was ignored");
            Check(Choose(secondary.Handle, IntPtr.Zero) == secondary.Handle, "valid previous target was not preserved");

            object settings = Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.MonitorSettings", true), true);
            Set(settings, "OverlayMode", "attach");
            Set(settings, "ShowResetCredits", false);
            Type overlayType = assembly.GetType("CodexRateMonitorNative.OverlayForm", true);
            using (var overlay = (Form)Activator.CreateInstance(overlayType, new object[] { settings }))
            {
                MethodInfo attach = overlayType.GetMethod("AttachTo");
                foreach (string position in new string[] { "top", "bottom-right" })
                {
                    Set(settings, "Position", position);
                    attach.Invoke(overlay, new object[] { main.Handle });
                    Rectangle anchored = overlay.Bounds;
                    foreach (string className in new string[] { "#32768", "Chrome_WidgetWin_2", "tooltips_class32" })
                    foreach (int offset in new int[] { 0, 250, 500 })
                    using (var menu = new Popup(className, 0, IntPtr.Zero, offset))
                    {
                        Check(!Eligible(menu.Handle), "unowned menu/tooltip was accepted: " + className);
                        Check(Choose(IntPtr.Zero, menu.Handle) == main.Handle, "initial discovery selected a popup: " + className);
                        Check(Choose(main.Handle, menu.Handle) == main.Handle, "menu opening changed the attachment target: " + className);
                        attach.Invoke(overlay, new object[] { Choose(main.Handle, menu.Handle) });
                        Check(overlay.Bounds == anchored, "menu position moved the " + position + " overlay");
                        if (className == "Chrome_WidgetWin_2" && position == "top" && offset == 0)
                        {
                            using (Process current = Process.GetCurrentProcess())
                                Check(current.MainWindowHandle == menu.Handle,
                                    "fixture did not reproduce the old Process.MainWindowHandle misidentification");
                            Console.WriteLine("PASS reproduced legacy MainWindowHandle selecting an unowned popup.");
                        }
                    }
                    Check(Choose(main.Handle, IntPtr.Zero) == main.Handle, "menu closure changed the attachment target");
                    main.Location = new Point(main.Left + 100, main.Top + 60);
                    attach.Invoke(overlay, new object[] { Choose(main.Handle, main.Handle) });
                    Check(overlay.Location == new Point(anchored.Left + 100, anchored.Top + 60),
                        "overlay stopped following main-window movement");
                }
            }

            foreach (int extendedStyle in new int[] { 0x80, 0x08000000 })
            using (var tool = new Popup("Issue8ToolWindow", extendedStyle, IntPtr.Zero, 0))
            {
                Check(!Eligible(tool.Handle), "tool/no-activate window was accepted");
                Check(Choose(IntPtr.Zero, tool.Handle) == main.Handle, "tool/no-activate window displaced initial main-window discovery");
                Check(Choose(main.Handle, tool.Handle) == main.Handle, "tool/no-activate window displaced cached target");
            }
            using (var menu = new Popup("Chrome_WidgetWin_2", 0, secondary.Handle, 0))
            {
                Check(!Eligible(menu.Handle), "owned popup was accepted");
                Check(Choose(main.Handle, menu.Handle) == secondary.Handle, "owned popup did not resolve to its main window");
            }
            using (Form dialog = MainWindow(1400, 900, 300))
            {
                dialog.Owner = main;
                Check(!Eligible(dialog.Handle), "owned dialog was accepted as a main window");
                Check(Choose(secondary.Handle, dialog.Handle) == main.Handle, "dialog did not resolve to its root owner: owner=" +
                    GetWindow(dialog.Handle, 4) + " root=" + GetAncestor(dialog.Handle, 3) + " main=" + main.Handle +
                    " mainVisible=" + IsWindowVisible(main.Handle) + " dialogVisible=" + IsWindowVisible(dialog.Handle));
                Check(Choose(IntPtr.Zero, IntPtr.Zero) == main.Handle, "owned dialog displaced initial discovery");
                using (var nested = new Popup("Chrome_WidgetWin_2", 0, dialog.Handle, 0))
                    Check(Choose(secondary.Handle, nested.Handle) == main.Handle, "nested popup ownership did not resolve to the main window");
            }
            using (var child = new Control())
            {
                main.Controls.Add(child);
                Check(!Eligible(child.Handle), "child control was accepted as a main window");
                Check(Choose(secondary.Handle, child.Handle) == main.Handle, "foreground child did not resolve to its main window");
            }
            Check((IntPtr)findMainWindow.Invoke(null, new object[] { new HashSet<uint>(), main.Handle, main.Handle }) == IntPtr.Zero,
                "empty process list retained an old target");
            Check((IntPtr)findMainWindow.Invoke(null, new object[] { new HashSet<uint> { uint.MaxValue }, main.Handle, main.Handle }) == IntPtr.Zero,
                "unrelated process was accepted");

            ShowWindow(main.Handle, 6);
            Check(IsIconic(main.Handle), "fixture did not minimize");
            Check(Choose(main.Handle, IntPtr.Zero) == main.Handle, "minimized main window lost its identity");
            object state = locator.GetMethod("GetUsageState").Invoke(null, new object[] { main.Handle });
            Check(state.ToString() == "Minimized", "minimized usage state regressed");
            ShowWindow(main.Handle, 4);
            Check(!IsIconic(main.Handle) && Eligible(main.Handle), "restored main window was rejected");

            ShowWindow(main.Handle, 0);
            Check(Choose(main.Handle, IntPtr.Zero) == secondary.Handle, "hidden cached target was retained");
            IntPtr closed = main.Handle;
            main.Dispose();
            Check(Choose(closed, IntPtr.Zero) == secondary.Handle, "destroyed cached target was retained");
            ShowWindow(secondary.Handle, 0);
            using (var popup = new Popup("Chrome_WidgetWin_2", 0, IntPtr.Zero, 0))
                Check(Choose(closed, popup.Handle) == IntPtr.Zero, "popup was selected when no main window existed");
            using (Form reopened = MainWindow(900, 650, 0))
                Check(Choose(closed, IntPtr.Zero) == reopened.Handle, "reopened main window was not discovered");
        }
        Console.WriteLine("PASS " + assertions + " native-window assertions: menus, tools, ownership, target switching, anchors, movement, minimize/restore, hide/close/reopen and process isolation.");
    }

    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { nativeError = e.Exception; };
            Run(Assembly.LoadFrom(args[0]));
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            for (Exception inner = error.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine(inner.GetType().Name + ": " + inner.Message);
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
if ($LASTEXITCODE -ne 0) { throw 'Window attachment test compilation failed.' }
& $testExe $monitorExe
if ($LASTEXITCODE -ne 0) { throw 'Window attachment test failed.' }
