using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexRateMonitorNative
{
    internal static class DisplayDpi
    {
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        public static int ForWindow(IntPtr window)
        {
            try
            {
                uint dpi = GetDpiForWindow(window);
                if (dpi > 0) return (int)dpi;
            }
            catch (EntryPointNotFoundException) { }
            using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                return (int)Math.Round(graphics.DpiX);
        }

        public static int ForWindowMonitor(IntPtr window, IntPtr reference)
        {
            if (MonitorFromWindow(window, 2) == MonitorFromWindow(reference, 2))
                return ForWindow(reference);
            // Query our own DPI-aware hidden window on the target monitor.
            // GetDpiForWindow(target) would return 96 for an unaware target.
            var probe = new NativeWindow();
            try
            {
                Rectangle screen = Screen.FromHandle(window).Bounds;
                probe.CreateHandle(new CreateParams
                {
                    Style = unchecked((int)0x80000000),
                    ExStyle = 0x80 | 0x08000000,
                    X = screen.Left + 1, Y = screen.Top + 1,
                    Width = 1, Height = 1
                });
                return ForWindow(probe.Handle);
            }
            finally { probe.DestroyHandle(); }
        }
    }

    internal static class UsagePanelTools
    {
        public static int? GetConfirmedWindows(RateSnapshot snapshot)
        {
            if (snapshot == null) return null;
            if (snapshot.ReplaceMissingWindows)
                return (snapshot.Primary != null ? 1 : 0) | (snapshot.Secondary != null ? 2 : 0);
            return snapshot.ConfirmedWindows;
        }

        public static int GetVisibleWindows(MonitorSettings settings, RateSnapshot snapshot)
        {
            if (settings.UsagePanels == "weekly") return 2;
            int? confirmed = GetConfirmedWindows(snapshot);
            return settings.UsagePanels == "all" || !confirmed.HasValue || confirmed.Value == 0
                ? 3 : confirmed.Value;
        }

        public static int GetWindowCount(MonitorSettings settings, RateSnapshot snapshot)
        {
            int visible = GetVisibleWindows(settings, snapshot);
            return ((visible & 1) != 0 ? 1 : 0) + ((visible & 2) != 0 ? 1 : 0);
        }

        public static string FormatSummary(MonitorSettings settings, RateSnapshot snapshot)
        {
            int visible = GetVisibleWindows(settings, snapshot);
            var parts = new List<string>();
            for (int index = 0; index < 2; index++)
            {
                if ((visible & (1 << index)) == 0) continue;
                WindowUsage usage = snapshot == null ? null : (index == 0 ? snapshot.Primary : snapshot.Secondary);
                string percent = usage == null ? "--%" : UsageDisplayTools.FormatPercent(
                    UsageDisplayTools.GetDisplayedPercent(usage.UsedPercent, settings.UsageDisplay));
                parts.Add(I18n.Translate(index == 0 ? "FiveHour" : "SevenDay", settings.Language) + " " + percent);
            }
            return string.Join(" · ", parts.ToArray());
        }
    }

    // Both windows use this 96-DPI geometry and the same painting code.
    internal sealed class OverlayRenderer
    {
        private MonitorSettings settings;
        private RateSnapshot snapshot;
        private ResetCreditsInfo resetCredits;
        private string status;

        public static Size GetLogicalSize(MonitorSettings settings, bool credits)
        {
            return settings.DisplayLines == "2"
                ? new Size(DrawingHelpers.BottomRightWidth, credits
                    ? DrawingHelpers.BottomRightHeightWithCredits : DrawingHelpers.BottomRightHeight)
                : new Size(credits ? DrawingHelpers.TopWidthWithCredits : 470, 40);
        }

        public static float GetScale(MonitorSettings settings, int dpi)
        {
            return (float)(settings.Style.Scale * StyleSettings.BaselineScale * Math.Max(96, dpi) / 96d);
        }

        public static Size GetPixelSize(MonitorSettings settings, bool credits, int dpi)
        {
            Size logical = GetLogicalSize(settings, credits);
            float scale = GetScale(settings, dpi);
            return new Size((int)Math.Round(logical.Width * scale),
                (int)Math.Round(logical.Height * scale));
        }

        private static Size GetSnapshotLogicalSize(MonitorSettings settings, RateSnapshot snapshot, bool credits)
        {
            int count = UsagePanelTools.GetWindowCount(settings, snapshot);
            if (count == 2) return GetLogicalSize(settings, credits);
            return settings.DisplayLines == "2"
                ? new Size(DrawingHelpers.BottomRightWidth, credits ? 67 : 39)
                : new Size(credits ? 470 : 238, 40);
        }

        public static Size GetSnapshotPixelSize(MonitorSettings settings, RateSnapshot snapshot, bool credits, int dpi)
        {
            Size logical = GetSnapshotLogicalSize(settings, snapshot, credits);
            float scale = GetScale(settings, dpi);
            return new Size((int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
        }

        public static Bitmap CreateBitmap(MonitorSettings settings, RateSnapshot snapshot,
            ResetCreditsInfo credits, string status, int dpi)
        {
            bool showCredits = settings.ShowResetCredits && credits != null && credits.AvailableCount > 0;
            Size size = GetSnapshotPixelSize(settings, snapshot, showCredits, dpi);
            var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    Paint(graphics, settings, snapshot, credits, status, dpi);
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        private bool ShowCreditsBadge
        {
            get { return settings.ShowResetCredits && resetCredits != null && resetCredits.AvailableCount > 0; }
        }

        public static void Paint(Graphics graphics, MonitorSettings settings,
            RateSnapshot snapshot, ResetCreditsInfo credits, string status, int dpi)
        {
            var renderer = new OverlayRenderer();
            renderer.settings = settings;
            renderer.snapshot = snapshot;
            renderer.resetCredits = credits;
            renderer.status = status;
            GraphicsState state = graphics.Save();
            try { renderer.Draw(graphics, dpi); }
            finally { graphics.Restore(state); }
        }

        private void Draw(Graphics g, int dpi)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearType assumes an opaque background; grayscale antialiasing
            // also works on the alpha surface used by the desktop compositor.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float scale = GetScale(settings, dpi);
            g.ScaleTransform(scale, scale);

            Size logical = GetSnapshotLogicalSize(settings, snapshot, ShowCreditsBadge);
            float areaW = logical.Width;
            float areaH = logical.Height;
            float cardW = areaW;
            float cardH = areaH;
            float radius = (float)settings.Style.CornerRadius;

            Color outer = ColorTools.Parse(settings.Style.Background);
            Color border = ColorTools.Parse(settings.Style.Border);
            Color card = ColorTools.Parse(settings.Style.CardBackground);
            Color text = ColorTools.Parse(settings.Style.Text);
            Color muted = ColorTools.Parse(settings.Style.MutedText);
            Color track = ColorTools.Parse(settings.Style.Track);

            using (var outerBrush = new SolidBrush(outer))
            using (var borderPen = new Pen(border, 1f))
            using (GraphicsPath outerPath = DrawingHelpers.RoundRect(
                new RectangleF(0.5f, 0.5f, cardW - 1f, cardH - 1f),
                radius))
            {
                g.FillPath(outerBrush, outerPath);
                g.DrawPath(borderPen, outerPath);
            }

            bool stacked = settings.DisplayLines == "2";
            int visible = UsagePanelTools.GetVisibleWindows(settings, snapshot);
            int count = UsagePanelTools.GetWindowCount(settings, snapshot);
            if (snapshot == null)
            {
                DrawStatusCard(g, stacked ? new RectangleF(3, 3, 246, count * 39 - 6)
                    : new RectangleF(5, 5, count * 232 - 4, 30), card, muted);
            }
            else
            {
                int slot = 0;
                for (int index = 0; index < 2; index++)
                {
                    if ((visible & (1 << index)) == 0) continue;
                    RectangleF bounds = stacked ? new RectangleF(3, 3 + slot * 39, 246, 33)
                        : new RectangleF(5 + slot * 232, 5, 228, 30);
                    DrawCard(g, bounds, index == 0, card, text, muted, track);
                    slot++;
                }
            }
            if (ShowCreditsBadge)
                DrawCreditsCard(g, stacked ? new RectangleF(3, 3 + count * 39, 246, 22)
                    : new RectangleF(5 + count * 232, 5, 228, 30), card, text, muted, track);
        }

        private void DrawStatusCard(Graphics g, RectangleF bounds, Color card, Color text)
        {
            using (var brush = new SolidBrush(card))
            using (GraphicsPath path = DrawingHelpers.RoundRect(bounds,
                Math.Max(0, (float)settings.Style.CornerRadius - 3f)))
                g.FillPath(brush, path);
            using (var font = new Font(settings.Style.FontFamily, (float)settings.Style.FontSize,
                FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(text))
            using (var format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                RectangleF content = RectangleF.Inflate(bounds, -7, 0);
                g.DrawString(status ?? I18n.Translate("UsageLoading", settings.Language), font, brush, content, format);
            }
        }

        private void DrawCard(
            Graphics g,
            RectangleF bounds,
            bool primary,
            Color card,
            Color text,
            Color muted,
            Color track)
        {
            float cardRadius = Math.Max(0, (float)settings.Style.CornerRadius - 3f);
            using (var brush = new SolidBrush(card))
            using (GraphicsPath path = DrawingHelpers.RoundRect(bounds, cardRadius))
                g.FillPath(brush, path);

            WindowUsage usage = snapshot == null ? null : (primary ? snapshot.Primary : snapshot.Secondary);
            string label = primary ? I18n.Translate("FiveHour", settings.Language) : I18n.Translate("SevenDay", settings.Language);
            double value = usage == null
                ? 0d
                : UsageDisplayTools.GetDisplayedPercent(
                    usage.UsedPercent, settings.UsageDisplay);
            string percent = usage == null ? "--%" :
                UsageDisplayTools.FormatPercent(value);
            string reset = usage == null
                ? I18n.Translate(UsagePanelTools.GetConfirmedWindows(snapshot).HasValue
                    ? "UsageNotProvided" : "UsageLoading", settings.Language)
                : FormatReset(usage.ResetsAt);

            FontFamily family;
            try
            {
                family = new FontFamily(settings.Style.FontFamily);
            }
            catch
            {
                family = SystemFonts.MessageBoxFont.FontFamily;
            }

            float mainFontSize = (float)settings.Style.FontSize;
            float resetFontSize = (float)settings.Style.ResetFontSize;
            using (family)
            using (var mainFont = new Font(family, mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var resetFont = new Font(family, resetFontSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(text))
            using (var mutedBrush = new SolidBrush(muted))
            {
                DrawingHelpers.DrawUsageText(
                    g, bounds, label, percent, reset,
                    mainFont, resetFont, textBrush, mutedBrush);
            }

            Color normal = ColorTools.Parse(primary ? settings.Style.Primary : settings.Style.Secondary);
            Color progress = usage == null
                ? normal
                : UsageDisplayTools.GetProgressColor(
                    value,
                    settings.UsageDisplay,
                    normal,
                    ColorTools.Parse(settings.Style.Warning),
                    ColorTools.Parse(settings.Style.Danger));

            RectangleF trackRect = new RectangleF(bounds.X + 7, bounds.Bottom - 4, bounds.Width - 14, 2);
            using (var trackBrush = new SolidBrush(track))
                g.FillRectangle(trackBrush, trackRect);
            using (var progressBrush = new SolidBrush(progress))
                g.FillRectangle(progressBrush,
                    new RectangleF(
                        trackRect.X,
                        trackRect.Y,
                        trackRect.Width * (float)value / 100f,
                        trackRect.Height));
        }

        // Third card: rate-limit reset credits (READ-ONLY display; the app
        // never redeems credits). Shows the available count, the earliest
        // expiry among available credits, and a thin bar with the elapsed
        // fraction of that credit's 30-day lifetime. The bar/deadline color
        // escalates: normal muted -> warning within 7 days -> danger within 3.
        private void DrawCreditsCard(
            Graphics g,
            RectangleF bounds,
            Color card,
            Color text,
            Color muted,
            Color track)
        {
            ResetCreditsInfo info = resetCredits;
            if (info == null)
                return;

            float cardRadius = Math.Max(0, (float)settings.Style.CornerRadius - 3f);

            // Deadline escalation: >7 days keeps the calm card, <=7 days turns
            // the texts amber, <=3 days additionally tints the card background
            // red -- an expiring reset is paid-for capacity about to vanish.
            double daysLeft = info.EarliestExpiry.HasValue
                ? (info.EarliestExpiry.Value - DateTime.Now).TotalDays
                : 99d;
            Color deadline = daysLeft <= 3d
                ? ColorTools.Parse(settings.Style.Danger)
                : (daysLeft <= 7d
                    ? ColorTools.Parse(settings.Style.Warning)
                    : muted);
            Color cardFill = card;
            if (daysLeft <= 3d)
            {
                Color danger = ColorTools.Parse(settings.Style.Danger);
                cardFill = Color.FromArgb(
                    Math.Min(255, card.R + 38),
                    (card.G + danger.G) / 2,
                    (card.B + danger.B) / 2);
            }

            using (var brush = new SolidBrush(cardFill))
            using (GraphicsPath path = DrawingHelpers.RoundRect(bounds, cardRadius))
                g.FillPath(brush, path);

            string count = string.Format(CultureInfo.InvariantCulture,
                I18n.Translate("CreditsBadge", settings.Language),
                info.AvailableCount.ToString(CultureInfo.InvariantCulture));
            string expiry = string.Format(CultureInfo.InvariantCulture,
                I18n.Translate("CreditsExpire", settings.Language),
                info.FormatEarliestExpiry());

            FontFamily family;
            try
            {
                family = new FontFamily(settings.Style.FontFamily);
            }
            catch
            {
                family = SystemFonts.MessageBoxFont.FontFamily;
            }

            float mainFontSize = (float)settings.Style.FontSize;
            float resetFontSize = (float)settings.Style.ResetFontSize;
            using (family)
            using (var mainFont = new Font(family, mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var smallFont = new Font(family, resetFontSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(daysLeft <= 7d ? deadline : text))
            using (var deadlineBrush = new SolidBrush(deadline))
            {
                DrawingHelpers.DrawCreditsText(
                    g, bounds, count, expiry, mainFont, smallFont, textBrush, deadlineBrush);
            }

            RectangleF trackRect = new RectangleF(bounds.X + 7, bounds.Bottom - 4, bounds.Width - 14, 2);
            using (var trackBrush = new SolidBrush(track))
                g.FillRectangle(trackBrush, trackRect);
            double fraction = info.ElapsedFraction();
            if (fraction > 0d)
            {
                using (var progressBrush = new SolidBrush(deadline))
                    g.FillRectangle(progressBrush,
                        new RectangleF(
                            trackRect.X,
                            trackRect.Y,
                            trackRect.Width * (float)Math.Min(1d, fraction),
                            trackRect.Height));
            }
        }

        private string FormatReset(long? unixSeconds)
        {
            if (!unixSeconds.HasValue)
                return "--";
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).LocalDateTime;
            return I18n.FormatDate(local, settings.Language);
        }
    }

    internal static class LayeredWindowSurface
    {
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend
        {
            public byte Operation, Flags, Opacity, AlphaFormat;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
        {
            public uint HeaderSize;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, ImageSize;
            public int XPixelsPerMeter, YPixelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
            public uint Color;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(
            IntPtr window, out uint colorKey, out byte alpha, out uint flags);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(
            IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(
            IntPtr window, IntPtr destinationDc, IntPtr destination, ref Size size,
            IntPtr sourceDc, ref Point source, uint colorKey, ref Blend blend, uint flags);

        public static void Present(IntPtr window, Bitmap bitmap, double opacity)
        {
            // WinForms can clear its own layering style when Opacity is 1,
            // notably after handle creation. Restore our per-pixel surface.
            const int layered = 0x80000;
            int style = GetWindowLong(window, -20);
            uint colorKey, flags;
            byte alpha;
            bool uniformAlpha = GetLayeredWindowAttributes(window, out colorKey, out alpha, out flags);
            if (uniformAlpha)
                SetWindowLong(window, -20, style & ~layered);
            if (uniformAlpha || (style & layered) == 0)
                SetWindowLong(window, -20, style | layered);
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                // A top-down DIB preserves the premultiplied BGRA bytes,
                // including partial alpha along the antialiased outer edge.
                var info = new BitmapInfo { HeaderSize = 40, Width = bitmap.Width,
                    Height = -bitmap.Height, Planes = 1, BitCount = 32 };
                IntPtr bits;
                dib = CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int rowSize = bitmap.Width * 4;
                    var row = new byte[rowSize];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowSize);
                        Marshal.Copy(row, 0, IntPtr.Add(bits, y * rowSize), rowSize);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                previous = SelectObject(memoryDc, dib);
                var size = new Size { Width = bitmap.Width, Height = bitmap.Height };
                var source = new Point();
                var blend = new Blend { Opacity = (byte)Math.Round(opacity * 255d), AlphaFormat = 1 };
                // Opacity is combined with per-pixel alpha here. Form.Opacity
                // uses SetLayeredWindowAttributes, which conflicts with this API.
                if (!UpdateLayeredWindow(window, screenDc, IntPtr.Zero, ref size,
                    memoryDc, ref source, 0, ref blend, 2))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
