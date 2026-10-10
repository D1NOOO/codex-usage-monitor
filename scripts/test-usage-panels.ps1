[CmdletBinding()]
param([string]$MonitorPath = '', [string]$CaptureDirectory = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path (Join-Path $repoRoot '.build\usage-panels-test') (Split-Path (Split-Path $monitorExe -Parent) -Leaf)
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'UsagePanelsRegression.cs'
$testExe = Join-Path $testDirectory 'UsagePanelsRegression.exe'
@'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class UsagePanelsRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Assembly assembly;
    private static Type settingsType, snapshotType, windowType, rendererType, panelsType, previewType, overlayType;
    private static int checks;
    private static Exception nativeError;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private static object Get(object value, string name) { return value.GetType().GetProperty(name).GetValue(value, null); }
    private static void Set(object value, string name, object item) { value.GetType().GetProperty(name).SetValue(value, item, null); }
    private static object Field(object value, string name) { return value.GetType().GetField(name, PrivateInstance).GetValue(value); }
    private static object Call(object value, string name, params object[] args) { return value.GetType().GetMethod(name).Invoke(value, args); }
    private static object Static(Type type, string name, params object[] args) { return type.GetMethod(name).Invoke(null, args); }
    private static void Check(bool condition, string message)
    {
        if (nativeError != null) throw new Exception("Native window callback failed", nativeError);
        checks++;
        if (!condition) throw new Exception(message);
    }
    private static object Settings(string language, string lines)
    {
        object settings = Activator.CreateInstance(settingsType, true);
        Set(settings, "Language", language); Set(settings, "DisplayLines", lines);
        Set(settings, "ShowResetCredits", false);
        return settings;
    }
    private static object Window(long duration)
    {
        object window = Activator.CreateInstance(windowType, true);
        Set(window, "UsedPercent", 25d); Set(window, "WindowDurationMins", duration);
        Set(window, "ResetsAt", DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        return window;
    }
    private static object Snapshot(int windows, bool complete)
    {
        object snapshot = Activator.CreateInstance(snapshotType, true);
        if ((windows & 1) != 0) Set(snapshot, "Primary", Window(300));
        if ((windows & 2) != 0) Set(snapshot, "Secondary", Window(10080));
        Set(snapshot, "ReplaceMissingWindows", complete);
        return snapshot;
    }
    private static int Visible(object settings, object snapshot)
    {
        return (int)Static(panelsType, "GetVisibleWindows", settings, snapshot);
    }
    private static Bitmap Render(object settings, object snapshot, object credits, int dpi)
    {
        return (Bitmap)Static(rendererType, "CreateBitmap", settings, snapshot, credits, null, dpi);
    }
    private static Size Expected(string lines, int windows, bool credits, int dpi, double scale)
    {
        bool full = windows == 3;
        int width = lines == "2" ? 252 : (full ? (credits ? 702 : 470) : (credits ? 470 : 238));
        int height = lines == "2" ? (full ? (credits ? 106 : 78) : (credits ? 67 : 39)) : 40;
        float factor = (float)(scale * 0.85 * dpi / 96d);
        return new Size((int)Math.Round(width * factor), (int)Math.Round(height * factor));
    }
    private static void SettingsAndControls()
    {
        MethodInfo parse = settingsType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
        var json = new JavaScriptSerializer();
        foreach (string panelSetting in new string[] { "auto", "all", "weekly" })
        {
            object settings = Settings("en", "1");
            Set(settings, "UsagePanels", panelSetting);
            Check((string)Get(Call(settings, "Clone"), "UsagePanels") == panelSetting, "Clone lost the panel selection");
            object parsed = parse.Invoke(null, new object[] { json.Serialize(settings) });
            Check((string)Get(parsed, "UsagePanels") == panelSetting, "Save/reload lost the panel selection");
        }
        object legacy = parse.Invoke(null, new object[] { "{\"OverlayMode\":\"attach\",\"Position\":\"bottom-right\"}" });
        Check((string)Get(legacy, "UsagePanels") == "auto", "Old settings do not default to automatic panels");
        Check((string)Get(legacy, "DisplayLines") == "2", "Unspecified corner layout does not default to multi-row");
        object explicitRows = parse.Invoke(null, new object[] { "{\"OverlayMode\":\"attach\",\"Position\":\"bottom-right\",\"DisplayLines\":\"1\",\"UsagePanels\":\"all\"}" });
        Check((string)Get(explicitRows, "DisplayLines") == "1", "Reload overwrote a saved one-row choice");
        object invalid = parse.Invoke(null, new object[] { "{\"UsagePanels\":\"invalid\"}" });
        Check((string)Get(invalid, "UsagePanels") == "auto", "Invalid panel mode is not normalized");

        Type formType = assembly.GetType("CodexRateMonitorNative.AppearanceSettingsForm", true);
        Type i18n = assembly.GetType("CodexRateMonitorNative.I18n", true);
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        {
            Static(i18n, "SetLanguage", language);
            using (var form = (Form)Activator.CreateInstance(formType, new object[] { Settings(language, "1"), null, null, null }))
            {
                var attach = (RadioButton)Field(form, "attachModeRadio");
                var desktop = (RadioButton)Field(form, "desktopModeRadio");
                var bottom = (RadioButton)Field(form, "bottomPosition");
                var top = (RadioButton)Field(form, "topPosition");
                var one = (RadioButton)Field(form, "oneLineRadio");
                var two = (RadioButton)Field(form, "twoLinesRadio");
                var panels = (ComboBox)Field(form, "usagePanels");
                Check(panels.Items.Count == 3, "Panel selector must provide exactly three options");
                string[] keys = { "UsagePanelsAuto", "UsagePanelsAll", "UsagePanelsWeekly" };
                for (int index = 0; index < keys.Length; index++)
                    Check((string)panels.Items[index] == (string)Static(i18n, "Translate", keys[index], language), "Panel option is not localized");
                bottom.Checked = true;
                Check(one.Checked, "A desktop position change changed the line count");
                attach.Checked = true;
                Check(two.Checked && (string)Get(Field(form, "working"), "DisplayLines") == "2", "Selecting corner attachment did not default to multi-row");
                one.Checked = true;
                ((CheckBox)Field(form, "showResetCredits")).Checked = true;
                ((ComboBox)Field(form, "usagePanels")).SelectedIndex = 1;
                Check(one.Checked && (string)Get(Field(form, "working"), "DisplayLines") == "1", "An unrelated control overwrote manual one-row selection");
                Check((string)Get(Field(form, "working"), "UsagePanels") == "all", "Panel selector did not update the settings");
                panels.SelectedIndex = 2;
                Check((string)Get(Field(form, "working"), "UsagePanels") == "weekly" && one.Checked, "Weekly selection did not persist or overwrote the row choice");
                panels.SelectedIndex = 0;
                Check((string)Get(Field(form, "working"), "UsagePanels") == "auto", "Returning to automatic panels did not update settings");
                top.Checked = true;
                Check(one.Checked, "Changing to top attachment forced a row count");
                bottom.Checked = true;
                Check(two.Checked, "Reselecting bottom-right did not supply the default");
                one.Checked = true;
                desktop.Checked = true;
                attach.Checked = true;
                Check(two.Checked, "Switching to attachment at a saved bottom-right position did not supply the default");
            }
            using (var form = (Form)Activator.CreateInstance(formType, new object[] { explicitRows, null, null, null }))
                Check(((RadioButton)Field(form, "oneLineRadio")).Checked, "Opening settings overwrote an explicit one-row corner choice");
            object weeklySettings = Settings(language, "1");
            Set(weeklySettings, "UsagePanels", "weekly");
            using (var form = (Form)Activator.CreateInstance(formType, new object[] { weeklySettings, null, null, null }))
                Check(((ComboBox)Field(form, "usagePanels")).SelectedIndex == 2, "Reopening settings lost weekly selection");
        }
    }
    private static void StateTransitions()
    {
        object settings = Settings("en", "1");
        Type clientType = assembly.GetType("CodexRateMonitorNative.AppServerClient", true);
        using (var client = (IDisposable)Activator.CreateInstance(clientType, true))
        {
            object payload = new JavaScriptSerializer().DeserializeObject("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":10080},\"secondary\":null}}");
            object weekly = clientType.GetMethod("ParseReadResult", PrivateInstance).Invoke(client, new object[] { payload });
            Check(Get(weekly, "Primary") == null && Get(weekly, "Secondary") != null, "A weekly window in the primary slot was misidentified as 5h");
            Check(Visible(settings, weekly) == 2, "A complete weekly response did not select the weekly panel");
            object notification = clientType.GetMethod("ParseRateLimitsContainer", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { payload });
            Check(Visible(settings, notification) == 3, "An unconfirmed notification changed the panel layout");
        }
        using (var overlay = (Form)Activator.CreateInstance(overlayType, new object[] { settings }))
        {
            object push = Snapshot(2, false);
            Call(overlay, "SetSnapshot", push);
            Check(Visible(settings, push) == 3, "A partial initial push hid an unconfirmed window");
            object full = Snapshot(3, true);
            Call(overlay, "SetSnapshot", full);
            Size fullSize = overlay.Size;
            push = Snapshot(2, false);
            Call(overlay, "SetSnapshot", push);
            Check(overlay.Size == fullSize && Visible(settings, push) == 3 && Get(push, "Primary") != null, "A weekly-only push collapsed a confirmed Plus layout");
            object weekly = Snapshot(2, true);
            Call(overlay, "SetSnapshot", weekly);
            Size weeklySize = overlay.Size;
            Check(weeklySize.Width < fullSize.Width && Get(weekly, "Primary") == null, "A full weekly read retained stale 5h data or width");
            Call(overlay, "SetStatus", "Network unavailable");
            Check(overlay.Size == weeklySize && Field(overlay, "snapshot") == weekly, "A refresh error changed the cached layout/data");
            push = Snapshot(1, false);
            Call(overlay, "SetSnapshot", push);
            Check(overlay.Size == weeklySize && Visible(settings, push) == 2 && Get(push, "Secondary") != null, "A partial push changed the confirmed Pro layout");
            string summary = (string)Static(panelsType, "FormatSummary", settings, push);
            Check(summary == "7d 75%", "Automatic tray summary retained a hidden 5h panel: " + summary);
            Set(settings, "UsagePanels", "all"); Call(overlay, "ApplySettings", settings);
            Check(overlay.Size == fullSize, "Show-all did not restore full width");
            Call(overlay, "SetSnapshot", Snapshot(2, true));
            summary = (string)Static(panelsType, "FormatSummary", settings, Field(overlay, "snapshot"));
            Check(summary == "5h --% · 7d 75%", "Show-all does not retain the unavailable placeholder");
            Set(settings, "UsagePanels", "auto"); Call(overlay, "ApplySettings", settings);
            Call(overlay, "SetSnapshot", Snapshot(3, true));
            Check(overlay.Size == fullSize, "Returning 5h data did not restore the Plus layout");
            object cached = Field(overlay, "snapshot");
            Set(settings, "UsagePanels", "weekly"); Call(overlay, "ApplySettings", settings);
            Check(overlay.Size == weeklySize && Visible(settings, cached) == 2, "Weekly selection did not collapse a Plus account to the weekly panel");
            Check((string)Static(panelsType, "FormatSummary", settings, cached) == "7d 75%", "Weekly summary includes the hidden 5h window");
            Check(Field(overlay, "snapshot") == cached && Get(cached, "Primary") != null, "Weekly selection discarded cached 5h data");
            Call(overlay, "SetStatus", "Network unavailable");
            Check(overlay.Size == weeklySize, "A refresh error restored hidden 5h in weekly mode");
            Call(overlay, "SetSnapshot", Snapshot(1, true));
            Check(overlay.Size == weeklySize && (string)Static(panelsType, "FormatSummary", settings, Field(overlay, "snapshot")) == "7d --%", "Missing weekly data restored 5h or invented weekly usage");
            Call(overlay, "ClearSnapshot", "Reading usage");
            Check(overlay.Size == weeklySize && Visible(settings, null) == 2, "Loading/account reset lost the weekly selection");
            push = Snapshot(1, false); Call(overlay, "SetSnapshot", push);
            Check(overlay.Size == weeklySize && Visible(settings, push) == 2, "An initial 5h notification restored the hidden panel");
            Call(overlay, "SetSnapshot", Snapshot(3, true));
            Set(settings, "UsagePanels", "all"); Call(overlay, "ApplySettings", settings);
            Check(overlay.Size == fullSize && (string)Static(panelsType, "FormatSummary", settings, Field(overlay, "snapshot")) == "5h 75% · 7d 75%", "Switching back to all lost cached 5h data");
            Set(settings, "UsagePanels", "auto"); Call(overlay, "ApplySettings", settings);
            Call(overlay, "SetSnapshot", Snapshot(2, true));
            Call(overlay, "ClearSnapshot", "Reading usage");
            Check(overlay.Size == fullSize && Field(overlay, "snapshot") == null, "Account reset retained the old account's panel availability");
            push = Snapshot(2, false); Call(overlay, "SetSnapshot", push);
            Check(Visible(settings, push) == 3, "New-account notification inherited old-account panel availability");
        }
    }
    private static void GeometryAndPreview(string captures)
    {
        foreach (string language in new string[] { "zh-CN", "zh-TW", "en" })
        foreach (string lines in new string[] { "1", "2" })
        foreach (string panelSetting in new string[] { "auto", "all", "weekly" })
        foreach (int windows in new int[] { 1, 2, 3 })
        foreach (bool creditsVisible in new bool[] { false, true })
        foreach (int dpi in new int[] { 96, 120, 144, 192 })
        foreach (double scale in new double[] { 0.5, 1d, 2d })
        {
            object settings = Settings(language, lines);
            Set(settings, "UsagePanels", panelSetting);
            Set(settings, "ShowResetCredits", creditsVisible); Set(Get(settings, "Style"), "Scale", scale);
            object snapshot = Snapshot(windows, true);
            using (var preview = (Control)Activator.CreateInstance(previewType, true))
            {
                object credits = Field(preview, "sampleCredits");
                int visible = panelSetting == "weekly" ? 2 : panelSetting == "all" ? 3 : windows;
                Check(Visible(settings, snapshot) == visible, "Panel selection did not choose the requested windows");
                Size expected = Expected(lines, visible, creditsVisible, dpi, scale);
                using (Bitmap bitmap = Render(settings, snapshot, credits, dpi))
                {
                    Check(bitmap.Size == expected, "Wrong adaptive bitmap size: " + language + "," + lines + "," + windows + "," + dpi + ": " + bitmap.Size + " vs " + expected);
                    Check(bitmap.GetPixel(0, 0).A == 0, "Adaptive card corner lost its transparency");
                    if (!string.IsNullOrEmpty(captures) && language == "zh-CN" && dpi == 192 && scale == 1d && windows != 1)
                        bitmap.Save(Path.Combine(captures, (windows == 3 ? "plus" : "pro") + "-" + panelSetting + "-" + lines + "-" + (creditsVisible ? "credits" : "quota") + ".png"));
                }
                Set(preview, "Settings", settings); Set(preview, "TargetDpi", dpi);
                Call(preview, "SetUsageSnapshot", snapshot);
                preview.Size = new Size(expected.Width + 80, expected.Height + 128);
                Check(((Rectangle)Call(preview, "GetOverlayBounds")).Size == expected, "Preview retained the full-size geometry for a collapsed overlay");
            }
        }
    }
    private static void AttachmentAnchors()
    {
        object settings = Settings("en", "2");
        Set(settings, "OverlayMode", "attach"); Set(settings, "Position", "bottom-right");
        using (var target = new Form())
        using (var overlay = (Form)Activator.CreateInstance(overlayType, new object[] { settings }))
        {
            target.Bounds = new Rectangle(-20000, -20000, 1200, 800);
            Call(overlay, "SetSnapshot", Snapshot(3, true));
            Call(overlay, "AttachTo", target.Handle);
            Rect initial; GetWindowRect(overlay.Handle, out initial);
            Call(overlay, "SetSnapshot", Snapshot(2, true));
            Rect collapsed; GetWindowRect(overlay.Handle, out collapsed);
            Check(initial.Right == collapsed.Right && initial.Bottom == collapsed.Bottom, "Collapsing panels moved the bottom-right attachment anchor");
            Check(collapsed.Bottom - collapsed.Top < initial.Bottom - initial.Top, "Stacked Pro layout did not reduce height");
            Set(settings, "UsagePanels", "all"); Call(overlay, "ApplySettings", settings);
            Rect restored; GetWindowRect(overlay.Handle, out restored);
            Check(initial.Right == restored.Right && initial.Bottom == restored.Bottom, "Show-all moved the attachment anchor");
            Check(restored.Bottom - restored.Top == initial.Bottom - initial.Top, "Show-all did not restore attachment height");
            Call(overlay, "SetSnapshot", Snapshot(3, true));
            Set(settings, "UsagePanels", "weekly"); Call(overlay, "ApplySettings", settings);
            Rect weekly; GetWindowRect(overlay.Handle, out weekly);
            Check(initial.Right == weekly.Right && initial.Bottom == weekly.Bottom, "Weekly selection moved the attachment anchor");
            Check(weekly.Bottom - weekly.Top == collapsed.Bottom - collapsed.Top, "Weekly Plus layout retained a hidden 5h row");
        }
    }
    [STAThread] private static void Main(string[] args)
    {
        IntPtr foreground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { nativeError = e.Exception; };
            assembly = Assembly.LoadFrom(args[0]);
            settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
            snapshotType = assembly.GetType("CodexRateMonitorNative.RateSnapshot", true);
            windowType = assembly.GetType("CodexRateMonitorNative.WindowUsage", true);
            rendererType = assembly.GetType("CodexRateMonitorNative.OverlayRenderer", true);
            panelsType = assembly.GetType("CodexRateMonitorNative.UsagePanelTools", true);
            previewType = assembly.GetType("CodexRateMonitorNative.OverlayPreviewControl", true);
            overlayType = assembly.GetType("CodexRateMonitorNative.OverlayForm", true);
            string captures = args.Length > 1 ? args[1] : "";
            if (!string.IsNullOrEmpty(captures)) Directory.CreateDirectory(captures);
            SettingsAndControls(); StateTransitions(); GeometryAndPreview(captures); AttachmentAnchors();
            Console.WriteLine("PASS " + checks + " usage-panel assertions: full/partial reads, errors, account changes, tray output, 3 languages, rows, credits, DPI/scale, previews and corner defaults/anchors.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
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
& $csc /nologo /target:exe /win32manifest:"$(Join-Path $repoRoot 'src\app.manifest')" /out:"$testExe" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $testSource
if ($LASTEXITCODE -ne 0) { throw 'Usage panel test compilation failed.' }
& $testExe $monitorExe $CaptureDirectory
if ($LASTEXITCODE -ne 0) { throw 'Usage panel regression failed.' }
