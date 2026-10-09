[CmdletBinding()]
param(
    [string]$MonitorPath = '',
    [string]$CaptureDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) {
    throw 'Build the monitor with scripts/build.ps1 before running this test.'
}

$testDirectory = Join-Path $repoRoot '.build\settings-layout-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'SettingsLayoutSmoke.cs'
$testExe = Join-Path $testDirectory 'SettingsLayoutSmoke.exe'
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

internal static class SettingsLayoutSmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Run(Assembly.LoadFrom(args[0]), args.Length > 1 ? args[1] : "");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            Environment.ExitCode = 1;
        }
        finally { SetForegroundWindow(foreground); }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    // Exercise large fonts and scaled geometry without changing the user's
    // display settings. Native borders/glyphs retain the host's real DPI.
    private static void SimulateDpi(Form form, int dpi, int width = 820, int height = 760)
    {
        float factor;
        using (Graphics graphics = form.CreateGraphics()) factor = dpi / graphics.DpiX;
        var fonts = Descendants(form).Concat(new Control[] { form }).ToDictionary(
            delegate(Control control) { return control; },
            delegate(Control control) { return control.Font; });
        form.SuspendLayout();
        form.MaximumSize = new Size(6000, 6000);
        form.Scale(new SizeF(factor, factor));
        // Scale(SizeF) can scale Label fonts itself. Restore each font from its
        // original point size so both inherited and explicit fonts scale once.
        foreach (Control control in new Control[] { form }.Concat(Descendants(form)))
        {
            Font original = fonts[control];
            float size = original.Size * factor;
            if (Math.Abs(control.Font.Size - size) > 0.01f)
                control.Font = new Font(original.FontFamily, size, original.Style, original.Unit);
        }
        form.ClientSize = new Size((int)Math.Round(width * dpi / 96f),
            (int)Math.Round(height * dpi / 96f));
        form.ResumeLayout(true);
    }

    private static void VerifyContent(Form form)
    {
        Control[] leaves = Descendants(form).Where(delegate(Control control)
        {
            return control.Visible && (control is Label || control is Button ||
                control is RadioButton || control is CheckBox || control is ComboBox ||
                control is NumericUpDown);
        }).ToArray();
        foreach (Control control in leaves)
        {
            for (Control child = control; child.Parent != form; child = child.Parent)
            {
                // Clipping at the scroll viewport is intentional; all internal
                // panels and groups must still contain their complete contents.
                var scrolling = child.Parent as ScrollableControl;
                if (scrolling != null && scrolling.AutoScroll) break;
                Check(child.Parent.ClientRectangle.Contains(child.Bounds),
                    control.GetType().Name + " '" + control.Text + "' clipped in " +
                    child.Parent.GetType().Name + ": " + child.Bounds + " / " + child.Parent.ClientRectangle);
            }
            // AutoSize labels use native text metrics, which can exclude a
            // leading pixel included in Font.Height. Measure their actual text.
            Check((control is Label && ((Label)control).AutoSize) || control.Height >= control.Font.Height,
                "text clipped vertically: " + control.Text);
            var label = control as Label;
            if (label != null && label.AutoSize)
            {
                Size required = TextRenderer.MeasureText(label.Text, label.Font,
                    new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                Check(label.Height >= required.Height, "label text clipped: " + label.Text);
            }
            foreach (Control sibling in control.Parent.Controls)
            {
                if (sibling == control || !sibling.Visible) continue;
                Check(!control.Bounds.IntersectsWith(sibling.Bounds),
                    "overlapping controls: " + control.Text + " / " + sibling.Text);
            }
        }
    }

    private static void Capture(Form form, Panel content, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(path);
        }
        content.AutoScrollPosition = new Point(0, content.VerticalScroll.Maximum);
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(path),
                Path.GetFileNameWithoutExtension(path) + "-bottom.png"));
        }
    }

    private static void VerifyCallbacks<T>(Type formType, Type i18n)
    {
        object settings = Activator.CreateInstance(typeof(T), true);
        int previews = 0, saves = 0, cancels = 0;
        object saved = null;
        Action<T> preview = delegate(T value) { previews++; };
        Action<T> save = delegate(T value) { saves++; saved = value; };
        Action cancel = delegate { cancels++; };
        using (var form = (Form)Activator.CreateInstance(formType,
            new object[] { settings, preview, save, cancel }))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            var scale = (NumericUpDown)formType.GetField("scale", PrivateInstance).GetValue(form);
            int before = previews;
            scale.Value = 110;
            Check(previews > before, "changing an input stopped live preview");
            string saveText = (string)i18n.GetMethod("T").Invoke(null, new object[] { "SaveClose" });
            Descendants(form).OfType<Button>().Single(delegate(Button button) { return button.Text == saveText; }).PerformClick();
            Check(saves == 1 && cancels == 0, "save did not commit exactly once");
            object style = typeof(T).GetProperty("Style").GetValue(saved, null);
            Check(Math.Abs((double)style.GetType().GetProperty("Scale").GetValue(style, null) - 1.1) < 0.001,
                "save lost the edited input");
        }
        using (var form = (Form)Activator.CreateInstance(formType,
            new object[] { settings, preview, save, cancel }))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            string cancelText = (string)i18n.GetMethod("T").Invoke(null, new object[] { "Cancel" });
            Descendants(form).OfType<Button>().Single(delegate(Button button) { return button.Text == cancelText; }).PerformClick();
            Check(saves == 1 && cancels == 1, "cancel stopped restoring the original settings");
        }
        Console.WriteLine("PASS live preview, edited settings commit and cancel callbacks.");
    }

    private static void Run(Assembly assembly, string captures)
    {
        Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
        Type formType = assembly.GetType("CodexRateMonitorNative.AppearanceSettingsForm", true);
        Type i18n = assembly.GetType("CodexRateMonitorNative.I18n", true);
        if (!string.IsNullOrEmpty(captures)) Directory.CreateDirectory(captures);
        int cases = 0;
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        foreach (int dpi in new int[] { 96, 120, 144, 168, 192 })
        foreach (string mode in new string[] { "desktop", "attach" })
        {
            i18n.GetMethod("SetLanguage").Invoke(null, new object[] { language });
            object settings = Activator.CreateInstance(settingsType, true);
            settingsType.GetProperty("OverlayMode").SetValue(settings, mode, null);
            using (var form = (Form)Activator.CreateInstance(formType,
                new object[] { settings, null, null, null }))
            {
                float actualDpi;
                using (Graphics graphics = form.CreateGraphics()) actualDpi = graphics.DpiX;
                Check(Math.Abs(form.ClientSize.Width - 820f * actualDpi / 96f) <= 1,
                    "startup did not scale the form to the effective DPI");
                Check(form.Font.Unit == GraphicsUnit.Pixel &&
                    Math.Abs(form.Font.Size - 9.5f * actualDpi / 72f) < 0.01f,
                    "startup did not use monitor-sized pixel fonts");
                Size initialSize = form.Size;
                form.PerformAutoScale();
                form.PerformAutoScale();
                Check(form.Size == initialSize, "repeated layout scaled the form again");
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                if (language == "zh-CN" && dpi == 120 && mode == "desktop" && !string.IsNullOrEmpty(captures))
                {
                    Panel hostContent = form.Controls.OfType<Panel>().Single(delegate(Panel panel) { return panel.AutoScroll; });
                    Capture(form, hostContent, Path.Combine(captures, "settings-host-dpi.png"));
                    hostContent.AutoScrollPosition = Point.Empty;
                }
                SimulateDpi(form, dpi);
                formType.GetMethod("SetPreviewDpi").Invoke(form, new object[] { dpi });
                try { VerifyContent(form); }
                catch (Exception ex)
                {
                    throw new Exception(language + " / " + dpi + " DPI / " + mode + ": " + ex.Message);
                }
                Panel content = form.Controls.OfType<Panel>().Single(delegate(Panel panel) { return panel.AutoScroll; });
                Panel footer = form.Controls.OfType<Panel>().Single(delegate(Panel panel) { return panel.Dock == DockStyle.Bottom; });
                MethodInfo fit = formType.GetMethod("FitToWorkingArea", PrivateInstance);
                foreach (Rectangle workArea in new Rectangle[] {
                    new Rectangle(0, 0, 2880, 1704),
                    new Rectangle(-1366, 40, 1366, 680)
                })
                {
                    fit.Invoke(form, new object[] { workArea });
                    Check(workArea.Contains(form.Bounds), "window extends outside the working area");
                    Check(form.ClientRectangle.Contains(footer.Bounds), "footer is outside the window");
                    VerifyContent(form);
                    Control lastInput = (Control)formType.GetField("cornerRadius", PrivateInstance).GetValue(form);
                    content.ScrollControlIntoView(lastInput);
                    Rectangle inputBounds = content.RectangleToClient(lastInput.RectangleToScreen(lastInput.ClientRectangle));
                    Check(content.ClientRectangle.Contains(inputBounds), "last input cannot be reached by scrolling");
                    Check(form.ClientRectangle.Contains(footer.Bounds), "scrolling moved the footer");
                    foreach (Control input in Descendants(content).Where(delegate(Control control)
                    {
                        return control.Visible && (control is Button || control is RadioButton ||
                            control is CheckBox || control is ComboBox || control is NumericUpDown);
                    }))
                    {
                        content.ScrollControlIntoView(input);
                        Rectangle bounds = content.RectangleToClient(input.RectangleToScreen(input.ClientRectangle));
                        Check(content.ClientRectangle.Contains(bounds), "control cannot be reached by scrolling: " + input.Text);
                    }
                    content.AutoScrollPosition = Point.Empty;
                }
                if (dpi == 192 && mode == "attach" && !string.IsNullOrEmpty(captures))
                {
                    form.MaximumSize = new Size(6000, 6000);
                    form.ClientSize = new Size(1640, 1520);
                    fit.Invoke(form, new object[] { new Rectangle(0, 0, 2880, 1704) });
                    content.AutoScrollPosition = Point.Empty;
                    Capture(form, content, Path.Combine(captures, language + "-192.png"));
                    Control root = content.Controls[0];
                    using (var bitmap = new Bitmap(root.Width, root.Height))
                    {
                        root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, root.Size));
                        bitmap.Save(Path.Combine(captures, language + "-192-content.png"));
                    }
                }
                cases++;
            }
        }
        Console.WriteLine("PASS " + cases + " settings layouts: 100%-200%, 3 languages, 2 display modes; startup, containment, overlap, scrolling and footer checks.");
        typeof(SettingsLayoutSmoke).GetMethod("VerifyCallbacks", BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(settingsType).Invoke(null, new object[] { formType, i18n });
        Type updateType = assembly.GetType("CodexRateMonitorNative.UpdateForm", true);
        Type infoType = assembly.GetType("CodexRateMonitorNative.UpdateInfo", true);
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        foreach (int dpi in new int[] { 96, 120, 144, 168, 192 })
        {
            i18n.GetMethod("SetLanguage").Invoke(null, new object[] { language });
            object info = Activator.CreateInstance(infoType, true);
            infoType.GetProperty("Version").SetValue(info, "9.9.9", null);
            infoType.GetProperty("Notes").SetValue(info, "Layout regression sample", null);
            using (var form = (Form)Activator.CreateInstance(updateType, new object[] { info }))
            {
                using (Graphics graphics = form.CreateGraphics())
                    Check(Math.Abs(form.ClientSize.Width - 560f * graphics.DpiX / 96f) <= 1, "update dialog startup ignores DPI");
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                SimulateDpi(form, dpi, 560, 430);
                VerifyContent(form);
                foreach (Control control in form.Controls)
                    Check(form.ClientRectangle.Contains(control.Bounds), "update dialog control is outside the window");
            }
        }
        Console.WriteLine("PASS 15 update dialog layouts: 100%-200%, 3 languages.");
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\app.config') -Destination ($testExe + '.config') -Force
& $csc /nologo /target:exe /win32manifest:"$(Join-Path $repoRoot 'src\app.manifest')" /out:"$testExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $testSource
if ($LASTEXITCODE -ne 0) { throw 'Settings layout smoke test compilation failed.' }
& $testExe $monitorExe $CaptureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Settings layout smoke test failed.' }
