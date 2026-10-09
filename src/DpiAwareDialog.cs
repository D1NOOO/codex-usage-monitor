using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace CodexRateMonitorNative
{
    // Framework 4.8's incremental DPI scaling can retain font metrics and
    // minimum sizes from the previous monitor. Dialogs rebuild their declared
    // dimensions and pixel fonts from a 96-DPI baseline instead.
    internal abstract class DpiAwareDialog : Form
    {
        private DialogDpiLayout dpiLayout;
        private int dpiChangeGeneration;
        protected int LayoutDpi { get; private set; }

        protected DpiAwareDialog()
        {
            AutoScaleMode = AutoScaleMode.None;
            LayoutDpi = 96;
            Font source = SystemFonts.MessageBoxFont;
            Font = new Font(source.FontFamily, source.SizeInPoints * 96f / 72f,
                source.Style, GraphicsUnit.Pixel);
        }

        protected void InitializeDpiLayout(Size logicalClientSize)
        {
            dpiLayout = new DialogDpiLayout(this, logicalClientSize);
            if (IsHandleCreated) ApplyDpiLayout(DisplayDpi.ForWindow(Handle));
        }

        private void ApplyDpiLayout(int dpi)
        {
            if (dpiLayout == null || IsDisposed) return;
            LayoutDpi = Math.Max(96, dpi);
            dpiLayout.Apply(LayoutDpi);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiLayout(DisplayDpi.ForWindow(Handle));
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            e.Cancel = true;
            base.OnDpiChanged(e);
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;
            ApplyDpiLayout(e.DeviceDpiNew);
            Location = e.SuggestedRectangle.Location;
            int generation = ++dpiChangeGeneration;
            // Finish after native child controls receive AFTERPARENT. Applying
            // the same baseline twice is idempotent and cannot compound sizes.
            BeginInvoke(new Action(delegate
            {
                if (IsDisposed || generation != dpiChangeGeneration) return;
                ApplyDpiLayout(e.DeviceDpiNew);
                OnDpiLayoutChanged();
            }));
        }

        protected virtual void OnDpiLayoutChanged() { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && dpiLayout != null) dpiLayout.Dispose();
        }
    }

    internal sealed class DialogDpiLayout : IDisposable
    {
        private readonly Form form;
        private readonly Size logicalClientSize;
        private readonly List<ControlMetrics> metrics = new List<ControlMetrics>();
        // Control.Font can retain its previous instance when an equivalent font
        // is assigned. Reuse owned fonts and keep them alive until disposal.
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        public DialogDpiLayout(Form form, Size logicalClientSize)
        {
            this.form = form;
            this.logicalClientSize = logicalClientSize;
            Capture(form);
        }

        private void Capture(Control control)
        {
            metrics.Add(new ControlMetrics(control));
            foreach (Control child in control.Controls) Capture(child);
        }

        private static int Pixels(int logical, int dpi)
        {
            return (int)Math.Round(logical * dpi / 96d);
        }

        private static Size Pixels(Size logical, int dpi)
        {
            return new Size(Pixels(logical.Width, dpi), Pixels(logical.Height, dpi));
        }

        private static Padding Pixels(Padding logical, int dpi)
        {
            return new Padding(Pixels(logical.Left, dpi), Pixels(logical.Top, dpi),
                Pixels(logical.Right, dpi), Pixels(logical.Bottom, dpi));
        }

        public void Apply(int dpi)
        {
            foreach (ControlMetrics item in metrics) item.Control.SuspendLayout();
            try
            {
                foreach (ControlMetrics item in metrics)
                {
                    Control control = item.Control;
                    // Pixel units avoid the process-wide point-to-pixel font
                    // conversion cached by GDI+ when the program first starts.
                    string key = item.FontFamily + "/" + item.FontPixels.ToString("R", CultureInfo.InvariantCulture) +
                        "/" + (int)item.FontStyle + "/" + dpi;
                    Font font;
                    if (!fonts.TryGetValue(key, out font))
                    {
                        font = new Font(item.FontFamily, item.FontPixels * dpi / 96f,
                            item.FontStyle, GraphicsUnit.Pixel);
                        fonts.Add(key, font);
                    }
                    control.Font = font;
                    control.Padding = Pixels(item.Padding, dpi);
                    control.Margin = Pixels(item.Margin, dpi);
                    if (control == form) continue;
                    control.MinimumSize = Pixels(item.MinimumSize, dpi);
                    control.MaximumSize = Pixels(item.MaximumSize, dpi);
                    if (!control.AutoSize)
                    {
                        control.Bounds = new Rectangle(
                            Pixels(item.Bounds.X, dpi), Pixels(item.Bounds.Y, dpi),
                            Pixels(item.Bounds.Width, dpi), Pixels(item.Bounds.Height, dpi));
                    }
                    foreach (AbsoluteStyle style in item.AbsoluteStyles)
                        style.Apply(dpi);
                }
                form.ClientSize = Pixels(logicalClientSize, dpi);
            }
            finally
            {
                // Parent layout sees complete, consistent child measurements.
                for (int i = metrics.Count - 1; i >= 0; i--)
                    metrics[i].Control.ResumeLayout(true);
            }
        }

        public void Dispose()
        {
            foreach (Font font in fonts.Values) font.Dispose();
            fonts.Clear();
        }

        private sealed class ControlMetrics
        {
            public readonly Control Control;
            public readonly Rectangle Bounds;
            public readonly Padding Padding, Margin;
            public readonly Size MinimumSize, MaximumSize;
            public readonly string FontFamily;
            public readonly float FontPixels;
            public readonly FontStyle FontStyle;
            public readonly List<AbsoluteStyle> AbsoluteStyles = new List<AbsoluteStyle>();

            public ControlMetrics(Control control)
            {
                Control = control;
                Bounds = control.Bounds;
                Padding = control.Padding;
                Margin = control.Margin;
                MinimumSize = control.MinimumSize;
                MaximumSize = control.MaximumSize;
                FontFamily = control.Font.FontFamily.Name;
                FontPixels = control.Font.Unit == GraphicsUnit.Pixel
                    ? control.Font.Size : control.Font.SizeInPoints * 96f / 72f;
                FontStyle = control.Font.Style;
                var table = control as TableLayoutPanel;
                if (table == null) return;
                foreach (ColumnStyle style in table.ColumnStyles)
                    if (style.SizeType == SizeType.Absolute)
                        AbsoluteStyles.Add(new AbsoluteStyle(style));
                foreach (RowStyle style in table.RowStyles)
                    if (style.SizeType == SizeType.Absolute)
                        AbsoluteStyles.Add(new AbsoluteStyle(style));
            }
        }

        private sealed class AbsoluteStyle
        {
            private readonly ColumnStyle column;
            private readonly RowStyle row;
            private readonly float logical;
            public AbsoluteStyle(ColumnStyle style) { column = style; logical = style.Width; }
            public AbsoluteStyle(RowStyle style) { row = style; logical = style.Height; }
            public void Apply(int dpi)
            {
                if (column != null) column.Width = logical * dpi / 96f;
                else row.Height = logical * dpi / 96f;
            }
        }
    }
}
