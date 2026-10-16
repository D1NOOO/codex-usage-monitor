using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexRateMonitorNative
{
    // Examines the front application on the overlay's monitor, rather than the
    // global foreground window (which may be on a different monitor).
    internal static class FullscreenDetector
    {
        private const int WS_CAPTION = 0x00C00000;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        public static bool IsFullscreenOnMonitor(IntPtr overlay)
        {
            IntPtr monitor = MonitorFromWindow(overlay, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return false;
            Rectangle screen = ToRectangle(info.Monitor);

            // GetWindow follows Z order. Bound the walk and detect repeated
            // handles because another process can destroy/reorder windows here.
            var visited = new HashSet<IntPtr>();
            for (IntPtr window = GetTopWindow(IntPtr.Zero);
                window != IntPtr.Zero && visited.Count < 4096 && visited.Add(window);
                window = NativeMethods.GetWindow(window, 2))
            {
                if (window == overlay || !IsApplicationWindow(window))
                    continue;
                Rectangle bounds;
                if (!TryGetBounds(window, out bounds))
                    continue;
                if (!bounds.IntersectsWith(screen))
                    continue;
                if (IsFullscreenWindow(window, bounds, screen))
                    return true;

                // An ordinary application above a fullscreen window means the
                // user has switched back to desktop work on this monitor.
                // Owned dialogs/menus keep their fullscreen owner's state.
                if (MonitorFromWindow(window, 0) == monitor)
                {
                    IntPtr owner = NativeMethods.GetWindow(window, NativeMethods.GW_OWNER);
                    for (int depth = 0; owner != IntPtr.Zero && depth < 32; depth++)
                    {
                        Rectangle ownerBounds;
                        if (IsApplicationWindow(owner) && TryGetBounds(owner, out ownerBounds) &&
                            IsFullscreenWindow(owner, ownerBounds, screen))
                            return true;
                        owner = NativeMethods.GetWindow(owner, NativeMethods.GW_OWNER);
                    }
                    return false;
                }
            }
            return false;
        }

        private static bool TryGetBounds(IntPtr window, out Rectangle bounds)
        {
            NativeMethods.RECT frame;
            bool success = DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS,
                out frame, Marshal.SizeOf(typeof(NativeMethods.RECT))) == 0;
            if (!success || frame.Right <= frame.Left || frame.Bottom <= frame.Top)
                success = NativeMethods.GetWindowRect(window, out frame);
            bounds = success ? ToRectangle(frame) : Rectangle.Empty;
            return success && bounds.Width > 0 && bounds.Height > 0;
        }

        private static bool IsFullscreenWindow(IntPtr window, Rectangle bounds, Rectangle screen)
        {
            NativeMethods.RECT client;
            var origin = new Point();
            if (!GetClientRect(window, out client) || !ClientToScreen(window, ref origin))
                return false;
            Rectangle content = new Rectangle(origin.X, origin.Y,
                client.Right - client.Left, client.Bottom - client.Top);
            int style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
            bool framedMaximized = IsZoomed(window) && (style & WS_CAPTION) == WS_CAPTION;
            return IsFullscreenGeometry(bounds, content, screen, framedMaximized);
        }

        internal static bool IsFullscreenGeometry(Rectangle frame, Rectangle client,
            Rectangle monitor, bool framedMaximized)
        {
            // Compare physical screen coordinates: the app runs PerMonitorV2.
            // Requiring the client to fill the screen excludes title bars and
            // normal maximized windows even when the taskbar is auto-hidden.
            return !framedMaximized && monitor.Width > 0 && monitor.Height > 0 &&
                CoversMonitor(frame, monitor) && CoversMonitor(client, monitor);
        }

        private static bool CoversMonitor(Rectangle bounds, Rectangle monitor)
        {
            const int tolerance = 2;
            return bounds.Width > 0 && bounds.Height > 0 &&
                bounds.Left <= monitor.Left + tolerance && bounds.Top <= monitor.Top + tolerance &&
                bounds.Right >= monitor.Right - tolerance && bounds.Bottom >= monitor.Bottom - tolerance;
        }

        private static bool IsApplicationWindow(IntPtr window)
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
                return false;
            int style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
            int extended = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);
            if ((style & NativeMethods.WS_CHILD) != 0 ||
                (extended & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
                return false;
            int cloaked;
            if (DwmGetWindowAttribute(window, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;
            var name = new StringBuilder(256);
            NativeMethods.GetClassName(window, name, name.Capacity);
            string className = name.ToString();
            return className != "Progman" && className != "WorkerW" &&
                className != "Shell_TrayWnd" && className != "Shell_SecondaryTrayWnd" &&
                className != "#32768" && className != "tooltips_class32";
        }

        private static Rectangle ToRectangle(NativeMethods.RECT value)
        {
            return Rectangle.FromLTRB(value.Left, value.Top, value.Right, value.Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeMethods.RECT Monitor, Work;
            public uint Flags;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeMethods.RECT rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
            out NativeMethods.RECT value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }

    internal sealed class FullscreenVisibility
    {
        private long restoreAfter;
        public bool Suppressed { get; private set; }

        public void Observe(bool fullscreen, long now)
        {
            if (fullscreen)
            {
                Suppressed = true;
                restoreAfter = now + Stopwatch.Frequency / 3;
            }
            else if (now >= restoreAfter)
                Suppressed = false;
        }

        public void Reset()
        {
            Suppressed = false;
            restoreAfter = 0;
        }
    }
}
