using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexRateMonitorNative
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\CodexRateMonitorNative", out created))
            {
                if (!created)
                    return;

                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool showSettings = args != null &&
                    args.Any(delegate(string value)
                    {
                        return string.Equals(value, "--settings", StringComparison.OrdinalIgnoreCase);
                    });
                using (var context = new MonitorContext(showSettings))
                    Application.Run(context);
            }
        }
    }

    internal sealed class MonitorContext : ApplicationContext, IDisposable
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "Codex Rate Monitor";

        private readonly OverlayForm overlay;
        private readonly NotifyIcon trayIcon;
        private readonly Icon applicationIcon;
        private readonly System.Windows.Forms.Timer timer;
        private readonly AppServerClient appServer;
        private ToolStripMenuItem startupItem;
        private ContextMenuStrip trayMenu;
        private MonitorSettings settings;
        private AppearanceSettingsForm appearanceForm;
        private DateTime lastRequest = DateTime.MinValue;
        private bool disposed;

        public MonitorContext(bool showSettings)
        {
            settings = MonitorSettings.Load();
            I18n.SetLanguage(settings.Language);
            overlay = new OverlayForm(settings);
            IntPtr ignored = overlay.Handle;

            appServer = new AppServerClient();
            appServer.SnapshotReceived += OnSnapshotReceived;
            appServer.StatusChanged += OnStatusChanged;

            applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            trayIcon = new NotifyIcon();
            trayIcon.Icon = applicationIcon ?? SystemIcons.Application;
            trayIcon.Text = I18n.T("AppTitle");
            trayMenu = BuildTrayMenu();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { ShowAppearanceSettings(); };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += OnTimerTick;
            timer.Start();

            if (showSettings)
                ShowAppearanceSettings();
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(I18n.T("AppearanceMenu"), null, delegate { ShowAppearanceSettings(); });
            menu.Items.Add(I18n.T("RefreshNow"), null, delegate { RequestRateLimits(); });
            menu.Items.Add(I18n.T("ReloadStyle"), null, delegate { ReloadSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            var topItem = new ToolStripMenuItem(I18n.T("TopPosition"));
            topItem.Click += delegate { SetPosition("top"); };
            menu.Items.Add(topItem);
            var bottomItem = new ToolStripMenuItem(I18n.T("BottomPosition"));
            bottomItem.Click += delegate { SetPosition("bottom-right"); };
            menu.Items.Add(bottomItem);
            menu.Items.Add(new ToolStripSeparator());
            startupItem = new ToolStripMenuItem(I18n.T("Startup"));
            startupItem.Checked = IsStartupEnabled();
            startupItem.Click += delegate { ToggleStartup(); };
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(I18n.T("Exit"), null, delegate { ExitMonitor(); });
            return menu;
        }

        private void RefreshTrayLanguage()
        {
            ContextMenuStrip old = trayMenu;
            trayMenu = BuildTrayMenu();
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Text = I18n.T("AppTitle");
            if (old != null)
                old.Dispose();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            IntPtr desktopWindow = WindowLocator.FindForegroundDesktopMainWindow();
            bool desktopIsForeground = desktopWindow != IntPtr.Zero &&
                                       !NativeMethods.IsIconic(desktopWindow);

            if (desktopIsForeground)
            {
                overlay.AttachTo(desktopWindow);
                if (!appServer.IsRunning)
                    StartAppServer();
            }
            else
            {
                overlay.Hide();
            }

            if (appServer.IsInitialized &&
                (DateTime.Now - lastRequest).TotalSeconds >= settings.RefreshSeconds)
            {
                RequestRateLimits();
            }
        }

        private void StartAppServer()
        {
            overlay.SetStatus(I18n.T("Connecting"));
            try
            {
                appServer.Start();
            }
            catch (Exception ex)
            {
                overlay.SetStatus(I18n.T("ServiceError"));
                trayIcon.Text = SafeTrayText(I18n.F("StartFailed", ex.Message));
            }
        }

        private void RequestRateLimits()
        {
            if (!appServer.IsRunning)
            {
                if (WindowLocator.FindDesktopMainWindow() != IntPtr.Zero)
                    StartAppServer();
                return;
            }

            if (!appServer.IsInitialized)
                return;

            lastRequest = DateTime.Now;
            appServer.RequestRateLimits();
        }

        private void OnSnapshotReceived(RateSnapshot snapshot)
        {
            Ui(delegate
            {
                overlay.SetSnapshot(snapshot);
                trayIcon.Text = SafeTrayText(
                    string.Format(CultureInfo.InvariantCulture,
                        I18n.T("UsageTray"),
                        I18n.T(UsageDisplayTools.IsRemaining(settings.UsageDisplay)
                            ? "Remaining"
                            : "Used"),
                        snapshot.Primary == null
                            ? "--%"
                            : UsageDisplayTools.FormatPercent(
                                UsageDisplayTools.GetDisplayedPercent(
                                    snapshot.Primary.UsedPercent, settings.UsageDisplay)),
                        snapshot.Secondary == null
                            ? "--%"
                            : UsageDisplayTools.FormatPercent(
                                UsageDisplayTools.GetDisplayedPercent(
                                    snapshot.Secondary.UsedPercent, settings.UsageDisplay))));
            });
        }

        private void OnStatusChanged(string status)
        {
            Ui(delegate
            {
                overlay.SetStatus(status);
                trayIcon.Text = SafeTrayText(I18n.F("TrayStatus", status));
            });
        }

        private void Ui(Action action)
        {
            if (disposed)
                return;
            try
            {
                if (overlay.InvokeRequired)
                    overlay.BeginInvoke(action);
                else
                    action();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static string SafeTrayText(string value)
        {
            if (value.Length > 63)
                return value.Substring(0, 63);
            return value;
        }

        private void SetPosition(string position)
        {
            settings.Position = position;
            settings.Save();
            overlay.ApplySettings(settings);
        }

        private void ReloadSettings()
        {
            settings = MonitorSettings.Load();
            I18n.SetLanguage(settings.Language);
            RefreshTrayLanguage();
            overlay.ApplySettings(settings);
            trayIcon.Text = I18n.T("StyleReloaded");
        }

        private void ShowAppearanceSettings()
        {
            if (appearanceForm != null && !appearanceForm.IsDisposed)
            {
                appearanceForm.Activate();
                return;
            }

            MonitorSettings original = settings.Clone();
            appearanceForm = new AppearanceSettingsForm(
                settings.Clone(),
                delegate(MonitorSettings preview)
                {
                    overlay.ApplySettings(preview);
                },
                delegate(MonitorSettings saved)
                {
                    settings = saved.Clone();
                    I18n.SetLanguage(settings.Language);
                    settings.Save();
                    RefreshTrayLanguage();
                    overlay.ApplySettings(settings);
                    trayIcon.Text = I18n.T("AppearanceSaved");
                },
                delegate
                {
                    overlay.ApplySettings(original);
                });
            appearanceForm.FormClosed += delegate { appearanceForm = null; };
            appearanceForm.Show();
            appearanceForm.Activate();
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
            {
                string value = key == null ? null : key.GetValue(StartupValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        private void ToggleStartup()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            {
                if (startupItem.Checked)
                {
                    key.DeleteValue(StartupValueName, false);
                    startupItem.Checked = false;
                }
                else
                {
                    string executable = Application.ExecutablePath;
                    key.SetValue(StartupValueName, "\"" + executable + "\"", RegistryValueKind.String);
                    startupItem.Checked = true;
                }
            }
        }

        private void ExitMonitor()
        {
            Dispose();
            ExitThread();
        }

        protected override void ExitThreadCore()
        {
            Dispose();
            base.ExitThreadCore();
        }

        public new void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            if (applicationIcon != null)
                applicationIcon.Dispose();
            if (appearanceForm != null && !appearanceForm.IsDisposed)
                appearanceForm.Close();
            appServer.Dispose();
            overlay.Close();
            overlay.Dispose();
        }
    }

    internal sealed class OverlayForm : Form
    {
        private MonitorSettings settings;
        private RateSnapshot snapshot;
        private string status = I18n.T("Connecting");

        public OverlayForm(MonitorSettings initialSettings)
        {
            settings = initialSettings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            ApplySettings(initialSettings);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_TOOLWINDOW = 0x80;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void ApplySettings(MonitorSettings value)
        {
            settings = value;
            Opacity = settings.Style.Opacity;
            int width = (int)Math.Round(
                (settings.Position == "bottom-right" ? DrawingHelpers.BottomRightWidth : 470) *
                settings.Style.Scale);
            int height = (int)Math.Round(
                (settings.Position == "bottom-right" ? DrawingHelpers.BottomRightHeight : 40) *
                settings.Style.Scale);
            Size = new Size(width, height);
            UpdateRegion();
            Invalidate();
        }

        public void SetSnapshot(RateSnapshot value)
        {
            if (snapshot != null && value != null)
            {
                if (value.Primary == null)
                    value.Primary = snapshot.Primary;
                if (value.Secondary == null)
                    value.Secondary = snapshot.Secondary;
                if (string.IsNullOrEmpty(value.PlanType))
                    value.PlanType = snapshot.PlanType;
            }
            snapshot = value;
            status = null;
            Invalidate();
        }

        public void SetStatus(string value)
        {
            if (snapshot == null)
                status = value;
            Invalidate();
        }

        public void AttachTo(IntPtr codexWindow)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(codexWindow, out rect))
            {
                Hide();
                return;
            }

            int x;
            int y;
            if (settings.Position == "bottom-right")
            {
                x = rect.Right - Width - 12;
                y = rect.Bottom - Height - 12;
            }
            else
            {
                x = rect.Left + ((rect.Right - rect.Left) - Width) / 2;
                y = rect.Top + 4;
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;
            float radius = (float)(settings.Style.CornerRadius * settings.Style.Scale);
            using (GraphicsPath path = DrawingHelpers.RoundRect(
                new RectangleF(0, 0, Width, Height), radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null)
                    old.Dispose();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float scale = (float)settings.Style.Scale;
            g.ScaleTransform(scale, scale);

            Color outer = ColorTools.Parse(settings.Style.Background);
            Color border = ColorTools.Parse(settings.Style.Border);
            Color card = ColorTools.Parse(settings.Style.CardBackground);
            Color text = ColorTools.Parse(settings.Style.Text);
            Color muted = ColorTools.Parse(settings.Style.MutedText);
            Color track = ColorTools.Parse(settings.Style.Track);

            using (var outerBrush = new SolidBrush(outer))
            using (var borderPen = new Pen(border, 1f))
            using (GraphicsPath outerPath = DrawingHelpers.RoundRect(
                new RectangleF(0.5f, 0.5f,
                    (Width / scale) - 1f, (Height / scale) - 1f),
                (float)settings.Style.CornerRadius))
            {
                g.FillPath(outerBrush, outerPath);
                g.DrawPath(borderPen, outerPath);
            }

            if (settings.Position == "bottom-right")
            {
                DrawCard(g, DrawingHelpers.GetBottomRightCardBounds(true),
                    true, card, text, muted, track);
                DrawCard(g, DrawingHelpers.GetBottomRightCardBounds(false),
                    false, card, text, muted, track);
            }
            else
            {
                DrawCard(g, new RectangleF(5, 5, 228, 30), true, card, text, muted, track);
                DrawCard(g, new RectangleF(237, 5, 228, 30), false, card, text, muted, track);
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
            string label = primary ? I18n.T("FiveHour") : I18n.T("SevenDay");
            double value = usage == null
                ? 0d
                : UsageDisplayTools.GetDisplayedPercent(
                    usage.UsedPercent, settings.UsageDisplay);
            string percent = usage == null ? "--%" :
                UsageDisplayTools.FormatPercent(value);
            string reset = usage == null ? (status ?? I18n.T("Connecting")) : FormatReset(usage.ResetsAt);

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
            using (var labelFont = new Font(family, mainFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var percentFont = new Font(
                family,
                mainFontSize + DrawingHelpers.PercentOpticalSizeOffset,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            using (var resetFont = new Font(family, resetFontSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(text))
            using (var mutedBrush = new SolidBrush(muted))
            {
                DrawingHelpers.DrawUsageText(
                    g, bounds, label, percent, reset,
                    labelFont, percentFont, resetFont, textBrush, mutedBrush);
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

        private static string FormatReset(long? unixSeconds)
        {
            if (!unixSeconds.HasValue)
                return "--";
            DateTime local = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).LocalDateTime;
            return I18n.FormatDate(local);
        }
    }

    internal sealed class AppServerClient : IDisposable
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object writeLock = new object();
        private readonly object requestLock = new object();
        private readonly HashSet<int> rateLimitRequests = new HashSet<int>();
        private Process process;
        private Thread outputThread;
        private Thread errorThread;
        private int requestId = 10;
        private bool disposed;

        public event Action<RateSnapshot> SnapshotReceived;
        public event Action<string> StatusChanged;

        public bool IsInitialized { get; private set; }

        public bool IsRunning
        {
            get
            {
                try
                {
                    return process != null && !process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Start()
        {
            if (IsRunning)
                return;

            if (process != null)
            {
                process.Dispose();
                process = null;
            }

            Exception lastError = null;
            foreach (CodexExecutable candidate in NativeCodexResolver.FindCandidates())
            {
                try
                {
                    StartCandidate(candidate);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    DisposeProcess();
                }
            }

            if (lastError == null)
                throw new FileNotFoundException(I18n.T("CliMissing"));

            throw new InvalidOperationException(
                I18n.T("CliMissing") + " " + lastError.Message, lastError);
        }

        private void StartCandidate(CodexExecutable candidate)
        {
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.FileName))
                throw new FileNotFoundException(I18n.T("CliMissing"));

            var startInfo = new ProcessStartInfo();
            startInfo.FileName = candidate.FileName;
            startInfo.Arguments = candidate.Arguments;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            process = Process.Start(startInfo);
            IsInitialized = false;

            outputThread = new Thread(ReadOutput);
            outputThread.IsBackground = true;
            outputThread.Name = "Codex app-server output";
            outputThread.Start();

            errorThread = new Thread(DrainErrors);
            errorThread.IsBackground = true;
            errorThread.Name = "Codex app-server errors";
            errorThread.Start();

            var initialize = new Dictionary<string, object>();
            initialize["method"] = "initialize";
            initialize["id"] = 1;
            var clientInfo = new Dictionary<string, object>();
            clientInfo["name"] = "codex-rate-monitor-native";
            clientInfo["title"] = "Codex Rate Monitor";
            clientInfo["version"] = BuildVersion.Value;
            var capabilities = new Dictionary<string, object>();
            capabilities["experimentalApi"] = true;
            capabilities["requestAttestation"] = false;
            capabilities["optOutNotificationMethods"] = new[]
            {
                "thread/started",
                "thread/status/changed",
                "thread/tokenUsage/updated"
            };
            var parameters = new Dictionary<string, object>();
            parameters["clientInfo"] = clientInfo;
            parameters["capabilities"] = capabilities;
            initialize["params"] = parameters;
            Send(initialize);
        }

        public void RequestRateLimits()
        {
            if (!IsInitialized)
                return;
            int id = Interlocked.Increment(ref requestId);
            lock (requestLock)
                rateLimitRequests.Add(id);
            var message = new Dictionary<string, object>();
            message["method"] = "account/rateLimits/read";
            message["id"] = id;
            message["params"] = new Dictionary<string, object>();
            Send(message);
        }

        private void ReadOutput()
        {
            try
            {
                string line;
                while (!disposed && process != null &&
                       (line = process.StandardOutput.ReadLine()) != null)
                {
                    ProcessMessage(line);
                }
            }
            catch (Exception ex)
            {
                RaiseStatus(I18n.F("CommunicationError", ex.Message));
            }
            finally
            {
                IsInitialized = false;
            }
        }

        private void DrainErrors()
        {
            try
            {
                while (!disposed && process != null &&
                       process.StandardError.ReadLine() != null)
                {
                }
            }
            catch
            {
            }
        }

        private void ProcessMessage(string line)
        {
            Dictionary<string, object> message;
            try
            {
                message = json.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                return;
            }
            if (message == null)
                return;

            int id;
            if (TryInt(message, "id", out id) && id == 1)
            {
                if (message.ContainsKey("error"))
                {
                    RaiseStatus(I18n.T("InitializationFailed"));
                    return;
                }
                var initialized = new Dictionary<string, object>();
                initialized["method"] = "initialized";
                Send(initialized);
                IsInitialized = true;
                RequestRateLimits();
                return;
            }

            bool isRateResponse = false;
            if (TryInt(message, "id", out id))
            {
                lock (requestLock)
                {
                    if (rateLimitRequests.Contains(id))
                    {
                        rateLimitRequests.Remove(id);
                        isRateResponse = true;
                    }
                }
            }

            if (isRateResponse)
            {
                if (message.ContainsKey("error"))
                {
                    RaiseStatus(GetRateLimitErrorStatus(message));
                    return;
                }
                var result = GetDictionary(message, "result");
                RateSnapshot snapshot = ParseReadResult(result);
                if (snapshot != null)
                    RaiseSnapshot(snapshot);
                return;
            }

            string method = GetString(message, "method");
            if (method == "account/rateLimits/updated")
            {
                var parameters = GetDictionary(message, "params");
                RateSnapshot snapshot = ParseRateLimitsContainer(parameters);
                if (snapshot != null)
                    RaiseSnapshot(snapshot);
            }
        }

        private RateSnapshot ParseReadResult(Dictionary<string, object> result)
        {
            return ParseRateLimitsContainer(result);
        }

        private static RateSnapshot ParseRateLimitsContainer(Dictionary<string, object> source)
        {
            if (source == null)
                return null;

            RateSnapshot snapshot = ParseSnapshot(GetDictionary(source, "rateLimits"));
            if (HasUsageWindow(snapshot))
                return snapshot;

            snapshot = ParseByLimitId(GetDictionary(source, "rateLimitsByLimitId"));
            if (HasUsageWindow(snapshot))
                return snapshot;

            snapshot = ParseSnapshot(source);
            if (HasUsageWindow(snapshot))
                return snapshot;

            return ParseByLimitId(source);
        }

        private static RateSnapshot ParseByLimitId(Dictionary<string, object> byId)
        {
            if (byId == null)
                return null;

            RateSnapshot snapshot = ParseSnapshot(GetDictionary(byId, "codex"));
            if (HasUsageWindow(snapshot))
                return snapshot;

            foreach (object raw in byId.Values)
            {
                snapshot = ParseSnapshot(raw as Dictionary<string, object>);
                if (HasUsageWindow(snapshot))
                    return snapshot;
            }

            return null;
        }

        private static bool HasUsageWindow(RateSnapshot snapshot)
        {
            return snapshot != null && (snapshot.Primary != null || snapshot.Secondary != null);
        }

        private static string GetRateLimitErrorStatus(Dictionary<string, object> message)
        {
            var error = GetDictionary(message, "error");
            string text = GetString(error, "message") ?? string.Empty;
            if (text.IndexOf("chatgpt authentication required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("api key auth is not supported", StringComparison.OrdinalIgnoreCase) >= 0)
                return I18n.T("ChatGptAuthRequired");

            return I18n.T("NotSignedIn");
        }

        private static RateSnapshot ParseSnapshot(Dictionary<string, object> source)
        {
            if (source == null)
                return null;
            var snapshot = new RateSnapshot();
            snapshot.Primary = ParseWindow(GetDictionary(source, "primary"));
            snapshot.Secondary = ParseWindow(GetDictionary(source, "secondary"));
            snapshot.PlanType = GetString(source, "planType");
            return snapshot;
        }

        private static WindowUsage ParseWindow(Dictionary<string, object> source)
        {
            if (source == null)
                return null;
            var usage = new WindowUsage();
            usage.UsedPercent = GetDouble(source, "usedPercent");
            long value;
            if (TryLong(source, "resetsAt", out value))
                usage.ResetsAt = value;
            return usage;
        }

        private void Send(Dictionary<string, object> message)
        {
            lock (writeLock)
            {
                if (!IsRunning)
                    return;
                process.StandardInput.WriteLine(json.Serialize(message));
                process.StandardInput.Flush();
            }
        }

        private void RaiseSnapshot(RateSnapshot snapshot)
        {
            Action<RateSnapshot> handler = SnapshotReceived;
            if (handler != null)
                handler(snapshot);
        }

        private void RaiseStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
                handler(status);
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> source, string key)
        {
            if (source == null)
                return null;
            object value;
            if (!source.TryGetValue(key, out value) || value == null)
                return null;
            return value as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null)
                return null;
            object value;
            return source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture)
                : null;
        }

        private static bool TryInt(Dictionary<string, object> source, string key, out int value)
        {
            value = 0;
            if (source == null)
                return false;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            try
            {
                value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLong(Dictionary<string, object> source, string key, out long value)
        {
            value = 0;
            if (source == null)
                return false;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            try
            {
                value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double GetDouble(Dictionary<string, object> source, string key)
        {
            if (source == null)
                return 0;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return 0;
            try
            {
                return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            IsInitialized = false;

            DisposeProcess();
        }

        private void DisposeProcess()
        {
            IsInitialized = false;

            try
            {
                if (process != null && !process.HasExited)
                {
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1000))
                        process.Kill();
                }
            }
            catch
            {
            }
            if (process != null)
                process.Dispose();
            process = null;
        }
    }

    internal sealed class CodexExecutable
    {
        public string FileName { get; private set; }
        public string Arguments { get; private set; }

        public CodexExecutable(string fileName, string arguments)
        {
            FileName = fileName;
            Arguments = arguments;
        }
    }

    internal static class NativeCodexResolver
    {
        public static IEnumerable<CodexExecutable> FindCandidates()
        {
            string bundled = FindBundledInDesktopApp();
            if (!string.IsNullOrEmpty(bundled))
                yield return DirectExecutable(bundled);

            foreach (string directory in CandidatePathDirectories())
            {
                string native = FindNativeUnderNpmDirectory(directory);
                if (!string.IsNullOrEmpty(native))
                    yield return DirectExecutable(native);
            }

            foreach (string directory in CandidatePathDirectories())
            {
                string executable = Path.Combine(directory, "codex.exe");
                if (File.Exists(executable) &&
                    executable.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
                    yield return DirectExecutable(executable);

                string command = Path.Combine(directory, "codex.cmd");
                if (File.Exists(command))
                    yield return CmdWrapper(command);

                string script = Path.Combine(directory, "codex.ps1");
                if (File.Exists(script))
                    yield return PowerShellWrapper(script);
            }
        }

        private static CodexExecutable DirectExecutable(string path)
        {
            return new CodexExecutable(path, "app-server");
        }

        private static CodexExecutable CmdWrapper(string path)
        {
            return new CodexExecutable(
                Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                "/d /c " + Quote(path) + " app-server");
        }

        private static CodexExecutable PowerShellWrapper(string path)
        {
            string powerShell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powerShell))
                powerShell = "powershell.exe";

            return new CodexExecutable(
                powerShell,
                "-NoProfile -ExecutionPolicy Bypass -File " + Quote(path) + " app-server");
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FindBundledInDesktopApp()
        {
            foreach (Process process in DesktopAppProcess.GetRunningProcesses())
            {
                try
                {
                    string path = process.MainModule == null ? null : process.MainModule.FileName;
                    string executable = FindBundledNearExecutable(path);
                    if (!string.IsNullOrEmpty(executable))
                        return executable;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return null;
        }

        private static string FindBundledNearExecutable(string desktopExecutable)
        {
            if (string.IsNullOrWhiteSpace(desktopExecutable))
                return null;
            string directory;
            try
            {
                directory = Path.GetDirectoryName(desktopExecutable);
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(directory))
                return null;

            foreach (string candidate in new[]
            {
                Path.Combine(directory, "resources", "codex.exe"),
                Path.Combine(directory, "codex.exe")
            })
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IEnumerable<string> CandidatePathDirectories()
        {
            var values = new List<string>();
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            values.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                values.Add(Path.Combine(appData, "npm"));

            return values
                .Select(delegate(string value) { return value.Trim().Trim('"'); })
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string FindNativeUnderNpmDirectory(string commandDirectory)
        {
            if (!File.Exists(Path.Combine(commandDirectory, "codex.cmd")) &&
                !Directory.Exists(Path.Combine(commandDirectory, "node_modules", "@openai", "codex")))
                return null;

            string packageRoot = Path.Combine(
                commandDirectory, "node_modules", "@openai", "codex", "node_modules");
            if (!Directory.Exists(packageRoot))
                return null;

            try
            {
                string[] files = Directory.GetFiles(packageRoot, "codex.exe", SearchOption.AllDirectories);
                return files.FirstOrDefault(delegate(string file)
                {
                    return file.IndexOf(@"\vendor\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           file.EndsWith(@"\bin\codex.exe", StringComparison.OrdinalIgnoreCase);
                });
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class DesktopAppProcess
    {
        private static readonly string[] ProcessNames = { "ChatGPT", "Codex" };

        public static IEnumerable<Process> GetRunningProcesses()
        {
            foreach (string processName in ProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (Process process in processes)
                    yield return process;
            }
        }

        public static bool IsDesktopAppProcess(Process process)
        {
            if (process == null)
                return false;

            string name;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
                return false;
            }

            if (string.Equals(name, "Codex", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(name, "ChatGPT", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class WindowLocator
    {
        public static IntPtr FindDesktopMainWindow()
        {
            Process selected = null;
            try
            {
                foreach (Process process in DesktopAppProcess.GetRunningProcesses())
                {
                    bool candidate;
                    try
                    {
                        candidate = DesktopAppProcess.IsDesktopAppProcess(process) &&
                                    process.MainWindowHandle != IntPtr.Zero;
                    }
                    catch
                    {
                        candidate = false;
                    }

                    if (!candidate)
                    {
                        process.Dispose();
                        continue;
                    }

                    if (selected == null || CompareStartTime(process, selected) > 0)
                    {
                        if (selected != null)
                            selected.Dispose();
                        selected = process;
                    }
                    else
                    {
                        process.Dispose();
                    }
                }

                if (selected != null)
                    return selected.MainWindowHandle;

                return FindEnumeratedDesktopWindow();
            }
            finally
            {
                if (selected != null)
                    selected.Dispose();
            }
        }

        private static IntPtr FindEnumeratedDesktopWindow()
        {
            var processIds = new HashSet<uint>();
            foreach (Process process in DesktopAppProcess.GetRunningProcesses())
            {
                try
                {
                    if (DesktopAppProcess.IsDesktopAppProcess(process))
                        processIds.Add((uint)process.Id);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (processIds.Count == 0)
                return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(window))
                    return true;

                uint processId;
                NativeMethods.GetWindowThreadProcessId(window, out processId);
                if (!processIds.Contains(processId))
                    return true;

                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static IntPtr FindForegroundDesktopMainWindow()
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return IntPtr.Zero;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(foreground, out processId);
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    if (!DesktopAppProcess.IsDesktopAppProcess(process))
                        return IntPtr.Zero;
                    return process.MainWindowHandle == IntPtr.Zero
                        ? foreground
                        : process.MainWindowHandle;
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static int CompareStartTime(Process left, Process right)
        {
            DateTime leftStart;
            DateTime rightStart;
            try { leftStart = left.StartTime; }
            catch { leftStart = DateTime.MinValue; }
            try { rightStart = right.StartTime; }
            catch { rightStart = DateTime.MinValue; }
            return leftStart.CompareTo(rightStart);
        }
    }

    internal sealed class MonitorSettings
    {
        public string Language { get; set; }
        public string Position { get; set; }
        public string UsageDisplay { get; set; }
        public int RefreshSeconds { get; set; }
        public StyleSettings Style { get; set; }

        public MonitorSettings()
        {
            Language = "auto";
            Position = "bottom-right";
            UsageDisplay = "remaining";
            RefreshSeconds = 60;
            Style = new StyleSettings();
        }

        public static string SettingsPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"); }
        }

        public static MonitorSettings Load()
        {
            MonitorSettings result = new MonitorSettings();
            if (!File.Exists(SettingsPath))
                return result;
            try
            {
                string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                var loaded = new JavaScriptSerializer().Deserialize<MonitorSettings>(text);
                if (loaded != null)
                    result = loaded;
            }
            catch
            {
            }
            result.Normalize();
            return result;
        }

        public void Save()
        {
            Normalize();
            try
            {
                string json = new JavaScriptSerializer().Serialize(this);
                File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public MonitorSettings Clone()
        {
            var clone = new MonitorSettings();
            clone.Language = Language;
            clone.Position = Position;
            clone.UsageDisplay = UsageDisplay;
            clone.RefreshSeconds = RefreshSeconds;
            clone.Style = Style == null ? new StyleSettings() : Style.Clone();
            clone.Normalize();
            return clone;
        }

        private void Normalize()
        {
            Language = I18n.NormalizeSetting(Language);
            if (Position == "bottom-left")
                Position = "bottom-right";
            if (Position != "top" && Position != "bottom-right")
                Position = "top";
            UsageDisplay = UsageDisplayTools.Normalize(UsageDisplay);
            RefreshSeconds = Math.Max(30, Math.Min(900, RefreshSeconds));
            if (Style == null)
                Style = new StyleSettings();
            Style.Normalize();
        }
    }

    internal sealed class StyleSettings
    {
        public double Scale { get; set; }
        public double Opacity { get; set; }
        public double CornerRadius { get; set; }
        public double FontSize { get; set; }
        public double ResetFontSize { get; set; }
        public string FontFamily { get; set; }
        public string Background { get; set; }
        public string CardBackground { get; set; }
        public string Border { get; set; }
        public string Text { get; set; }
        public string MutedText { get; set; }
        public string Track { get; set; }
        public string Primary { get; set; }
        public string Secondary { get; set; }
        public string Warning { get; set; }
        public string Danger { get; set; }

        public StyleSettings()
        {
            Scale = 1.0;
            Opacity = 0.97;
            CornerRadius = 9;
            FontSize = 14;
            ResetFontSize = 13;
            FontFamily = "Microsoft YaHei UI";
            Background = "#F7F7F5";
            CardBackground = "#FFFFFF";
            Border = "#D8D8D4";
            Text = "#252525";
            MutedText = "#727272";
            Track = "#EAEAE7";
            Primary = "#4F8CFF";
            Secondary = "#8A63D2";
            Warning = "#E6A23C";
            Danger = "#E45757";
        }

        public void Normalize()
        {
            Scale = Math.Max(0.75, Math.Min(1.5, Scale));
            Opacity = Math.Max(0.5, Math.Min(1.0, Opacity));
            CornerRadius = Math.Max(0, Math.Min(20, CornerRadius));
            FontSize = Math.Max(10, Math.Min(22, FontSize));
            ResetFontSize = Math.Max(9, Math.Min(18, ResetFontSize));
            if (string.IsNullOrWhiteSpace(FontFamily))
                FontFamily = "Microsoft YaHei UI";
            Background = ColorTools.Normalize(Background, "#F7F7F5");
            CardBackground = ColorTools.Normalize(CardBackground, "#FFFFFF");
            Border = ColorTools.Normalize(Border, "#D8D8D4");
            Text = ColorTools.Normalize(Text, "#252525");
            MutedText = ColorTools.Normalize(MutedText, "#727272");
            Track = ColorTools.Normalize(Track, "#EAEAE7");
            Primary = ColorTools.Normalize(Primary, "#4F8CFF");
            Secondary = ColorTools.Normalize(Secondary, "#8A63D2");
            Warning = ColorTools.Normalize(Warning, "#E6A23C");
            Danger = ColorTools.Normalize(Danger, "#E45757");
        }

        public StyleSettings Clone()
        {
            return new StyleSettings
            {
                Scale = Scale,
                Opacity = Opacity,
                CornerRadius = CornerRadius,
                FontSize = FontSize,
                ResetFontSize = ResetFontSize,
                FontFamily = FontFamily,
                Background = Background,
                CardBackground = CardBackground,
                Border = Border,
                Text = Text,
                MutedText = MutedText,
                Track = Track,
                Primary = Primary,
                Secondary = Secondary,
                Warning = Warning,
                Danger = Danger
            };
        }
    }

    internal sealed class RateSnapshot
    {
        public WindowUsage Primary { get; set; }
        public WindowUsage Secondary { get; set; }
        public string PlanType { get; set; }
    }

    internal sealed class WindowUsage
    {
        public double UsedPercent { get; set; }
        public long? ResetsAt { get; set; }
    }

    internal static class UsageDisplayTools
    {
        public static string Normalize(string value)
        {
            return string.Equals(value, "used", StringComparison.OrdinalIgnoreCase)
                ? "used"
                : "remaining";
        }

        public static bool IsRemaining(string value)
        {
            return Normalize(value) == "remaining";
        }

        public static double GetDisplayedPercent(double usedPercent, string mode)
        {
            double used = Math.Max(0, Math.Min(100, usedPercent));
            return IsRemaining(mode) ? 100d - used : used;
        }

        public static string FormatPercent(double value)
        {
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            return rounded.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public static Color GetProgressColor(
            double displayedPercent,
            string mode,
            Color normal,
            Color warning,
            Color danger)
        {
            if (IsRemaining(mode))
            {
                if (displayedPercent <= 15f)
                    return danger;
                if (displayedPercent <= 40f)
                    return warning;
                return normal;
            }

            if (displayedPercent >= 85f)
                return danger;
            if (displayedPercent >= 60f)
                return warning;
            return normal;
        }
    }

    internal static class DrawingHelpers
    {
        public const int BottomRightWidth = 184;
        public const int BottomRightHeight = 66;

        // Latin digits and '%' render optically smaller than CJK glyphs in common UI fonts.
        public const float PercentOpticalSizeOffset = 1f;

        public static RectangleF GetBottomRightCardBounds(bool primary)
        {
            // Six pixels between rows keeps the cards visually separate at 100–125% DPI.
            return new RectangleF(3, primary ? 3 : 36, 178, 27);
        }

        public static void DrawUsageText(
            Graphics graphics,
            RectangleF bounds,
            string label,
            string percent,
            string reset,
            Font labelFont,
            Font percentFont,
            Font resetFont,
            Brush textBrush,
            Brush mutedBrush)
        {
            const float leftPadding = 7f;
            const float labelGap = 4f;
            const float timeGap = 7f;
            const float rightPadding = 7f;

            using (StringFormat textFormat = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                textFormat.FormatFlags |= StringFormatFlags.NoWrap |
                                          StringFormatFlags.MeasureTrailingSpaces;

                // Reserve the bottom strip for the progress bar, then center every
                // font cell on the same horizontal line. Optical size differences
                // therefore grow equally upward and downward.
                float centerLine = bounds.Top + (bounds.Height - 4f) / 2f;
                float labelTop = centerLine - GetCellHeight(labelFont) / 2f;
                float percentTop = centerLine - GetCellHeight(percentFont) / 2f;
                float resetTop = centerLine - GetCellHeight(resetFont) / 2f;

                float labelX = bounds.Left + leftPadding;
                float labelWidth = MeasureTextWidth(graphics, label, labelFont, textFormat);
                float percentX = labelX + labelWidth + labelGap;
                float percentWidth = MeasureTextWidth(graphics, percent, percentFont, textFormat);

                graphics.DrawString(label, labelFont, textBrush,
                    new PointF(labelX, labelTop), textFormat);
                graphics.DrawString(percent, percentFont, textBrush,
                    new PointF(percentX, percentTop), textFormat);

                float timeLeft = percentX + percentWidth + timeGap;
                float timeRight = bounds.Right - rightPadding;
                if (timeRight > timeLeft)
                {
                    using (StringFormat timeFormat = (StringFormat)textFormat.Clone())
                    {
                        timeFormat.Alignment = StringAlignment.Far;
                        timeFormat.Trimming = StringTrimming.EllipsisCharacter;
                        float resetHeight = GetCellHeight(resetFont) + 2f;
                        graphics.DrawString(reset, resetFont, mutedBrush,
                            new RectangleF(
                                timeLeft,
                                resetTop,
                                timeRight - timeLeft,
                                resetHeight),
                            timeFormat);
                    }
                }
            }
        }

        private static float MeasureTextWidth(
            Graphics graphics,
            string text,
            Font font,
            StringFormat format)
        {
            return (float)Math.Ceiling(
                graphics.MeasureString(text, font, 1000, format).Width);
        }

        private static float GetCellHeight(Font font)
        {
            FontFamily family = font.FontFamily;
            int emHeight = family.GetEmHeight(font.Style);
            int cellHeight = family.GetCellAscent(font.Style) +
                             family.GetCellDescent(font.Style);
            return font.Size * cellHeight / emHeight;
        }

        public static GraphicsPath RoundRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Max(0, radius * 2);
            if (diameter <= 0.1f)
            {
                path.AddRectangle(bounds);
                path.CloseFigure();
                return path;
            }
            diameter = Math.Min(diameter, Math.Min(bounds.Width, bounds.Height));
            var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class ColorTools
    {
        public static string Normalize(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            string text = value.Trim();
            if ((text.Length == 7 || text.Length == 9) && text[0] == '#')
            {
                int ignored;
                if (int.TryParse(text.Substring(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out ignored))
                    return text;
            }
            return fallback;
        }

        public static Color Parse(string value)
        {
            string text = Normalize(value, "#000000");
            int r = int.Parse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (text.Length == 9)
            {
                int a = int.Parse(text.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(a, r, g, b);
            }
            return Color.FromArgb(r, g, b);
        }
    }

    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SW_SHOWNOACTIVATE = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int command);
    }
}
