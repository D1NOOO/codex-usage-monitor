using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            if (UpdateInstaller.TryHandleCommandLine(args))
                return;

            bool created;
            using (var mutex = new Mutex(true, @"Local\CodexRateMonitorNative", out created))
            {
                if (!created)
                    return;

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
        private readonly System.Windows.Forms.Timer topmostTimer;
        private readonly AppServerClient appServer;
        private readonly RateSnapshotStabilizer snapshotStabilizer;
        private readonly UsageRefreshScheduler refreshScheduler = new UsageRefreshScheduler();
        private readonly UpdateChecker updateChecker;
        private ToolStripMenuItem startupItem;
        private ToolStripMenuItem updateItem;
        private ToolStripMenuItem desktopModeItem;
        private ToolStripMenuItem attachModeItem;
        private ContextMenuStrip trayMenu;
        private MonitorSettings settings;
        private AppearanceSettingsForm appearanceForm;
        private UpdateForm updateForm;
        private UpdateInfo availableUpdate;
        private Icon updateAvailableIcon;
        private RateSnapshot lastSnapshot;
        private ResetCreditsInfo resetCredits;
        private DateTime lastCreditsCheck = DateTime.MinValue;
        private bool creditsFetchInFlight;
        private System.Windows.Forms.Timer trayTextRestoreTimer;
        private DateTime lastSnapshotAt = DateTime.MinValue;
        private double lastSnapshotMonotonic;
        private double nextUsageHintAt;
        private DesktopUsageState lastUsageState;
        private bool refreshFailed;
        private int completedReadId;
        private int accountEpoch;
        private string accountFingerprint;
        private IntPtr desktopWindow;
        private double nextWindowCheck;
        private double nextStartAttempt;
        private double authProbeNotBefore;

        private static double NowSeconds
        {
            get { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
        }

        // Unchanged quotas are normal during idle periods. Counts are diagnostic;
        // only authentication recovery may restart the long-lived child.
        private int identicalReads;
        private bool updateNotificationSeen;
        private DateTime lastResyncAt = DateTime.MinValue;
        private const int AnomalyResyncCooldownSeconds = 120;
        private const int AuthFailuresBeforeResync = 3;
        private const int AuthProbeSeconds = 300;
        private const int AuthRecoveryTimeoutSeconds = 180;
        private int authFailures;
        private bool authResyncAttempted;
        private bool authDead;
        private DateTime authAttemptAt = DateTime.MinValue;
        private int authReadFloor;
        private bool disposed;

        public MonitorContext(bool showSettings)
        {
            settings = MonitorSettings.Load();
            DiagnosticLog.Configure(
                settings.DiagnosticsEnabled,
                settings.DiagnosticRetentionDays);
            DiagnosticLog.Write("monitor-start", "version=" + BuildVersion.Value);
            I18n.SetLanguage(settings.Language);
            overlay = new OverlayForm(settings);
            overlay.DesktopLayoutChanged += OnDesktopLayoutChanged;
            IntPtr ignored = overlay.Handle;

            appServer = new AppServerClient();
            snapshotStabilizer = new RateSnapshotStabilizer();
            appServer.SnapshotReceived += OnSnapshotReceived;
            appServer.StatusChanged += OnStatusChanged;
            appServer.UpdateNotificationReceived += OnUpdateNotification;
            appServer.AuthRevoked += OnAuthRevoked;
            appServer.Initialized += OnInitialized;
            appServer.ReadCompleted += OnReadCompleted;
            appServer.AccountObserved += OnAccountObserved;
            appServer.AccountUpdated += OnAccountUpdated;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            updateChecker = new UpdateChecker(BuildVersion.Value);
            updateChecker.CheckCompleted += OnUpdateCheckCompleted;

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

            // Dedicated high-frequency timer that only re-asserts the desktop
            // overlay's topmost slot. SetWindowPos(TOPMOST, NOMOVE|NOSIZE|
            // NOACTIVATE) is a no-redraw, no-activate z-order tweak costing
            // microseconds, so running it at 50ms (20 Hz) keeps the overlay
            // above the shell taskbar with imperceptible recovery latency and
            // negligible CPU. It self-no-ops outside desktop mode.
            topmostTimer = new System.Windows.Forms.Timer();
            topmostTimer.Interval = 50;
            topmostTimer.Tick += delegate { overlay.ReassertTopmost(); };
            topmostTimer.Start();

            // One-shot timer that restores the usage tooltip shortly after a
            // transient tray message (e.g. "appearance saved") so the hover
            // info never stays stuck on a stale notice.
            trayTextRestoreTimer = new System.Windows.Forms.Timer();
            trayTextRestoreTimer.Interval = 4000;
            trayTextRestoreTimer.Tick += delegate
            {
                trayTextRestoreTimer.Stop();
                UpdateTrayText();
            };

            // First reset-credit lookup shortly after startup. The dedicated
            // client is strictly read-only; see ResetCreditsClient.
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000);
                Ui(FetchResetCredits);
            });

            if (showSettings)
                ShowAppearanceSettings();
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(I18n.T("AppearanceMenu"), null, delegate { ShowAppearanceSettings(); });
            menu.Items.Add(I18n.T("RefreshNow"), null, delegate { RefreshNow(); });
            updateItem = new ToolStripMenuItem(UpdateMenuText());
            updateItem.Click += delegate { CheckForUpdates(); };
            menu.Items.Add(updateItem);
            menu.Items.Add(I18n.T("ReloadStyle"), null, delegate { ReloadSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            desktopModeItem = new ToolStripMenuItem(I18n.T("DesktopFloatingMode"));
            desktopModeItem.Checked = settings.OverlayMode == "desktop";
            desktopModeItem.Click += delegate { SetOverlayMode("desktop"); };
            menu.Items.Add(desktopModeItem);
            attachModeItem = new ToolStripMenuItem(I18n.T("WindowAttachMode"));
            attachModeItem.Checked = settings.OverlayMode == "attach";
            attachModeItem.Click += delegate { SetOverlayMode("attach"); };
            menu.Items.Add(attachModeItem);
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
            RefreshUpdateIndicators();
            if (old != null)
                old.Dispose();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (appearanceForm != null && !appearanceForm.IsDisposed)
                appearanceForm.SetPreviewDpi(overlay.OverlayDpi);
            DiagnosticLog.CleanupIfDue();
            if (authResyncAttempted && !authDead &&
                (DateTime.Now - authAttemptAt).TotalSeconds >= AuthRecoveryTimeoutSeconds)
            {
                DiagnosticLog.Write("auth-resync-timeout", null);
                MarkAuthDead();
            }

            double now = NowSeconds;
            // Process discovery is cached; each tick only checks native window state.
            if (now >= nextWindowCheck)
            {
                desktopWindow = WindowLocator.FindDesktopMainWindow();
                nextWindowCheck = now + 1;
            }
            DesktopUsageState state = WindowLocator.GetUsageState(desktopWindow);
            lastUsageState = state;
            refreshScheduler.ObserveWindow(state, now);
            int expiredId, expiredGeneration;
            string expiredReason = refreshScheduler.PendingReason;
            double expiredAge = refreshScheduler.PendingAge(now);
            if (refreshScheduler.ExpireRead(now, out expiredId, out expiredGeneration))
            {
                appServer.CancelRateLimitsRequest(expiredId);
                refreshFailed = true;
                if (authDead) authProbeNotBefore = now + AuthProbeSeconds;
                DiagnosticLog.Write("rate-request-timeout", "id=" + expiredId +
                    " generation=" + expiredGeneration + " reason=" + expiredReason +
                    " elapsed_ms=" + (expiredAge * 1000).ToString("0", CultureInfo.InvariantCulture) +
                    " failures=" + refreshScheduler.FailureCount +
                    " retry_in_s=" + refreshScheduler.RetryDelay(now).ToString("0", CultureInfo.InvariantCulture));
                UpdateTrayText();
            }
            if (settings.OverlayMode == "desktop")
            {
                overlay.EnsureDesktopVisible();
            }
            else
            {
                if (state == DesktopUsageState.Foreground)
                    overlay.AttachTo(desktopWindow);
                else
                    overlay.Hide();
            }

            if (!appServer.IsRunning && !authDead && now >= nextStartAttempt)
            {
                nextStartAttempt = now + 30;
                if (desktopWindow != IntPtr.Zero || WindowLocator.IsDesktopAppRunning())
                    AutoStartAppServer();
            }

            if (appServer.IsInitialized)
            {
                int refreshSeconds = UsageRefreshScheduler.Interval(state,
                    settings.ForegroundRefreshSeconds, settings.RefreshSeconds,
                    Math.Max(settings.RefreshSeconds, settings.MinimizedRefreshSeconds));
                if (authDead)
                    refreshSeconds = Math.Max(refreshSeconds, AuthProbeSeconds);
                if ((!authDead || now >= authProbeNotBefore) &&
                    refreshScheduler.ShouldRead(now, refreshSeconds))
                    RequestRateLimits();
            }

            if (lastSnapshot != null && now >= nextUsageHintAt)
            {
                nextUsageHintAt = now + 30;
                UpdateTrayText();
            }

            if (settings.ShowResetCredits &&
                (DateTime.Now - lastCreditsCheck).TotalSeconds >=
                    Math.Max(300, settings.ResetCreditsSeconds))
            {
                FetchResetCredits();
            }
        }

        private void StartAppServer()
        {
            overlay.SetStatus(I18n.T("Connecting"));
            try
            {
                refreshScheduler.Reset(NowSeconds, 0);
                appServer.Start();
                // Fresh child: diagnostic counts start over.
                identicalReads = 0;
                updateNotificationSeen = false;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("app-server-start-failed", "type=" + ex.GetType().Name);
                overlay.SetStatus(I18n.T("ServiceError"));
                trayIcon.Text = SafeTrayText(I18n.F("StartFailed", ex.Message));
            }
        }

        private void AutoStartAppServer()
        {
            if (authFailures > 0)
            {
                // A child exit during an auth incident consumes the single
                // automatic restart allowance, too.
                if (authResyncAttempted)
                {
                    MarkAuthDead();
                    return;
                }
                authResyncAttempted = true;
                authAttemptAt = DateTime.Now;
                lastResyncAt = DateTime.Now;
                DiagnosticLog.Write("auth-resync-restart", "reason=child-exit");
            }
            StartAppServer();
            if (authResyncAttempted && !appServer.IsRunning)
                MarkAuthDead();
        }

        private void RefreshNow()
        {
            if (authFailures == 0 && !authDead)
            {
                RequestRateLimits(true);
                return;
            }

            // A manual retry is a new, explicit attempt. A further 401 before
            // a valid snapshot returns directly to the terminal auth state.
            authDead = false;
            authFailures = 0;
            authResyncAttempted = true;
            authAttemptAt = DateTime.Now;
            authReadFloor = appServer.LatestRequestId;
            lastResyncAt = DateTime.Now;
            overlay.ClearSnapshot(I18n.T("Connecting"));
            trayIcon.Text = SafeTrayText(I18n.F("TrayStatus", I18n.T("Connecting")));
            DiagnosticLog.Write("auth-manual-restart", null);
            try
            {
                refreshScheduler.Reset(NowSeconds, 0);
                if (appServer.IsRunning)
                    appServer.RefreshRateLimits(true);
                else
                    StartAppServer();
                if (!appServer.IsRunning)
                    MarkAuthDead();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("auth-manual-restart-failed", "type=" + ex.GetType().Name);
                MarkAuthDead();
            }
        }

        private void RequestRateLimits(bool manual = false)
        {
            if (!appServer.IsRunning)
            {
                if (!authDead && WindowLocator.FindDesktopMainWindow() != IntPtr.Zero)
                    AutoStartAppServer();
                return;
            }

            if (!appServer.IsInitialized || refreshScheduler.InFlight)
                return;

            double now = NowSeconds;
            appServer.RequestRateLimits(delegate(int id)
            {
                if (refreshScheduler.BeginRead(id, appServer.Generation, now, manual))
                    DiagnosticLog.Write("rate-refresh-started", "id=" + id +
                        " generation=" + appServer.Generation +
                        " reason=" + refreshScheduler.PendingReason +
                        " window=" + lastUsageState.ToString().ToLowerInvariant());
            });
        }

        private void OnInitialized(int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation) return;
                refreshScheduler.Reset(NowSeconds, 0, "startup");
                RequestRateLimits();
            });
        }

        private void OnReadCompleted(int id, int generation, bool success)
        {
            Ui(delegate
            {
                double now = NowSeconds;
                if (generation != appServer.Generation || !refreshScheduler.InFlight ||
                    refreshScheduler.PendingRequestId != id || refreshScheduler.PendingGeneration != generation)
                {
                    DiagnosticLog.Write("rate-completion-ignored", "id=" + id +
                        " generation=" + generation + " reason=obsolete-read");
                    return;
                }
                string reason = refreshScheduler.PendingReason;
                double age = refreshScheduler.PendingAge(now);
                if (!refreshScheduler.CompleteRead(id, generation, now, success)) return;
                DiagnosticLog.Write("rate-refresh-completed", "id=" + id +
                    " generation=" + generation + " reason=" + reason +
                    " state=" + (success ? "success" : "failure") +
                    " elapsed_ms=" + (age * 1000).ToString("0", CultureInfo.InvariantCulture) +
                    " failures=" + refreshScheduler.FailureCount +
                    " retry_in_s=" + refreshScheduler.RetryDelay(now).ToString("0", CultureInfo.InvariantCulture));
                refreshFailed = !success;
                completedReadId = success ? id : 0;
                if (authDead && !success)
                {
                    authProbeNotBefore = NowSeconds + AuthProbeSeconds;
                    refreshScheduler.Trigger(NowSeconds, AuthProbeSeconds, "auth_probe");
                }
                UpdateTrayText();
            });
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Ui(delegate
            {
                nextWindowCheck = 0;
                refreshScheduler.Trigger(NowSeconds, 1, "resume");
                DiagnosticLog.Write("rate-refresh-trigger", "reason=resume");
            });
        }

        private void ClearAccountData(string status)
        {
            accountEpoch++;
            lastSnapshot = null;
            lastSnapshotAt = DateTime.MinValue;
            lastSnapshotMonotonic = 0;
            refreshScheduler.ClearResetTargets();
            resetCredits = null;
            lastCreditsCheck = DateTime.MinValue;
            identicalReads = 0;
            completedReadId = 0;
            snapshotStabilizer.Reset();
            overlay.ClearSnapshot(status);
            if (appearanceForm != null && !appearanceForm.IsDisposed)
                appearanceForm.SetUsageSnapshot(null);
            overlay.SetResetCredits(null);
            overlay.SetUsageHint(status);
            trayIcon.Text = SafeTrayText(I18n.F("TrayStatus", status));
        }

        private void OnAccountUpdated(int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation) return;
                accountFingerprint = null;
                ClearAccountData(I18n.T("Connecting"));
                authReadFloor = appServer.LatestRequestId;
                authDead = false;
                authFailures = 0;
                authResyncAttempted = false;
                refreshScheduler.Reset(NowSeconds, 1);
                DiagnosticLog.Write("account-changed", "source=notification");
            });
        }

        private void OnAccountObserved(AccountState account, int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation) return;
                if (account.ReadRequestId > 0 && (!refreshScheduler.InFlight ||
                    refreshScheduler.PendingRequestId != account.ReadRequestId ||
                    refreshScheduler.PendingGeneration != generation)) return;
                if (accountFingerprint != account.Fingerprint)
                {
                    ClearAccountData(I18n.T("Connecting"));
                    accountFingerprint = account.Fingerprint;
                    DiagnosticLog.Write("account-changed", "source=read");
                }
                if (!account.CanReadQuota)
                {
                    authDead = true;
                    authProbeNotBefore = NowSeconds + AuthProbeSeconds;
                    ClearAccountData(I18n.T(account.StatusKey));
                    refreshScheduler.Trigger(NowSeconds, AuthProbeSeconds, "auth_probe");
                }
            });
        }

        private void OnUpdateNotification(int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation)
                    return;
                updateNotificationSeen = true;
                identicalReads = 0;
            });
        }

        // Equal readings do not prove a stale connection.
        private void TrackSnapshotFreshness(RateSnapshot snapshot)
        {
            if (lastSnapshot == null || snapshot == null)
                return;
            if (updateNotificationSeen)
            {
                identicalReads = 0;
                return;
            }
            if (SameWindow(snapshot.Primary, lastSnapshot.Primary) &&
                SameWindow(snapshot.Secondary, lastSnapshot.Secondary))
            {
                identicalReads++;
            }
            else
            {
                identicalReads = 0;
                return;
            }
        }

        private static bool SameWindow(WindowUsage a, WindowUsage b)
        {
            if (a == null && b == null)
                return true;
            if (a == null || b == null)
                return false;
            return a.UsedPercent == b.UsedPercent &&
                   a.ResetsAt == b.ResetsAt;
        }

        private void OnSnapshotReceived(RateSnapshot snapshot, int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation)
                {
                    LogNotificationDisposition(snapshot, generation, "ignored", "obsolete-generation");
                    return;
                }
                if (snapshot.ReadRequestId > 0 && snapshot.ReadRequestId != completedReadId)
                {
                    DiagnosticLog.Write("snapshot-ignored", "id=" + snapshot.ReadRequestId +
                        " generation=" + generation + " reason=obsolete-read");
                    return;
                }
                if ((authFailures > 0 || authResyncAttempted || authDead) &&
                    (!snapshot.ReplaceMissingWindows || snapshot.ReadRequestId <= authReadFloor))
                {
                    DiagnosticLog.Write("auth-snapshot-ignored", "reason=predates-failure");
                    LogNotificationDisposition(snapshot, generation, "ignored", "auth-unverified");
                    return;
                }
                string validation;
                if (!snapshotStabilizer.TryAccept(
                    snapshot, DateTimeOffset.UtcNow, out validation))
                {
                    DiagnosticLog.Write(
                        "snapshot-deferred",
                        validation + " " + AppServerClient.DescribeSnapshot(snapshot));
                    LogNotificationDisposition(snapshot, generation, "deferred", "snapshot-validation");
                    refreshScheduler.Trigger(NowSeconds, 1, "validation");
                    return;
                }

                if (validation.StartsWith(
                    "accepted=confirmed-reset", StringComparison.Ordinal))
                    DiagnosticLog.Write(
                        "snapshot-confirmed",
                        AppServerClient.DescribeSnapshot(snapshot));

                if (authFailures > 0 || authResyncAttempted || authDead)
                {
                    DiagnosticLog.Write("auth-recovered", null);
                    authFailures = 0;
                    authResyncAttempted = false;
                    authDead = false;
                    authAttemptAt = DateTime.MinValue;
                }
                overlay.SetSnapshot(snapshot);
                DiagnosticLog.Write(
                    "snapshot-displayed",
                    "source=" + (snapshot.NotificationId > 0 ? "notification" : "read") +
                    " id=" + (snapshot.NotificationId > 0 ? snapshot.NotificationId : snapshot.ReadRequestId) +
                    " generation=" + generation + " mode=" + settings.UsageDisplay +
                    " " + AppServerClient.DescribeSnapshot(snapshot));
                LogNotificationDisposition(snapshot, generation, "displayed", "validated");
                TrackSnapshotFreshness(snapshot);
                lastSnapshot = snapshot;
                if (appearanceForm != null && !appearanceForm.IsDisposed)
                    appearanceForm.SetUsageSnapshot(snapshot);
                lastSnapshotAt = DateTime.Now;
                lastSnapshotMonotonic = NowSeconds;
                refreshScheduler.ObserveResets(snapshot.Primary == null ? null : snapshot.Primary.ResetsAt,
                    snapshot.Secondary == null ? null : snapshot.Secondary.ResetsAt,
                    DateTimeOffset.UtcNow, lastSnapshotMonotonic,
                    snapshot.Primary == null ? (double?)null : snapshot.Primary.UsedPercent,
                    snapshot.Secondary == null ? (double?)null : snapshot.Secondary.UsedPercent);
                refreshFailed = false;
                UpdateTrayText();
            });
        }

        private static void LogNotificationDisposition(RateSnapshot snapshot, int generation, string state, string reason)
        {
            if (snapshot.NotificationId <= 0) return;
            DiagnosticLog.Write("rate-notification-" + state, "id=" + snapshot.NotificationId +
                " generation=" + generation + " reason=" + reason);
        }

        private static string FormatCacheAge(double seconds)
        {
            seconds = Math.Max(0, seconds);
            string key = seconds < 60 ? "AgeSeconds" : seconds < 3600 ? "AgeMinutes" :
                seconds < 86400 ? "AgeHours" : "AgeDays";
            double divisor = seconds < 60 ? 1 : seconds < 3600 ? 60 : seconds < 86400 ? 3600 : 86400;
            return I18n.F(key, Math.Min(9999, Math.Floor(seconds / divisor)).ToString(CultureInfo.InvariantCulture));
        }

        // Keep update time within the tray's 63-character limit. The overlay
        // tooltip can additionally show full credit details and failure state.
        private void UpdateTrayText()
        {
            if (authFailures > 0 || authResyncAttempted || authDead)
                return;
            if (lastSnapshot == null)
                return;
            string text = string.Format(CultureInfo.InvariantCulture,
                I18n.T("TrayUsageTitle"),
                I18n.T(UsageDisplayTools.IsRemaining(settings.UsageDisplay)
                    ? "Remaining"
                    : "Used"));
            string summary = UsagePanelTools.FormatSummary(settings, lastSnapshot);
            text += "\n" + summary;
            string updated = I18n.F("UpdatedAt", lastSnapshotAt.ToString("HH:mm:ss"));
            double cacheAge = Math.Max(0, NowSeconds - lastSnapshotMonotonic);
            int interval = UsageRefreshScheduler.Interval(lastUsageState,
                settings.ForegroundRefreshSeconds, settings.RefreshSeconds, settings.MinimizedRefreshSeconds);
            bool stale = cacheAge > Math.Max(120, interval * 2 + 30);
            string hint = text + "\n" + updated;
            if (refreshFailed) hint += "\n" + I18n.T("RefreshFailedCached");
            else if (stale) hint += "\n" + I18n.T("CachedDataStale");
            if (refreshFailed || stale) hint += "\n" + I18n.F("LastConfirmedAgo", FormatCacheAge(cacheAge));
            if (resetCredits != null && resetCredits.AvailableCount > 0)
            {
                hint += "\n" + I18n.T("TrayCreditsTitle");
                hint += "\n" + string.Format(CultureInfo.InvariantCulture,
                    I18n.T("TrayCreditsDetail"),
                    resetCredits.AvailableCount.ToString(CultureInfo.InvariantCulture),
                    resetCredits.FormatEarliestExpiry());
            }
            overlay.SetUsageHint(hint);
            // The compact form works in all three languages without truncating
            // the update time. Failure remains visible even in attach mode.
            string compact = I18n.F("UsageTraySummary",
                I18n.T(UsageDisplayTools.IsRemaining(settings.UsageDisplay) ? "Remaining" : "Used"),
                summary);
            if (refreshFailed || stale)
                compact += "\n" + lastSnapshotAt.ToString("HH:mm:ss") + " " +
                    I18n.T(refreshFailed ? "RefreshFailedShort" : "CachedShort") + " " + FormatCacheAge(cacheAge);
            else compact += "\n" + updated;
            trayIcon.Text = SafeTrayText(compact);
        }

        // Read-only reset-credit refresh. Runs off the UI thread; the result is
        // marshaled back through Ui(). Failures stay silent (info stays null or
        // stale) because the ChatGPT backend is an auxiliary data source.
        private void FetchResetCredits()
        {
            if (!settings.ShowResetCredits || creditsFetchInFlight || authDead || accountFingerprint == null)
                return;
            creditsFetchInFlight = true;
            int startedEpoch = accountEpoch;
            lastCreditsCheck = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                ResetCreditsInfo info = ResetCreditsClient.Fetch();
                Ui(delegate
                {
                    creditsFetchInFlight = false;
                    if (startedEpoch != accountEpoch)
                        return;
                    resetCredits = info;
                    // On failure retry soon (60s) instead of waiting a full
                    // cycle; the backend is auxiliary so this stays quiet.
                    lastCreditsCheck = info == null
                        ? DateTime.Now.AddSeconds(
                            -Math.Max(300, settings.ResetCreditsSeconds) + 60)
                        : DateTime.Now;
                    overlay.SetResetCredits(
                        settings.ShowResetCredits ? resetCredits : null);
                    UpdateTrayText();
                });
            });
        }

        private void OnAuthRevoked(int generation, int latestRequestId)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation)
                    return;
                authReadFloor = Math.Max(authReadFloor, latestRequestId);
                authFailures++;
                DiagnosticLog.Write("auth-revoked", "count=" +
                    authFailures.ToString(CultureInfo.InvariantCulture));
                if (authFailures == 1)
                {
                    ClearAccountData(I18n.T("Connecting"));
                }
                if (authResyncAttempted)
                {
                    MarkAuthDead();
                    return;
                }
                if (authFailures < AuthFailuresBeforeResync ||
                    (DateTime.Now - lastResyncAt).TotalSeconds < AnomalyResyncCooldownSeconds)
                    return;

                authResyncAttempted = true;
                authAttemptAt = DateTime.Now;
                lastResyncAt = DateTime.Now;
                DiagnosticLog.Write("auth-resync-restart", "reason=revoked");
                try
                {
                    refreshScheduler.Reset(NowSeconds, 0);
                    appServer.RefreshRateLimits();
                    if (!appServer.IsRunning)
                        MarkAuthDead();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("auth-resync-restart-failed", "type=" + ex.GetType().Name);
                    MarkAuthDead();
                }
            });
        }

        private void MarkAuthDead()
        {
            if (authDead)
                return;
            authDead = true;
            authProbeNotBefore = NowSeconds + AuthProbeSeconds;
            string status = I18n.T("AuthUnavailable");
            ClearAccountData(status);
            refreshScheduler.Trigger(NowSeconds, AuthProbeSeconds, "auth_probe");
            DiagnosticLog.Write("auth-dead", null);
        }

        private void OnStatusChanged(string status, int generation)
        {
            Ui(delegate
            {
                if (generation != appServer.Generation || authFailures > 0 ||
                    authResyncAttempted || authDead)
                    return;
                overlay.SetStatus(status);
                if (lastSnapshot != null)
                {
                    refreshFailed = true;
                    UpdateTrayText();
                }
                else
                    trayIcon.Text = SafeTrayText(I18n.F("TrayStatus", status));
            });
        }

        private void CheckForUpdates()
        {
            if (updateItem != null)
            {
                updateItem.Enabled = false;
                updateItem.Text = I18n.T("CheckingUpdates");
            }
            updateChecker.Check(true);
        }

        private void OnUpdateCheckCompleted(UpdateCheckResult result, bool manual)
        {
            Ui(delegate
            {
                if (updateItem != null)
                    updateItem.Enabled = true;

                if (!result.Success)
                {
                    DiagnosticLog.Write("update-check-failed", "type=" + (result.Error ?? "unknown"));
                    RefreshUpdateIndicators();
                    if (manual)
                        MessageBox.Show(
                            I18n.T("UpdateCheckFailed"),
                            I18n.T("UpdateTitle"),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    return;
                }

                DiagnosticLog.Write(
                    "update-check",
                    "latest=" + result.Info.Version +
                    " available=" + result.UpdateAvailable.ToString(CultureInfo.InvariantCulture));
                availableUpdate = result.UpdateAvailable ? result.Info : null;
                RefreshUpdateIndicators();

                if (!manual)
                    return;
                if (!result.UpdateAvailable)
                {
                    MessageBox.Show(
                        I18n.F("AlreadyLatest", BuildVersion.Value),
                        I18n.T("UpdateTitle"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
                ShowUpdateForm(result.Info);
            });
        }

        private void ShowUpdateForm(UpdateInfo info)
        {
            if (updateForm != null && !updateForm.IsDisposed)
            {
                updateForm.Activate();
                return;
            }
            updateForm = new UpdateForm(info);
            updateForm.UpdateRequested += BeginInstallUpdate;
            updateForm.FormClosed += delegate { updateForm = null; };
            updateForm.Show();
            updateForm.Activate();
        }

        private void BeginInstallUpdate(UpdateForm form, UpdateInfo info)
        {
            form.SetBusy(I18n.T("UpdateDownloading"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    PreparedUpdate prepared = UpdateInstaller.Prepare(info);
                    Ui(delegate
                    {
                        try
                        {
                            form.SetBusy(I18n.T("UpdateInstalling"));
                            UpdateInstaller.LaunchHelper(prepared, info.Version);
                            ExitMonitor();
                        }
                        catch (Exception ex)
                        {
                            DiagnosticLog.Write("update-launch-failed", "type=" + ex.GetType().Name);
                            form.SetError(I18n.F("UpdateInstallFailed", ex.Message));
                        }
                    });
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write("update-download-failed", "type=" + ex.GetType().Name);
                    Ui(delegate
                    {
                        if (form != null && !form.IsDisposed)
                            form.SetError(I18n.F("UpdateInstallFailed", ex.Message));
                    });
                }
            });
        }

        private string UpdateMenuText()
        {
            return availableUpdate == null
                ? I18n.T("CheckUpdates")
                : "● " + I18n.F("UpdateAvailableMenu", availableUpdate.Version);
        }

        private void RefreshUpdateIndicators()
        {
            if (updateItem != null)
                updateItem.Text = UpdateMenuText();
            // Update presence is signaled only via the tray icon red-dot
            // badge; tooltip and overlay stay purely about usage.
            if (availableUpdate != null)
            {
                if (updateAvailableIcon == null)
                    updateAvailableIcon = CreateBadgedIcon(applicationIcon ?? SystemIcons.Application);
                trayIcon.Icon = updateAvailableIcon;
            }
            else
            {
                trayIcon.Icon = applicationIcon ?? SystemIcons.Application;
            }
        }

        private static Icon CreateBadgedIcon(Icon source)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.DrawIcon(source, new Rectangle(0, 0, 32, 32));
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var border = new SolidBrush(Color.White))
                using (var dot = new SolidBrush(Color.FromArgb(232, 67, 67)))
                {
                    graphics.FillEllipse(border, 18, 0, 14, 14);
                    graphics.FillEllipse(dot, 20, 2, 10, 10);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
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
            if (value.Length <= 63)
                return value;
            // Windows tray tooltips hold at most 63 chars. Cut at the last
            // line break so a sentence is never chopped mid-word (which used
            // to render as garbage like "earlies"); otherwise trim hard.
            string cut = value.Substring(0, 63);
            int nl = cut.LastIndexOf('\n');
            if (nl > 0)
                return cut.Substring(0, nl);
            return cut.TrimEnd();
        }

        // Shows a short-lived tray notice and schedules the usage tooltip to
        // come back automatically, so transient messages never stick.
        private void ShowTransientTrayText(string text)
        {
            trayIcon.Text = SafeTrayText(text);
            trayTextRestoreTimer.Stop();
            trayTextRestoreTimer.Start();
        }

        private void SetPosition(string position)
        {
            settings.SelectPlacement(settings.OverlayMode, position);
            settings.Save();
            overlay.ApplySettings(settings);
        }

        private void SetOverlayMode(string mode)
        {
            settings.SelectPlacement(mode, settings.Position);
            settings.Save();
            if (desktopModeItem != null)
                desktopModeItem.Checked = mode == "desktop";
            if (attachModeItem != null)
                attachModeItem.Checked = mode == "attach";
            overlay.ApplySettings(settings);
            if (mode == "desktop")
                overlay.ShowDesktop();
            else
                overlay.Hide();
        }

        private void OnDesktopLayoutChanged(int x, int y)
        {
            settings.DesktopX = x;
            settings.DesktopY = y;
            settings.Save();
        }

        private void ReloadSettings()
        {
            settings = MonitorSettings.Load();
            DiagnosticLog.Configure(
                settings.DiagnosticsEnabled,
                settings.DiagnosticRetentionDays);
            I18n.SetLanguage(settings.Language);
            RefreshTrayLanguage();
            overlay.ApplySettings(settings);
            UpdateTrayText();
            ShowTransientTrayText(I18n.T("StyleReloaded"));
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
                    DiagnosticLog.Configure(
                        settings.DiagnosticsEnabled,
                        settings.DiagnosticRetentionDays);
                    I18n.SetLanguage(settings.Language);
                    settings.Save();
                    RefreshTrayLanguage();
                    overlay.ApplySettings(settings);
                    UpdateTrayText();
                    ShowTransientTrayText(I18n.T("AppearanceSaved"));
                },
                delegate
                {
                    overlay.ApplySettings(original);
                });
            appearanceForm.FormClosed += delegate { appearanceForm = null; };
            appearanceForm.SetUsageSnapshot(lastSnapshot);
            appearanceForm.SetPreviewDpi(overlay.OverlayDpi);
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
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            DiagnosticLog.Write("monitor-stop", null);
            timer.Stop();
            timer.Dispose();
            topmostTimer.Stop();
            topmostTimer.Dispose();
            if (trayTextRestoreTimer != null)
            {
                trayTextRestoreTimer.Stop();
                trayTextRestoreTimer.Dispose();
                trayTextRestoreTimer = null;
            }
            trayIcon.Visible = false;
            trayIcon.Dispose();
            updateChecker.Dispose();
            if (updateAvailableIcon != null)
                updateAvailableIcon.Dispose();
            if (applicationIcon != null)
                applicationIcon.Dispose();
            if (appearanceForm != null && !appearanceForm.IsDisposed)
                appearanceForm.Close();
            if (updateForm != null && !updateForm.IsDisposed)
                updateForm.Close();
            appServer.Dispose();
            overlay.Close();
            overlay.Dispose();
        }
    }

    internal sealed class OverlayForm : Form
    {
        private readonly ToolTip usageHint = new ToolTip { ShowAlways = true, AutoPopDelay = 10000 };
        private MonitorSettings settings;
        private RateSnapshot snapshot;
        private ResetCreditsInfo resetCredits;
        private string status = I18n.T("Connecting");

        private string overlayMode = "attach";
        private bool dragging;
        private Point dragOffset;
        private int overlayDpi = 96;
        private IntPtr attachedWindow;
        private bool applyingDpi;
        private bool presentingSurface;
        private bool creatingSurfaceHandle;

        public int OverlayDpi { get { return overlayDpi; } }

        public void SetUsageHint(string text)
        {
            usageHint.SetToolTip(this, text);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) usageHint.Dispose();
            base.Dispose(disposing);
        }

        // Raised when the user finishes dragging the desktop overlay so the
        // context can persist the new position. The overlay is always topmost
        // in desktop mode (no pin toggle anymore).
        public event Action<int, int> DesktopLayoutChanged;

        public OverlayForm(MonitorSettings initialSettings)
        {
            settings = initialSettings;
            // Custom painting owns scaling; WinForms must not scale it again.
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            // Allow a borderless alpha window below the normal minimum track
            // height. Keep Form.Opacity at 1; Present owns the alpha channel.
            AllowTransparency = true;
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
                const int WS_EX_LAYERED = 0x80000;
                const int WS_EX_NOACTIVATE = 0x08000000;
                CreateParams cp = base.CreateParams;
                cp.Style |= unchecked((int)0x80000000); // WS_POPUP: no normal-window minimum track size.
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                // Attach mode is click-through so it never steals focus from the
                // ChatGPT/Codex window. Desktop mode needs mouse interaction for
                // the pin button and dragging, so it drops WS_EX_TRANSPARENT.
                if (overlayMode != "desktop")
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                return cp;
            }
        }

        public void ApplySettings(MonitorSettings value)
        {
            settings = value;
            string newMode = string.IsNullOrEmpty(value.OverlayMode) ? "desktop" : value.OverlayMode;
            bool modeChanged = overlayMode != newMode;
            overlayMode = newMode;

            BackColor = ColorTools.Parse(settings.Style.Background);

            if (modeChanged)
            {
                dragging = false;
                RecreateHandle();
            }

            UpdateOverlayLayout();

            if (overlayMode == "desktop" && Visible)
                ShowDesktop();
            RefreshSurface();
        }

        private bool ShowCreditsBadge
        {
            get
            {
                return settings.ShowResetCredits &&
                       resetCredits != null &&
                       resetCredits.AvailableCount > 0;
            }
        }

        private void UpdateOverlaySize()
        {
            Size pixels = OverlayRenderer.GetSnapshotPixelSize(settings, snapshot, ShowCreditsBadge, overlayDpi);
            if (Size != pixels)
            {
                // Form.SetBoundsCore caps Size at the screen's tracking limit.
                // Keep this custom layered window's bounds equal to its bitmap,
                // including large user scales on a small monitor.
                if (IsHandleCreated)
                    NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, pixels.Width, pixels.Height,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                else
                    Size = pixels;
            }
            if (Visible && overlayMode == "desktop")
                Location = ClampLocationToScreen(Location);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            creatingSurfaceHandle = true;
            try
            {
                base.OnHandleCreated(e);
                ApplyDpi(DisplayDpi.ForWindow(Handle), Location);
            }
            finally { creatingSurfaceHandle = false; }
            // WinForms finishes applying extended styles after WM_CREATE.
            // Present only after creation is complete, including recreation
            // when switching between desktop and attachment modes.
            BeginInvoke((MethodInvoker)RefreshSurface);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // Rebuild from the 96-DPI baseline instead of multiplying the old
            // window size. Keep a placed desktop overlay stationary on its
            // monitor; use Windows' suggested location when crossing monitors.
            e.Cancel = true;
            base.OnDpiChanged(e);
            bool sameScreen = Screen.FromRectangle(Bounds).DeviceName ==
                Screen.FromRectangle(e.SuggestedRectangle).DeviceName;
            ApplyDpi(e.DeviceDpiNew, overlayMode == "desktop" && sameScreen
                ? Location : e.SuggestedRectangle.Location);
            if (dragging)
                dragOffset = new Point(Control.MousePosition.X - Left, Control.MousePosition.Y - Top);
        }

        private void ApplyDpi(int dpi, Point location)
        {
            if (applyingDpi || settings == null)
                return;
            applyingDpi = true;
            try
            {
                overlayDpi = Math.Max(96, dpi);
                Location = location;
                UpdateOverlaySize();
                if (overlayMode == "attach" && attachedWindow != IntPtr.Zero)
                    AttachTo(attachedWindow);
                RefreshSurface();
            }
            finally { applyingDpi = false; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Transparent);
        }

        public void SetSnapshot(RateSnapshot value)
        {
            if (value != null)
                value.ConfirmedWindows = value.ReplaceMissingWindows
                    ? UsagePanelTools.GetConfirmedWindows(value)
                    : (snapshot == null ? null : snapshot.ConfirmedWindows);
            if (snapshot != null && value != null && !value.ReplaceMissingWindows)
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
            UpdateOverlayLayout();
            RefreshSurface();
        }

        public void SetStatus(string value)
        {
            if (snapshot == null)
                status = value;
            RefreshSurface();
        }

        public void ClearSnapshot(string value)
        {
            snapshot = null;
            status = value;
            UpdateOverlayLayout();
            RefreshSurface();
        }

        public void SetResetCredits(ResetCreditsInfo value)
        {
            resetCredits = value;
            UpdateOverlayLayout();
            RefreshSurface();
        }

        private void UpdateOverlayLayout()
        {
            UpdateOverlaySize();
            // Native attachment uses ShowWindow rather than Form.Show(), so
            // WinForms' managed Visible flag can lag the actual window state.
            if (overlayMode == "attach" && attachedWindow != IntPtr.Zero &&
                IsHandleCreated && NativeMethods.IsWindowVisible(Handle))
                AttachTo(attachedWindow);
        }

        public void AttachTo(IntPtr codexWindow)
        {
            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(codexWindow, out rect))
            {
                Hide();
                return;
            }

            attachedWindow = codexWindow;
            // The target may itself be DPI-unaware, so its window DPI is not
            // necessarily the effective DPI of the monitor hosting it.
            int dpi = DisplayDpi.ForWindowMonitor(codexWindow, Handle);
            if (overlayDpi != dpi)
            {
                overlayDpi = dpi;
                UpdateOverlaySize();
                RefreshSurface();
            }
            int inset = (int)Math.Round(12d * overlayDpi / 96d);

            int x;
            int y;
            if (settings.Position == "bottom-right")
            {
                x = rect.Right - Width - inset;
                y = rect.Bottom - Height - inset;
            }
            else
            {
                x = rect.Left + ((rect.Right - rect.Left) - Width) / 2;
                y = rect.Top + (int)Math.Round(4d * overlayDpi / 96d);
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

        // ---- Desktop floating mode ----

        public void EnsureDesktopVisible()
        {
            if (overlayMode != "desktop")
                return;
            if (!Visible)
                ShowDesktop();
        }

        public void ShowDesktop()
        {
            if (overlayMode != "desktop")
                return;
            // Only resolve/assign the location the first time the window is made
            // visible. Once it is on screen (e.g. dragged to a custom spot), later
            // calls — triggered by live preview, save, or style reload from the
            // settings form — must only re-assert topmost, never reposition. This
            // keeps a user-placed desktop window pinned where they left it.
            if (!Visible)
            {
                Location = ResolveDesktopLocation();
                ApplyDpi(DisplayDpi.ForWindow(Handle), Location);
                Location = ResolveDesktopLocation();
            }
            if (!Visible)
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
            BringToFront();
            RefreshSurface();
        }

        // The desktop overlay is always topmost so it can cover the shell
        // taskbar (which is itself topmost). Re-asserted every 50ms by the
        // dedicated topmostTimer to stay locked above the taskbar at all times,
        // like desktop lyric overlays.
        private new void BringToFront()
        {
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        public void ReassertTopmost()
        {
            if (overlayMode != "desktop" || !Visible)
                return;
            BringToFront();
        }

        private void SaveDesktopLocation()
        {
            var handler = DesktopLayoutChanged;
            if (handler != null)
                handler(Location.X, Location.Y);
        }

        private Point ResolveDesktopLocation()
        {
            Point desired;
            if ((settings.DesktopX != 0 || settings.DesktopY != 0) &&
                IsLocationOnScreen(new Point(settings.DesktopX, settings.DesktopY)))
            {
                desired = new Point(settings.DesktopX, settings.DesktopY);
            }
            else
            {
                // Default to bottom-center of the primary screen's working area
                // (just above the taskbar) — a natural spot for a status strip,
                // instead of the top-right corner.
                Rectangle area = Screen.PrimaryScreen != null
                    ? Screen.PrimaryScreen.WorkingArea
                    : Screen.GetBounds(Point.Empty);
                desired = new Point(
                    area.Left + (area.Width - Width) / 2,
                    area.Bottom - Height - (int)Math.Round(16d * overlayDpi / 96d));
            }
            // Clamp to the full screen bounds (which includes the taskbar region) so the
            // overlay can be parked on top of the taskbar; it only prevents the
            // overlay from being dragged completely off-screen. Covering the
            // taskbar is intentional — both states are topmost.
            return ClampLocationToScreen(desired);
        }

        private Point ClampLocationToScreen(Point desired)
        {
            Rectangle area = Screen.PrimaryScreen != null
                ? Screen.PrimaryScreen.Bounds
                : Screen.GetBounds(Point.Empty);
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.Contains(desired))
                {
                    area = screen.Bounds;
                    break;
                }
            }
            int x = Math.Max(area.Left, Math.Min(desired.X, area.Right - Width));
            int y = Math.Max(area.Top, Math.Min(desired.Y, area.Bottom - Height));
            return new Point(x, y);
        }

        private static bool IsLocationOnScreen(Point location)
        {
            // Use the full screen bounds (which include the taskbar region), not
            // WorkingArea. The desktop overlay is intentionally allowed to sit on
            // top of the taskbar, so a saved position there must be treated as
            // valid instead of being discarded and reset to the default.
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.Contains(location))
                    return true;
            }
            return false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (overlayMode != "desktop")
                return;
            // The whole overlay is draggable in desktop mode (no pin button).
            dragging = true;
            dragOffset = new Point(
                Control.MousePosition.X - Location.X,
                Control.MousePosition.Y - Location.Y);
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (overlayMode != "desktop")
                return;
            Cursor = Cursors.SizeAll;
            if (dragging)
            {
                Point raw = new Point(
                    Control.MousePosition.X - dragOffset.X,
                    Control.MousePosition.Y - dragOffset.Y);
                // Keep the overlay on screen (it may sit over the taskbar by design).
                Location = ClampLocationToScreen(raw);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (overlayMode != "desktop")
                return;
            if (dragging)
            {
                dragging = false;
                Capture = false;
                SaveDesktopLocation();
            }
        }

        private void RefreshSurface()
        {
            if (!IsHandleCreated || IsDisposed || presentingSurface || creatingSurfaceHandle)
                return;
            presentingSurface = true;
            try
            {
                using (Bitmap bitmap = OverlayRenderer.CreateBitmap(settings, snapshot,
                    resetCredits, status, overlayDpi))
                    LayeredWindowSurface.Present(Handle, bitmap, settings.Style.Opacity);
            }
            finally { presentingSurface = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            OverlayRenderer.Paint(e.Graphics, settings, snapshot, resetCredits, status, overlayDpi);
        }
    }

    internal sealed class AppServerClient : IDisposable
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly object writeLock = new object();
        private readonly object requestLock = new object();
        private readonly HashSet<int> rateLimitRequests = new HashSet<int>();
        private readonly Dictionary<int, int> accountRequests = new Dictionary<int, int>();
        private bool waitingForAccountSnapshot;
        private Process process;
        private Thread outputThread;
        private Thread errorThread;
        private int requestId = 10;
        private int generation;
        private int notificationSequence;
        private bool disposed;

        public event Action<RateSnapshot, int> SnapshotReceived;
        public event Action<string, int> StatusChanged;
        public event Action<int, int> AuthRevoked;
        public event Action<int> Initialized;
        public event Action<int, int, bool> ReadCompleted;
        public event Action<AccountState, int> AccountObserved;
        public event Action<int> AccountUpdated;

        // Notifications belong to this stdio connection, not the desktop GUI's.
        public event Action<int> UpdateNotificationReceived;

        public bool IsInitialized { get; private set; }
        public int Generation { get { return Interlocked.CompareExchange(ref generation, 0, 0); } }
        public int LatestRequestId { get { return Interlocked.CompareExchange(ref requestId, 0, 0); } }

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
                    DiagnosticLog.Write(
                        "app-server-candidate",
                        "executable=" + SafeExecutableName(candidate.FileName));
                    StartCandidate(candidate);
                    return;
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write(
                        "app-server-candidate-failed",
                        "executable=" + SafeExecutableName(candidate.FileName) +
                        " type=" + ex.GetType().Name);
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
            int startedGeneration = Interlocked.Increment(ref generation);
            Process startedProcess = process;
            IsInitialized = false;
            waitingForAccountSnapshot = true;
            DiagnosticLog.Write(
                "app-server-started",
                "pid=" + process.Id.ToString(CultureInfo.InvariantCulture) +
                " executable=" + SafeExecutableName(candidate.FileName));

            outputThread = new Thread(new ThreadStart(
                delegate { ReadOutput(startedProcess, startedGeneration); }));
            outputThread.IsBackground = true;
            outputThread.Name = "Codex app-server output";
            outputThread.Start();

            errorThread = new Thread(new ThreadStart(
                delegate { DrainErrors(startedProcess); }));
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

        public int RequestRateLimits(Action<int> onStarted)
        {
            if (!IsInitialized || !IsRunning)
                return 0;
            int id, accountId;
            lock (requestLock)
            {
                if (rateLimitRequests.Count > 0) return 0;
                id = Interlocked.Increment(ref requestId);
                accountId = Interlocked.Increment(ref requestId);
                rateLimitRequests.Add(id);
                accountRequests[accountId] = id;
            }
            if (onStarted != null) onStarted(id);
            // A local identity check prevents old account data from surviving a
            // switch. false avoids a proactive token refresh on every poll.
            SendRead("account/read", accountId,
                new Dictionary<string, object> { { "refreshToken", false } }, id);
            return id;
        }

        public void CancelRateLimitsRequest(int id)
        {
            lock (requestLock)
            {
                rateLimitRequests.Remove(id);
                foreach (int key in accountRequests.Where(p => p.Value == id).Select(p => p.Key).ToArray())
                    accountRequests.Remove(key);
            }
        }

        private void SendRead(string method, int rpcId, Dictionary<string, object> parameters, int readId)
        {
            try
            {
                lock (requestLock)
                {
                    if (!rateLimitRequests.Contains(readId)) return;
                    if (!IsRunning) throw new IOException("Child exited");
                    DiagnosticLog.Write(method == "account/read" ? "account-request" : "rate-request",
                        "id=" + rpcId.ToString(CultureInfo.InvariantCulture) +
                        " read_id=" + readId + " generation=" + Generation);
                    Send(new Dictionary<string, object> { { "method", method }, { "id", rpcId }, { "params", parameters } });
                }
            }
            catch (Exception ex)
            {
                CancelRateLimitsRequest(readId);
                DiagnosticLog.Write("rate-send-failed", "type=" + ex.GetType().Name);
                RaiseReadCompleted(readId, Generation, false);
                RaiseStatus(I18n.T("ServiceError"), Generation);
            }
        }

        // Full child restart. NOT part of the normal refresh loop any more --
        // periodic refresh uses the long-lived server. Only auth recovery uses
        // this restart path.
        public void RefreshRateLimits(bool force = false)
        {
            if (!force && !IsInitialized)
                return;

            DiagnosticLog.Write("app-server-refresh", "action=restart");
            DisposeProcess();
            Start();
        }

        private void ReadOutput(Process current, int startedGeneration)
        {
            try
            {
                string line;
                while (!disposed &&
                       (line = current.StandardOutput.ReadLine()) != null)
                {
                    if (startedGeneration != Generation)
                        break;
                    ProcessMessage(line, startedGeneration);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write("app-server-read-failed", "type=" + ex.GetType().Name);
                RaiseStatus(I18n.F("CommunicationError", ex.Message), startedGeneration);
            }
            finally
            {
                if (startedGeneration == Generation)
                    IsInitialized = false;
                DiagnosticLog.Write("app-server-output-ended", null);
            }
        }

        private void DrainErrors(Process current)
        {
            try
            {
                bool reported = false;
                while (!disposed && current.StandardError.ReadLine() != null)
                {
                    if (!reported)
                    {
                        DiagnosticLog.Write("app-server-stderr", "content=redacted");
                        reported = true;
                    }
                }
            }
            catch
            {
            }
        }

        private void ProcessMessage(string line, int startedGeneration)
        {
            if (startedGeneration != Generation)
                return;
            Dictionary<string, object> message;
            try
            {
                message = json.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                DiagnosticLog.Write("app-server-invalid-json", null);
                return;
            }
            if (message == null)
                return;

            int id;
            if (TryInt(message, "id", out id) && id == 1)
            {
                if (message.ContainsKey("error"))
                {
                    DiagnosticLog.Write("app-server-initialize-error", null);
                    RaiseStatus(I18n.T("InitializationFailed"), startedGeneration);
                    return;
                }
                var initialized = new Dictionary<string, object>();
                initialized["method"] = "initialized";
                Send(initialized);
                IsInitialized = true;
                DiagnosticLog.Write("app-server-initialized", null);
                Action<int> ready = Initialized;
                if (ready != null) ready(startedGeneration);
                return;
            }

            int readId = 0;
            if (TryInt(message, "id", out id))
            {
                lock (requestLock)
                {
                    if (accountRequests.TryGetValue(id, out readId))
                        accountRequests.Remove(id);
                }
            }
            if (readId != 0)
            {
                if (message.ContainsKey("error"))
                {
                    CancelRateLimitsRequest(readId);
                    RaiseReadCompleted(readId, startedGeneration, false);
                    var error = GetDictionary(message, "error");
                    DiagnosticLog.Write("account-response-error", "id=" + id + " read_id=" + readId +
                        " generation=" + startedGeneration + " " + DescribeError(error));
                    if (IsAuthRevokedError(error)) RaiseAuthRevoked(startedGeneration, LatestRequestId);
                    else RaiseStatus(I18n.T("ServiceError"), startedGeneration);
                    return;
                }
                AccountState account = ParseAccount(GetDictionary(message, "result"));
                account.ReadRequestId = readId;
                Action<AccountState, int> observed = AccountObserved;
                if (observed != null) observed(account, startedGeneration);
                if (!account.CanReadQuota)
                {
                    waitingForAccountSnapshot = true;
                    CancelRateLimitsRequest(readId);
                    RaiseReadCompleted(readId, startedGeneration, false);
                    return;
                }
                SendRead("account/rateLimits/read", readId, new Dictionary<string, object>(), readId);
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
                    var error = GetDictionary(message, "error");
                    DiagnosticLog.Write(
                        "rate-response-error",
                        "id=" + id.ToString(CultureInfo.InvariantCulture) +
                        " generation=" + startedGeneration + " " + DescribeError(error));
                    RaiseReadCompleted(id, startedGeneration, false);
                    if (IsAuthRevokedError(error))
                        RaiseAuthRevoked(startedGeneration, LatestRequestId);
                    else
                        RaiseStatus(GetRateLimitErrorStatus(message), startedGeneration);
                    return;
                }
                var result = GetDictionary(message, "result");
                RateSnapshot snapshot = ParseReadResult(result);
                if (snapshot != null)
                    snapshot.ReadRequestId = id;
                DiagnosticLog.Write(
                    "rate-response",
                    "id=" + id.ToString(CultureInfo.InvariantCulture) + " generation=" + startedGeneration +
                    " raw=" + DescribeRateLimitsContainer(result) +
                    " parsed=" + DescribeSnapshot(snapshot));
                RaiseReadCompleted(id, startedGeneration, snapshot != null);
                if (snapshot != null)
                {
                    waitingForAccountSnapshot = false;
                    RaiseSnapshot(snapshot, startedGeneration);
                }
                else
                    RaiseStatus(I18n.T("ServiceError"), startedGeneration);
                return;
            }

            string method = GetString(message, "method");
            if (method == "account/updated")
            {
                DiagnosticLog.Write("account-notification-received", "generation=" + startedGeneration);
                lock (requestLock)
                {
                    rateLimitRequests.Clear();
                    accountRequests.Clear();
                }
                waitingForAccountSnapshot = true;
                Action<int> changed = AccountUpdated;
                if (changed != null) changed(startedGeneration);
                return;
            }
            if (method == "account/rateLimits/updated")
            {
                int notificationId = Interlocked.Increment(ref notificationSequence);
                string notificationDetails = "id=" + notificationId + " generation=" + startedGeneration;
                DiagnosticLog.Write("rate-notification-received", notificationDetails);
                if (waitingForAccountSnapshot)
                {
                    DiagnosticLog.Write("rate-notification-ignored", notificationDetails + " reason=account-unverified");
                    return;
                }
                var parameters = GetDictionary(message, "params");
                RateSnapshot snapshot = ParseRateLimitsContainer(parameters);
                DiagnosticLog.Write(
                    "rate-notification",
                    notificationDetails + " state=" + (snapshot == null ? "invalid" : "accepted-for-validation") +
                    " raw=" + DescribeRateLimitsContainer(parameters) +
                    " parsed=" + DescribeSnapshot(snapshot));
                Action<int> notificationHandler = UpdateNotificationReceived;
                if (notificationHandler != null)
                    notificationHandler(startedGeneration);
                if (snapshot != null)
                {
                    snapshot.NotificationId = notificationId;
                    RaiseSnapshot(snapshot, startedGeneration);
                }
                else DiagnosticLog.Write("rate-notification-ignored", notificationDetails + " reason=invalid-payload");
                return;
            }
            if (TryInt(message, "id", out id))
                DiagnosticLog.Write("app-server-response-ignored", "id=" + id +
                    " generation=" + startedGeneration + " reason=untracked-read");
        }

        private static AccountState ParseAccount(Dictionary<string, object> result)
        {
            var account = GetDictionary(result, "account");
            string type = GetString(account, "type") ?? "signed-out";
            string identity = type + "|" + (GetString(account, "email") ?? "").Trim().ToLowerInvariant() +
                "|" + (GetString(account, "accountId") ?? "");
            string fingerprint;
            using (SHA256 hash = SHA256.Create())
                fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
            return new AccountState {
                Fingerprint = fingerprint,
                CanReadQuota = type == "chatgpt",
                StatusKey = type == "signed-out" ? "NotSignedIn" : "ChatGptAuthRequired"
            };
        }

        private RateSnapshot ParseReadResult(Dictionary<string, object> result)
        {
            RateSnapshot snapshot = ParseRateLimitsContainer(result);
            if (snapshot != null)
                snapshot.ReplaceMissingWindows = true;
            return snapshot;
        }

        private static RateSnapshot ParseRateLimitsContainer(Dictionary<string, object> source)
        {
            if (source == null)
                return null;

            RateSnapshot snapshot = ParseByLimitId(GetDictionary(source, "rateLimitsByLimitId"));
            if (HasUsageWindow(snapshot))
                return snapshot;

            snapshot = ParseSnapshot(GetDictionary(source, "rateLimits"));
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

            return null;
        }

        private static bool HasUsageWindow(RateSnapshot snapshot)
        {
            return snapshot != null && (snapshot.Primary != null || snapshot.Secondary != null);
        }

        private static string DescribeRateLimitsContainer(Dictionary<string, object> source)
        {
            if (source == null)
                return "null";

            var parts = new List<string>();
            AppendSnapshotDescription(parts, "rateLimits", GetDictionary(source, "rateLimits"));
            var byId = GetDictionary(source, "rateLimitsByLimitId");
            AppendSnapshotDescription(parts, "byId.codex", GetDictionary(byId, "codex"));
            AppendSnapshotDescription(parts, "direct", source);
            return parts.Count == 0 ? "no-windows" : string.Join(";", parts.ToArray());
        }

        private static void AppendSnapshotDescription(
            List<string> parts,
            string name,
            Dictionary<string, object> source)
        {
            if (source == null)
                return;
            var primary = GetDictionary(source, "primary");
            var secondary = GetDictionary(source, "secondary");
            if (primary == null && secondary == null)
                return;
            parts.Add(
                name + "{primary=" + DescribeRawWindow(primary) +
                ",secondary=" + DescribeRawWindow(secondary) + "}");
        }

        private static string DescribeRawWindow(Dictionary<string, object> source)
        {
            if (source == null)
                return "null";
            double used;
            long duration;
            long reset;
            return "used=" + (TryDouble(source, "usedPercent", out used)
                    ? used.ToString("0.###", CultureInfo.InvariantCulture)
                    : "missing") +
                ",duration=" + (TryLong(source, "windowDurationMins", out duration)
                    ? duration.ToString(CultureInfo.InvariantCulture)
                    : "missing") +
                ",reset=" + (TryLong(source, "resetsAt", out reset)
                    ? reset.ToString(CultureInfo.InvariantCulture)
                    : "missing");
        }

        internal static string DescribeSnapshot(RateSnapshot snapshot)
        {
            if (snapshot == null)
                return "null";
            return "primary=" + DescribeWindow(snapshot.Primary) +
                ",secondary=" + DescribeWindow(snapshot.Secondary);
        }

        private static string DescribeWindow(WindowUsage usage)
        {
            if (usage == null)
                return "null";
            return "used=" + usage.UsedPercent.ToString("0.###", CultureInfo.InvariantCulture) +
                ",duration=" + (usage.WindowDurationMins.HasValue
                    ? usage.WindowDurationMins.Value.ToString(CultureInfo.InvariantCulture)
                    : "missing") +
                ",reset=" + (usage.ResetsAt.HasValue
                    ? usage.ResetsAt.Value.ToString(CultureInfo.InvariantCulture)
                    : "missing");
        }

        private static string SafeExecutableName(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? "unknown" : Path.GetFileName(path);
            }
            catch
            {
                return "unknown";
            }
        }

        private static string DescribeRawMessage(Dictionary<string, object> message)
        {
            try
            {
                string json = new JavaScriptSerializer().Serialize(message);
                return json.Length > 400 ? json.Substring(0, 400) : json;
            }
            catch
            {
                return "(unserializable)";
            }
        }

        private static string GetRateLimitErrorStatus(Dictionary<string, object> message)
        {
            var error = GetDictionary(message, "error");
            string text = GetString(error, "message") ?? string.Empty;
            if (text.IndexOf("chatgpt authentication required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("api key auth is not supported", StringComparison.OrdinalIgnoreCase) >= 0)
                return I18n.T("ChatGptAuthRequired");

            if (text.IndexOf("not signed in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0)
                return I18n.T("NotSignedIn");
            return I18n.T("ServiceError");
        }

        internal static string DescribeError(Dictionary<string, object> error)
        {
            int code;
            bool hasCode = TryInt(error, "code", out code);
            string message = (GetString(error, "message") ?? "").ToLowerInvariant();
            string category = "unknown";
            if (IsAuthRevokedError(error) || hasCode && (code == 401 || code == 403) ||
                message.Contains("not signed in") || message.Contains("authentication required")) category = "auth";
            else if (hasCode && code == 429 || message.Contains("429") || message.Contains("too many requests")) category = "rate-limited";
            else if (hasCode && (code == 408 || code == 504) || message.Contains("timeout") || message.Contains("timed out")) category = "timeout";
            else if (message.Contains("connection") || message.Contains("network") || message.Contains("dns") ||
                message.Contains("econn") || message.Contains("failed to send request")) category = "network";
            else if (hasCode && code >= 500 && code <= 599) category = "server";
            else if (hasCode && (code == -32700 || code == -32600 || code == -32601 || code == -32602)) category = "protocol";
            else if (hasCode && code == -32603) category = "server";
            // Only fixed categories and a numeric protocol code leave this method.
            // The original error message/data can contain URLs, identities or credentials.
            return "category=" + category + " code=" + (hasCode ? code.ToString(CultureInfo.InvariantCulture) : "unavailable") +
                " content=redacted";
        }

        private static bool IsAuthRevokedError(Dictionary<string, object> error)
        {
            if (error == null)
                return false;
            string code = GetString(error, "code");
            if (string.Equals(code, "token_revoked", StringComparison.OrdinalIgnoreCase))
                return true;
            var data = GetDictionary(error, "data");
            code = GetString(data, "code");
            if (string.Equals(code, "token_revoked", StringComparison.OrdinalIgnoreCase))
                return true;
            string detail = GetString(error, "message");
            return detail != null &&
                detail.IndexOf("token_revoked", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static RateSnapshot ParseSnapshot(Dictionary<string, object> source)
        {
            if (source == null)
                return null;
            var snapshot = new RateSnapshot();
            AddWindow(snapshot, ParseWindow(GetDictionary(source, "primary")), true);
            AddWindow(snapshot, ParseWindow(GetDictionary(source, "secondary")), false);
            snapshot.PlanType = GetString(source, "planType");
            return snapshot;
        }

        private static void AddWindow(
            RateSnapshot snapshot,
            WindowUsage usage,
            bool wasPrimary)
        {
            if (usage == null)
                return;

            // The service normally returns the 5-hour window as primary and the
            // 7-day window as secondary. When the 5-hour limit is disabled, the
            // remaining 7-day window can move to primary, so duration is the
            // stable identifier and position is only a legacy fallback.
            if (usage.WindowDurationMins == 300)
            {
                snapshot.Primary = usage;
                return;
            }
            if (usage.WindowDurationMins == 10080)
            {
                snapshot.Secondary = usage;
                return;
            }

            if (wasPrimary && snapshot.Primary == null)
                snapshot.Primary = usage;
            else if (!wasPrimary && snapshot.Secondary == null)
                snapshot.Secondary = usage;
        }

        private static WindowUsage ParseWindow(Dictionary<string, object> source)
        {
            if (source == null)
                return null;
            var usage = new WindowUsage();
            usage.UsedPercent = GetDouble(source, "usedPercent");
            long value;
            if (TryLong(source, "windowDurationMins", out value))
                usage.WindowDurationMins = value;
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

        private void RaiseSnapshot(RateSnapshot snapshot, int startedGeneration)
        {
            Action<RateSnapshot, int> handler = SnapshotReceived;
            if (handler != null)
                handler(snapshot, startedGeneration);
        }

        private void RaiseReadCompleted(int id, int startedGeneration, bool success)
        {
            Action<int, int, bool> handler = ReadCompleted;
            if (handler != null) handler(id, startedGeneration, success);
        }

        private void RaiseStatus(string status, int startedGeneration)
        {
            Action<string, int> handler = StatusChanged;
            if (handler != null)
                handler(status, startedGeneration);
        }

        private void RaiseAuthRevoked(int startedGeneration, int latestRequestId)
        {
            Action<int, int> handler = AuthRevoked;
            if (handler != null)
                handler(startedGeneration, latestRequestId);
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
            double value;
            return TryDouble(source, key, out value) ? value : 0;
        }

        private static bool TryDouble(
            Dictionary<string, object> source,
            string key,
            out double value)
        {
            value = 0;
            if (source == null)
                return false;
            object raw;
            if (!source.TryGetValue(key, out raw) || raw == null)
                return false;
            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
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
            lock (requestLock)
            {
                rateLimitRequests.Clear();
                accountRequests.Clear();
            }
            waitingForAccountSnapshot = true;

            Process current = process;
            process = null;

            try
            {
                if (current != null && !current.HasExited)
                {
                    current.StandardInput.Close();
                    if (!current.WaitForExit(1000))
                        current.Kill();
                }
            }
            catch
            {
            }

            try
            {
                if (outputThread != null && outputThread != Thread.CurrentThread)
                    outputThread.Join(1000);
                if (errorThread != null && errorThread != Thread.CurrentThread)
                    errorThread.Join(1000);
            }
            catch
            {
            }
            outputThread = null;
            errorThread = null;

            if (current != null)
                current.Dispose();
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
        private static IntPtr lastDesktopWindow;

        public static DesktopUsageState GetUsageState(IntPtr mainWindow)
        {
            if (mainWindow == IntPtr.Zero || !NativeMethods.IsWindowVisible(mainWindow) ||
                NativeMethods.IsIconic(mainWindow)) return DesktopUsageState.Minimized;
            uint mainPid, foregroundPid;
            NativeMethods.GetWindowThreadProcessId(mainWindow, out mainPid);
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out foregroundPid);
            return mainPid != 0 && mainPid == foregroundPid ?
                DesktopUsageState.Foreground : DesktopUsageState.Background;
        }
        // True when the ChatGPT/Codex desktop app is running, even if its main
        // window is minimized to the tray (no visible window). Used to keep
        // low-frequency monitoring alive while the window is hidden.
        public static bool IsDesktopAppRunning()
        {
            try
            {
                foreach (Process process in DesktopAppProcess.GetRunningProcesses())
                {
                    bool isDesktop;
                    try
                    {
                        isDesktop = DesktopAppProcess.IsDesktopAppProcess(process);
                    }
                    catch
                    {
                        isDesktop = false;
                    }
                    if (isDesktop)
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public static IntPtr FindDesktopMainWindow()
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

            lastDesktopWindow = FindMainWindow(processIds, lastDesktopWindow,
                NativeMethods.GetForegroundWindow());
            return lastDesktopWindow;
        }

        private static bool IsMainWindowCandidate(IntPtr window, HashSet<uint> processIds)
        {
            if (window == IntPtr.Zero || !NativeMethods.IsWindowVisible(window))
                return false;

            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (!processIds.Contains(processId) ||
                NativeMethods.GetWindow(window, NativeMethods.GW_OWNER) != IntPtr.Zero)
                return false;

            int style = NativeMethods.GetWindowLong(window, NativeMethods.GWL_STYLE);
            int extendedStyle = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);
            if ((style & NativeMethods.WS_CHILD) != 0 ||
                (extendedStyle & (NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)) != 0)
                return false;

            // Menus can be visible and unowned, so Process.MainWindowHandle's
            // first-visible-unowned-window heuristic is insufficient here.
            var className = new StringBuilder(256);
            if (NativeMethods.GetClassName(window, className, className.Capacity) == 0)
                return false;
            string name = className.ToString();
            return !string.Equals(name, "#32768", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "Chrome_WidgetWin_2", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "tooltips_class32", StringComparison.OrdinalIgnoreCase);
        }

        private static IntPtr FindMainWindow(HashSet<uint> processIds,
            IntPtr previousWindow, IntPtr foregroundWindow)
        {
            if (processIds.Count == 0)
                return IntPtr.Zero;

            // Walk GW_OWNER explicitly: GA_ROOTOWNER uses GetParent, which
            // does not return the owner of an owned overlapped window.
            IntPtr foregroundMain = FindRootOwner(foregroundWindow);
            if (IsMainWindowCandidate(foregroundMain, processIds))
                return foregroundMain;
            if (IsMainWindowCandidate(previousWindow, processIds))
                return previousWindow;

            IntPtr found = IntPtr.Zero;
            long largestArea = 0;
            NativeMethods.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsMainWindowCandidate(window, processIds))
                    return true;

                NativeMethods.RECT rect;
                if (!NativeMethods.GetWindowRect(window, out rect))
                    return true;
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > largestArea)
                {
                    found = window;
                    largestArea = area;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static IntPtr FindRootOwner(IntPtr window)
        {
            IntPtr root = window == IntPtr.Zero ? IntPtr.Zero :
                NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT);
            for (int depth = 0; root != IntPtr.Zero && depth < 32; depth++)
            {
                IntPtr owner = NativeMethods.GetWindow(root, NativeMethods.GW_OWNER);
                if (owner == IntPtr.Zero)
                    return root;
                root = NativeMethods.GetAncestor(owner, NativeMethods.GA_ROOT);
            }
            return IntPtr.Zero;
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
                    return FindMainWindow(new HashSet<uint> { processId }, IntPtr.Zero, foreground);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }

    internal sealed class MonitorSettings
    {
        public string Language { get; set; }
        public string Position { get; set; }
        public string UsageDisplay { get; set; }

        // "1" = single horizontal row; "2" = two stacked rows. Decoupled from
        // Position so the corner placement and the line count are independent.
        public string DisplayLines { get; set; }
        // "auto" follows windows confirmed by a full read; "all" retains placeholders.
        public string UsagePanels { get; set; }
        public int RefreshSeconds { get; set; }
        public int ForegroundRefreshSeconds { get; set; }
        public bool DiagnosticsEnabled { get; set; }
        public int DiagnosticRetentionDays { get; set; }
        public StyleSettings Style { get; set; }

        // "attach" follows the ChatGPT/Codex window; "desktop" floats on the
        // Windows desktop and is always topmost (covers the taskbar).
        public string OverlayMode { get; set; }
        public int DesktopX { get; set; }
        public int DesktopY { get; set; }

        // Read-only rate-limit reset-credit display. The app only queries the
        // credits endpoint; it never redeems/consumes credits.
        public bool ShowResetCredits { get; set; }
        public int ResetCreditsSeconds { get; set; }

        // Refresh cadence while the desktop window is minimized/tray-only.
        // Polls continue at this reduced rate so data stays roughly fresh for
        // near-zero traffic (a read is a few hundred bytes).
        public int MinimizedRefreshSeconds { get; set; }

        public MonitorSettings()
        {
            Language = "auto";
            Position = "top";
            DisplayLines = "1";
            UsagePanels = "auto";
            UsageDisplay = "remaining";
            RefreshSeconds = 60;
            ForegroundRefreshSeconds = 30;
            DiagnosticsEnabled = false;
            DiagnosticRetentionDays = 7;
            Style = new StyleSettings();
            OverlayMode = "desktop";
            ShowResetCredits = true;
            ResetCreditsSeconds = 1800;
            MinimizedRefreshSeconds = 300;
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
                result = Parse(text);
            }
            catch
            {
            }
            result.Normalize();
            return result;
        }

        internal static MonitorSettings Parse(string text)
        {
            var serializer = new JavaScriptSerializer();
            var root = serializer.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Settings must be a JSON object.");
            var result = serializer.Deserialize<MonitorSettings>(text) ?? new MonitorSettings();
            if (!HasProperty(root, "DisplayLines"))
                result.DisplayLines = result.OverlayMode == "attach" && result.Position == "bottom-right" ? "2" : "1";
            var rawStyle = FindProperty(root, "Style") as Dictionary<string, object>;
            if (rawStyle == null || !HasProperty(rawStyle, "ScaleBasisVersion"))
            {
                // Existing files predate the compact baseline. Retain their
                // rendered size; absent scale uses the original release's 100%.
                if (result.Style == null) result.Style = new StyleSettings();
                result.Style.ScaleBasisVersion = 1;
                if (rawStyle == null || !HasProperty(rawStyle, "Scale")) result.Style.Scale = 1;
            }
            result.Normalize();
            return result;
        }

        private static object FindProperty(Dictionary<string, object> source, string key)
        {
            foreach (var item in source)
                if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) return item.Value;
            return null;
        }

        private static bool HasProperty(Dictionary<string, object> source, string key)
        {
            return source.Keys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
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
            clone.DisplayLines = DisplayLines;
            clone.UsagePanels = UsagePanels;
            clone.RefreshSeconds = RefreshSeconds;
            clone.ForegroundRefreshSeconds = ForegroundRefreshSeconds;
            clone.DiagnosticsEnabled = DiagnosticsEnabled;
            clone.DiagnosticRetentionDays = DiagnosticRetentionDays;
            clone.Style = Style == null ? new StyleSettings() : Style.Clone();
            clone.OverlayMode = OverlayMode;
            clone.DesktopX = DesktopX;
            clone.DesktopY = DesktopY;
            clone.ShowResetCredits = ShowResetCredits;
            clone.ResetCreditsSeconds = ResetCreditsSeconds;
            clone.MinimizedRefreshSeconds = MinimizedRefreshSeconds;
            clone.Normalize();
            return clone;
        }

        public void SelectPlacement(string mode, string position)
        {
            bool enteringCorner = mode == "attach" && position == "bottom-right" &&
                (OverlayMode != "attach" || Position != "bottom-right");
            OverlayMode = mode;
            Position = position;
            if (enteringCorner) DisplayLines = "2";
        }

        private void Normalize()
        {
            Language = I18n.NormalizeSetting(Language);
            if (Position != "top" && Position != "bottom-right")
                Position = "top";
            if (DisplayLines != "1" && DisplayLines != "2")
                DisplayLines = OverlayMode == "attach" && Position == "bottom-right" ? "2" : "1";
            if (UsagePanels != "all" && UsagePanels != "weekly") UsagePanels = "auto";
            if (OverlayMode != "attach" && OverlayMode != "desktop")
                OverlayMode = "desktop";
            UsageDisplay = UsageDisplayTools.Normalize(UsageDisplay);
            RefreshSeconds = Math.Max(30, Math.Min(900, RefreshSeconds));
            ForegroundRefreshSeconds = Math.Max(30, Math.Min(RefreshSeconds, ForegroundRefreshSeconds));
            ResetCreditsSeconds = Math.Max(300, Math.Min(86400, ResetCreditsSeconds));
            MinimizedRefreshSeconds = Math.Max(60, Math.Min(3600, MinimizedRefreshSeconds));
            DiagnosticRetentionDays = Math.Max(1, Math.Min(30, DiagnosticRetentionDays));
            if (Style == null)
                Style = new StyleSettings();
            Style.Normalize();
        }
    }

    internal sealed class StyleSettings
    {
        internal const int CurrentScaleBasisVersion = 2;
        internal const double BaselineScale = 0.85;
        internal const double MinimumScale = 0.5;
        internal const double MaximumScale = 2.0;
        public int ScaleBasisVersion { get; set; }
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
            ScaleBasisVersion = CurrentScaleBasisVersion;
            Scale = 1;
            Opacity = 0.97;
            CornerRadius = 9;
            FontSize = 14;
            ResetFontSize = 13;
            FontFamily = "Microsoft JhengHei UI";
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
            if (double.IsNaN(Scale) || double.IsInfinity(Scale)) Scale = 1;
            if (ScaleBasisVersion < CurrentScaleBasisVersion)
            {
                Scale = Math.Max(0.75, Math.Min(1.5, Scale)) / BaselineScale;
                ScaleBasisVersion = CurrentScaleBasisVersion;
            }
            Scale = Math.Max(MinimumScale, Math.Min(MaximumScale, Scale));
            Opacity = Math.Max(0.5, Math.Min(1.0, Opacity));
            CornerRadius = Math.Max(0, Math.Min(20, CornerRadius));
            FontSize = Math.Max(10, Math.Min(22, FontSize));
            ResetFontSize = Math.Max(9, Math.Min(18, ResetFontSize));
            if (string.IsNullOrWhiteSpace(FontFamily))
                FontFamily = "Microsoft JhengHei UI";
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
                ScaleBasisVersion = ScaleBasisVersion,
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

    internal sealed class AccountState
    {
        public string Fingerprint { get; set; }
        public bool CanReadQuota { get; set; }
        public string StatusKey { get; set; }
        public int ReadRequestId { get; set; }
    }

    internal sealed class RateSnapshot
    {
        public int NotificationId { get; set; }
        public WindowUsage Primary { get; set; }
        public WindowUsage Secondary { get; set; }
        public string PlanType { get; set; }
        public bool ReplaceMissingWindows { get; set; }
        // 1 = 5h, 2 = 7d; null means no complete read has confirmed availability.
        public int? ConfirmedWindows { get; set; }
        public int ReadRequestId { get; set; }
    }

    internal sealed class WindowUsage
    {
        public double UsedPercent { get; set; }
        public long? WindowDurationMins { get; set; }
        public long? ResetsAt { get; set; }
    }

    internal sealed class ResetCreditsInfo
    {
        public int AvailableCount { get; set; }

        // Local-time snapshots of the earliest expiring available credit.
        public DateTime? EarliestExpiry { get; set; }
        public DateTime? EarliestGranted { get; set; }

        public string FormatEarliestExpiry()
        {
            return EarliestExpiry.HasValue
                ? EarliestExpiry.Value.ToString("MM-dd", CultureInfo.CurrentCulture)
                : "--";
        }

        // Elapsed fraction of the earliest credit's lifetime, for the thin
        // lifetime bar. 0 when the grant timestamp is unknown.
        public double ElapsedFraction()
        {
            if (!EarliestExpiry.HasValue || !EarliestGranted.HasValue)
                return 0d;
            double total = (EarliestExpiry.Value - EarliestGranted.Value).TotalHours;
            if (total <= 0d)
                return 0d;
            double elapsed = (DateTime.Now - EarliestGranted.Value).TotalHours;
            return Math.Max(0d, Math.Min(1d, elapsed / total));
        }
    }

    // SECURITY / SCOPE NOTE: this client is strictly READ-ONLY. It performs a
    // single GET against the reset-credit info endpoint so the monitor can
    // display balances and expiry dates. Redemption endpoints (e.g. POST
    // .../rate-limit-reset-credits/consume or any app-server consume method)
    // must NEVER be called from this application.
    internal static class ResetCreditsClient
    {
        private const string EndpointUrl =
            "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

        static ResetCreditsClient()
        {
            // .NET 4.8 negotiates TLS 1.2+ by default, but force-enable 1.2 so
            // the request never falls back on older policy defaults.
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12;
            }
            catch
            {
            }
        }

        public static ResetCreditsInfo Fetch()
        {
            string token = null;
            string accountId = null;
            try
            {
                string authPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex",
                    "auth.json");
                if (!File.Exists(authPath))
                    return null;
                var auth = new JavaScriptSerializer().DeserializeObject(
                    File.ReadAllText(authPath, Encoding.UTF8)) as Dictionary<string, object>;
                token = GetString(GetDictionary(auth, "tokens"), "access_token");
                accountId = GetString(GetDictionary(auth, "tokens"), "account_id");
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(token))
                return null;

            try
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(EndpointUrl);
                request.Method = "GET"; // read-only by design
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.Accept = "application/json";
                // Community tooling consistently uses the CLI's own UA against
                // this endpoint; generic tool UAs get blocked at the edge.
                request.UserAgent = "codex_cli_rs/" + BuildVersion.Value;
                request.Headers[System.Net.HttpRequestHeader.Authorization] = "Bearer " + token;
                if (!string.IsNullOrEmpty(accountId))
                    request.Headers["chatgpt-account-id"] = accountId;
                // Follow the machine's WinINET proxy configuration (the same
                // system proxy the user's browser uses), with no special auth.
                request.Proxy = System.Net.WebRequest.DefaultWebProxy;

                using (var response = (System.Net.HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != System.Net.HttpStatusCode.OK)
                        return null;
                    using (var reader = new StreamReader(
                        response.GetResponseStream(), Encoding.UTF8))
                    {
                        return Parse(reader.ReadToEnd());
                    }
                }
            }
            catch (System.Net.WebException ex)
            {
                string details = "status=" + ex.Status;
                try
                {
                    var httpResponse = ex.Response as System.Net.HttpWebResponse;
                    if (httpResponse != null)
                        details += " httpcode=" + (int)httpResponse.StatusCode;
                }
                catch
                {
                }
                DiagnosticLog.Write("reset-credits-fetch-failed", details);
                return null;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(
                    "reset-credits-fetch-failed",
                    "type=" + ex.GetType().Name);
                return null;
            }
        }

        private static ResetCreditsInfo Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            var root = new JavaScriptSerializer().DeserializeObject(json)
                as Dictionary<string, object>;
            if (root == null)
                return null;

            var info = new ResetCreditsInfo();
            long available;
            if (TryLong(root, "available_count", out available))
                info.AvailableCount = (int)available;

            DateTime? earliestExpiry = null;
            DateTime? earliestGranted = null;
            var credits = root.ContainsKey("credits")
                ? root["credits"] as object[]
                : null;
            if (credits != null)
            {
                foreach (object item in credits)
                {
                    var credit = item as Dictionary<string, object>;
                    if (credit == null)
                        continue;
                    string status = GetString(credit, "status");
                    if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
                        continue;
                    DateTime expiry = ParseUtcDate(GetString(credit, "expires_at"));
                    if (expiry == DateTime.MinValue)
                        continue;
                    DateTime granted = ParseUtcDate(GetString(credit, "granted_at"));
                    if (!earliestExpiry.HasValue || expiry < earliestExpiry.Value)
                    {
                        earliestExpiry = expiry;
                        earliestGranted = granted == DateTime.MinValue
                            ? (DateTime?)null
                            : granted;
                    }
                }
            }

            info.EarliestExpiry = earliestExpiry;
            info.EarliestGranted = earliestGranted;
            return info;
        }

        private static DateTime ParseUtcDate(string value)
        {
            if (string.IsNullOrEmpty(value))
                return DateTime.MinValue;
            DateTime parsed;
            if (!DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
                return DateTime.MinValue;
            return parsed.ToLocalTime();
        }

        private static Dictionary<string, object> GetDictionary(
            Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null ||
                !source.TryGetValue(key, out value) || value == null)
                return null;
            return value as Dictionary<string, object>;
        }

        private static string GetString(
            Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null)
                return null;
            return value as string;
        }

        private static bool TryLong(
            Dictionary<string, object> source, string key, out long value)
        {
            value = 0;
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                return false;
            return long.TryParse(
                raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }

    internal sealed class RateSnapshotStabilizer
    {
        private const double PercentTolerance = 0.5d;
        private const long ResetMatchToleranceSeconds = 120;
        private const int RequiredResetConfirmations = 3;

        private RateSnapshot accepted;
        private RateSnapshot pending;
        private DateTimeOffset pendingObservedAt;
        private int pendingConfirmations;

        public void Reset()
        {
            accepted = null;
            pending = null;
            pendingConfirmations = 0;
        }

        public bool TryAccept(
            RateSnapshot candidate,
            DateTimeOffset observedAt,
            out string validation)
        {
            validation = "accepted=normal";
            if (candidate == null)
                return false;

            if (accepted == null || !IsSuspiciousRegression(accepted, candidate, observedAt))
            {
                accepted = Clone(candidate);
                pending = null;
                pendingConfirmations = 0;
                return true;
            }

            if (pending != null && SameWindowGeneration(
                pending, candidate, observedAt - pendingObservedAt))
            {
                pending = Clone(candidate);
                pendingObservedAt = observedAt;
                pendingConfirmations++;
                if (pendingConfirmations >= RequiredResetConfirmations)
                {
                    accepted = Clone(candidate);
                    pending = null;
                    pendingConfirmations = 0;
                    validation = "accepted=confirmed-reset confirmations=" +
                        RequiredResetConfirmations.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                validation = "accepted=deferred-suspicious-regression confirmations=" +
                    pendingConfirmations.ToString(CultureInfo.InvariantCulture) + "/" +
                    RequiredResetConfirmations.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            pending = Clone(candidate);
            pendingObservedAt = observedAt;
            pendingConfirmations = 1;
            validation = "accepted=deferred-suspicious-regression confirmations=1/" +
                RequiredResetConfirmations.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static bool IsSuspiciousRegression(
            RateSnapshot current,
            RateSnapshot candidate,
            DateTimeOffset observedAt)
        {
            long now = observedAt.ToUnixTimeSeconds();
            return WindowRegressedBeforeReset(current.Primary, candidate.Primary, now) ||
                WindowRegressedBeforeReset(current.Secondary, candidate.Secondary, now);
        }

        private static bool WindowRegressedBeforeReset(
            WindowUsage current,
            WindowUsage candidate,
            long now)
        {
            return current != null && candidate != null &&
                current.ResetsAt.HasValue && current.ResetsAt.Value > now &&
                candidate.UsedPercent + PercentTolerance < current.UsedPercent;
        }

        private static bool SameWindowGeneration(
            RateSnapshot first,
            RateSnapshot second,
            TimeSpan elapsed)
        {
            return WindowsMatch(first.Primary, second.Primary, elapsed) &&
                WindowsMatch(first.Secondary, second.Secondary, elapsed);
        }

        private static bool WindowsMatch(
            WindowUsage first,
            WindowUsage second,
            TimeSpan elapsed)
        {
            if (first == null || second == null)
                return first == null && second == null;
            if (second.UsedPercent + PercentTolerance < first.UsedPercent)
                return false;
            if (!first.ResetsAt.HasValue || !second.ResetsAt.HasValue)
                return first.ResetsAt.HasValue == second.ResetsAt.HasValue;

            long delta = second.ResetsAt.Value - first.ResetsAt.Value;
            long elapsedSeconds = Math.Max(0L, (long)Math.Round(elapsed.TotalSeconds));
            return Math.Abs(delta) <= ResetMatchToleranceSeconds ||
                Math.Abs(delta - elapsedSeconds) <= ResetMatchToleranceSeconds;
        }

        private static RateSnapshot Clone(RateSnapshot value)
        {
            if (value == null)
                return null;
            return new RateSnapshot
            {
                Primary = Clone(value.Primary),
                Secondary = Clone(value.Secondary),
                PlanType = value.PlanType,
                ConfirmedWindows = value.ConfirmedWindows,
                ReplaceMissingWindows = value.ReplaceMissingWindows
            };
        }

        private static WindowUsage Clone(WindowUsage value)
        {
            if (value == null)
                return null;
            return new WindowUsage
            {
                UsedPercent = value.UsedPercent,
                WindowDurationMins = value.WindowDurationMins,
                ResetsAt = value.ResetsAt
            };
        }
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
        public const int BottomRightWidth = 252;
        public const int BottomRightHeight = 78;
        public const int BottomRightHeightWithCredits = 106;
        public const int TopWidthWithCredits = 702;

        public static RectangleF GetBottomRightCardBounds(bool primary)
        {
            // Six pixels between rows keeps larger user-selected fonts legible.
            return new RectangleF(3, primary ? 3 : 42, 246, 33);
        }

        public static RectangleF GetCreditsRowBounds()
        {
            return new RectangleF(3, 81, 246, 22);
        }

        public static void DrawCreditsText(
            Graphics graphics,
            RectangleF bounds,
            string count,
            string expiry,
            Font mainFont,
            Font smallFont,
            Brush textBrush,
            Brush deadlineBrush)
        {
            const float leftPadding = 7f;
            const float rightPadding = 7f;

            using (StringFormat textFormat = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                textFormat.FormatFlags |= StringFormatFlags.NoWrap |
                                          StringFormatFlags.MeasureTrailingSpaces;

                // Same single-line composition as DrawUsageText: bold count on
                // the left, right-aligned deadline in the small font.
                float centerLine = bounds.Top + (bounds.Height - 4f) / 2f;
                float mainTop = centerLine - GetCellHeight(mainFont) / 2f;
                float smallTop = centerLine - GetCellHeight(smallFont) / 2f;

                graphics.DrawString(count, mainFont, textBrush,
                    new PointF(bounds.Left + leftPadding, mainTop), textFormat);

                // Reserve the count's measured width so increasing either font
                // cannot draw the right-aligned deadline over the main text.
                float expiryLeft = bounds.Left + leftPadding +
                    MeasureTextWidth(graphics, count, mainFont, textFormat) + 6f;
                float expiryWidth = bounds.Right - rightPadding - expiryLeft;
                if (expiryWidth <= 0f)
                    return;
                using (StringFormat expiryFormat = (StringFormat)textFormat.Clone())
                {
                    expiryFormat.Alignment = StringAlignment.Far;
                    expiryFormat.Trimming = StringTrimming.EllipsisCharacter;
                    float expiryHeight = GetCellHeight(smallFont) + 2f;
                    graphics.DrawString(expiry, smallFont, deadlineBrush,
                        new RectangleF(
                            expiryLeft,
                            smallTop,
                            expiryWidth,
                            expiryHeight),
                        expiryFormat);
                }
            }
        }

        public static void DrawUsageText(
            Graphics graphics,
            RectangleF bounds,
            string label,
            string percent,
            string reset,
            Font mainFont,
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

                // Reserve the bottom strip for the progress bar, then center the
                // main and reset font cells on the same horizontal line.
                float centerLine = bounds.Top + (bounds.Height - 4f) / 2f;
                float mainTop = centerLine - GetCellHeight(mainFont) / 2f;
                float resetTop = centerLine - GetCellHeight(resetFont) / 2f;

                float labelX = bounds.Left + leftPadding;
                float labelWidth = MeasureTextWidth(graphics, label, mainFont, textFormat);
                float percentX = labelX + labelWidth + labelGap;
                float percentWidth = MeasureTextWidth(graphics, percent, mainFont, textFormat);

                graphics.DrawString(label, mainFont, textBrush,
                    new PointF(labelX, mainTop), textFormat);
                graphics.DrawString(percent, mainFont, textBrush,
                    new PointF(percentX, mainTop), textFormat);

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
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const int SW_SHOWNOACTIVATE = 4;
        internal const uint GW_OWNER = 4;
        internal const uint GA_ROOT = 2;
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_CHILD = 0x40000000;
        internal const int WS_EX_TOOLWINDOW = 0x80;
        internal const int WS_EX_NOACTIVATE = 0x08000000;

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
        internal static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint command);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        internal static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

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
