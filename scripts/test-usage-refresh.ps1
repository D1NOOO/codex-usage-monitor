[CmdletBinding()]
param([string]$MonitorPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) { throw 'Build the monitor first.' }
$testDirectory = Join-Path $repoRoot '.build\usage-refresh-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'UsageRefreshTest.cs'
$testExe = Join-Path $testDirectory 'UsageRefreshTest.exe'
@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CodexRateMonitorNative;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class UsageRefreshTest
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int assertions, snapshots, completed, notifications, accounts, changes, initialized;
    private static object lastSnapshot, lastAccount;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly object Sync = new object();
    private static Type loggerType;
    private static string logDirectory;
    private static void Check(bool ok, string name) {
        if (!ok) throw new Exception(name);
        assertions++;
        Console.WriteLine("PASS " + name);
    }
    private static object Call(object target, string name, params object[] args) {
        return target.GetType().GetMethod(name, Methods).Invoke(target, args);
    }
    private static object Get(object target, string name) { return target.GetType().GetProperty(name).GetValue(target, null); }
    private static void Set(object target, string name, object value) { target.GetType().GetProperty(name).SetValue(target, value, null); }
    private static void Field(object target, string name, object value) { target.GetType().GetField(name, Fields).SetValue(target, value); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Fields).GetValue(target); }
    private static void Wait(Func<bool> ready, string label) {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000) {
            lock (Sync) { if (ready()) return; }
            Thread.Sleep(10);
        }
        throw new Exception("Timed out: " + label);
    }
    private static void CaptureSnapshot<T>(T value, int generation) { lock (Sync) { lastSnapshot = value; snapshots++; } }
    private static void CaptureAccount<T>(T value, int generation) { lock (Sync) { lastAccount = value; accounts++; } }
    private static void Subscribe(object client, string eventName, string callback, Type genericType) {
        EventInfo ev = client.GetType().GetEvent(eventName);
        MethodInfo handler = typeof(UsageRefreshTest).GetMethod(callback, BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(genericType);
        ev.AddEventHandler(client, Delegate.CreateDelegate(ev.EventHandlerType, handler));
    }
    [STAThread]
    private static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--fake-server") { FakeServer(); return; }
        try {
            Assembly assembly = Assembly.LoadFrom(args[0]);
            logDirectory=Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),"logs-"+Guid.NewGuid().ToString("N"));
            loggerType=assembly.GetType("CodexRateMonitorNative.DiagnosticLog",true);
            Type storeType=assembly.GetType("CodexRateMonitorNative.DiagnosticLogStore",true);
            loggerType.GetField("store",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
                Activator.CreateInstance(storeType,new object[]{logDirectory,20L*1024*1024,2L*1024*1024}));
            loggerType.GetMethod("Configure").Invoke(null,new object[]{true,7});
            SchedulerTests();
            RetryDeadlineTests(assembly);
            ProtocolTests(assembly);
            ContextTests(assembly);
            DiagnosticTests(assembly);
            Console.WriteLine("Passed " + assertions + " usage refresh assertions.");
        } catch (Exception ex) {
            Console.Error.WriteLine("FAIL " + ex);
            Environment.ExitCode = 1;
        }
    }
    private static void SchedulerTests() {
        var s = new UsageRefreshScheduler();
        Check(UsageRefreshScheduler.Interval(DesktopUsageState.Foreground,30,60,300)==30 &&
            UsageRefreshScheduler.Interval(DesktopUsageState.Background,30,60,300)==60 &&
            UsageRefreshScheduler.Interval(DesktopUsageState.Minimized,30,60,300)==300, "three window cadences");
        s.ObserveWindow(DesktopUsageState.Background, 0);
        Check(s.ShouldRead(0,60) && s.BeginRead(1,1,0,false), "startup reads immediately");
        Check(!s.BeginRead(2,1,0,true) && !s.ShouldRead(60,30), "manual and timer reads merge while pending");
        Check(!s.CompleteRead(1,2,1,true) && !s.CompleteRead(2,1,1,true), "wrong generation and id cannot release pending read");
        s.CompleteRead(1,1,1,true);
        Check(!s.ShouldRead(60,60) && s.ShouldRead(61,60), "background deadline follows successful response");
        s.ObserveWindow(DesktopUsageState.Foreground,61);
        Check(!s.ShouldRead(61.5,30) && s.ShouldRead(62,30), "foreground supplement waits one second even when periodic read is due");
        s.BeginRead(3,1,62,false);
        s.ObserveWindow(DesktopUsageState.Minimized,62.1);
        s.ObserveWindow(DesktopUsageState.Background,62.2);
        s.ObserveWindow(DesktopUsageState.Foreground,62.5);
        s.CompleteRead(3,1,64,true);
        Check(!s.ShouldRead(64,30), "response after coalesced restore events covers their supplement");
        s.Trigger(65,1); s.Trigger(65.5,1);
        Check(!s.ShouldRead(66,30) && s.ShouldRead(66.5,30), "wake and focus triggers debounce together");
        s.BeginRead(4,1,66.5,false); s.CompleteRead(4,1,67,true);
        s.ObserveWindow(DesktopUsageState.Minimized,68);
        Check(!s.ShouldRead(366,300) && s.ShouldRead(367,300), "tray mode reduces frequency");
        s.Reset(400,1);
        Check(!s.ShouldRead(400.9,30) && s.ShouldRead(401,30), "account change schedules a debounced fresh read");
        s.BeginRead(5,2,401,false);
        int id, gen;
        Check(!s.ExpireRead(430.9,out id,out gen) && s.ExpireRead(431,out id,out gen) && id==5 && gen==2, "read times out at thirty seconds");
        Check(!s.CompleteRead(5,2,432,true) && !s.ShouldRead(460.9,30) && s.ShouldRead(461,30), "late completion is ignored and timeout enters backoff");
        s.Reset(0,0);
        double now=0;
        int[] waits={30,60,120,240,300,300};
        for(int i=0;i<waits.Length;i++) {
            s.BeginRead(10+i,3,now,false); s.CompleteRead(10+i,3,now,false);
            Check(!s.ShouldRead(now+waits[i]-0.01,30) && s.ShouldRead(now+waits[i],30), "failure backoff " + waits[i] + " seconds #"+i);
            now+=waits[i];
        }
        Check(s.BeginRead(30,3,now-1,true), "explicit manual retry bypasses failure delay");
        s.CompleteRead(30,3,now,true);
        Check(s.FailureCount==0 && !s.ShouldRead(now+29,30) && s.ShouldRead(now+30,30), "success restores foreground cadence");
        foreach(var state in new[]{DesktopUsageState.Foreground,DesktopUsageState.Background,DesktopUsageState.Minimized}) {
            s=new UsageRefreshScheduler();
            int count=0,interval=UsageRefreshScheduler.Interval(state,30,60,300);
            for(double t=0;t<600;t+=0.25) if(s.ShouldRead(t,interval)) {
                s.BeginRead(++count,1,t,false);s.CompleteRead(count,1,t,true);
            }
            Check(count==600/interval,"ten minutes yields " + count + " reads in " + state);
        }
        s=new UsageRefreshScheduler();
        DateTimeOffset epoch=DateTimeOffset.FromUnixTimeSeconds(1000);
        s.BeginRead(1,1,0,false);s.CompleteRead(1,1,0,true);
        s.ObserveResets(1010,null,epoch,0,100,null);
        Check(!s.ShouldRead(11.99,300) && s.ShouldRead(12,300),"reset supplement runs at reset plus two seconds in minimized mode");
        s.BeginRead(2,1,12,false);
        Check(s.PendingReason=="reset_due" && !s.ShouldRead(13,300),"reset shares the single pending read and records its reason");
        s.CompleteRead(2,1,14,true);s.ObserveResets(1010,null,epoch.AddSeconds(14),14,100,null);
        Check(!s.ShouldRead(23.99,300) && s.ShouldRead(24,300),"unchanged reset gets one confirmation ten seconds after completion");
        s.BeginRead(3,1,24,false);s.CompleteRead(3,1,25,true);
        s.ObserveResets(1010,null,epoch.AddSeconds(25),25,100,null);
        Check(!s.ShouldRead(35,300),"unchanged expired reset cannot create an endless fast polling loop");
        s=new UsageRefreshScheduler();s.BeginRead(1,1,0,false);s.CompleteRead(1,1,0,true);
        s.ObserveResets(1010,1010,epoch,0,100,80);
        s.BeginRead(2,1,12,false);s.CompleteRead(2,1,13,true);
        s.ObserveResets(2000,2000,epoch.AddSeconds(13),13,0,0);
        Check(!s.ShouldRead(23,300),"advanced resets cancel both old confirmations with one shared read");
        s=new UsageRefreshScheduler();s.BeginRead(1,1,0,false);s.CompleteRead(1,1,0,true);
        s.ObserveResets(1010,null,epoch,0,100,null);
        s.BeginRead(2,1,12,false);s.CompleteRead(2,1,13,true);
        s.ObserveResets(1010,null,epoch.AddSeconds(13),13,0,null);
        Check(!s.ShouldRead(23,300),"server-confirmed usage reset cancels confirmation even with an unchanged timestamp");
        s=new UsageRefreshScheduler();s.BeginRead(1,1,0,false);s.CompleteRead(1,1,0,true);
        s.ObserveResets(1010,null,epoch,0);s.BeginRead(2,1,12,false);s.CompleteRead(2,1,13,false);
        s.Trigger(14,1,"focus");
        Check(!s.ShouldRead(42.99,30) && s.ShouldRead(43,300),"reset and focus supplements respect network backoff");
        s.BeginRead(3,1,43,false);
        Check(s.PendingReason=="retry" && s.PendingAge(44)==1,"retry reason and monotonic request age survive supplemental triggers");
        s.CompleteRead(3,1,44,true);s.ClearResetTargets();
        Check(!s.ShouldRead(45,300),"clearing account reset targets removes scheduled reset work");
        s=new UsageRefreshScheduler();s.ObserveWindow(DesktopUsageState.Minimized,0);s.ObserveWindow(DesktopUsageState.Background,1);
        s.BeginRead(1,1,2,false);Check(s.PendingReason=="restore","restoring a window has a distinct refresh reason");
        s.CompleteRead(1,1,2,true);s.ObserveWindow(DesktopUsageState.Foreground,3);
        s.BeginRead(2,1,4,false);Check(s.PendingReason=="focus","foreground supplement has a distinct refresh reason");
        s.CompleteRead(2,1,4,true);s.Trigger(5,1,"resume");s.BeginRead(3,1,6,false);
        Check(s.PendingReason=="resume","wake supplement has a distinct refresh reason");
        s.CompleteRead(3,1,6,true);s.BeginRead(4,1,7,true);Check(s.PendingReason=="manual","manual refresh reason is preserved");
    }
    private static void RetryDeadlineTests(Assembly assembly) {
        Type schedulerType=assembly.GetType("CodexRateMonitorNative.UsageRefreshScheduler",true);
        Type stateType=assembly.GetType("CodexRateMonitorNative.DesktopUsageState",true);
        string[] states={"Foreground","Background","Minimized"};
        int[] intervals={30,60,300};
        int[] waits={30,60,120,240,300,300};
        for(int state=0;state<states.Length;state++) {
            object s=Activator.CreateInstance(schedulerType,true);
            Call(s,"ObserveWindow",Enum.Parse(stateType,states[state]),0d);
            int interval=intervals[state],id=100;
            double started=0;
            Check((bool)Call(s,"BeginRead",id,1,started,false),"production retry setup in "+states[state]);
            for(int attempt=0;attempt<waits.Length;attempt++) {
                double failed=started+5.024,deadline=failed+waits[attempt];
                string label=states[state]+" failure #"+(attempt+1);
                Check((bool)Call(s,"CompleteRead",id,1,failed,false) &&
                    (int)Get(s,"FailureCount")==attempt+1 &&
                    Math.Abs((double)Call(s,"RetryDelay",failed)-waits[attempt])<0.001,
                    label+" records "+waits[attempt]+" seconds of backoff");
                Check(!(bool)Call(s,"ShouldRead",deadline-0.01,interval) &&
                    !(bool)Call(s,"BeginRead",id+1,1,deadline-0.01,false),
                    label+" cannot retry before its deadline");
                Check((bool)Call(s,"ShouldRead",deadline,interval) &&
                    (bool)Call(s,"BeginRead",++id,1,deadline,false),
                    label+" retries at the deadline regardless of window cadence");
                Check((string)Get(s,"PendingReason")=="retry" &&
                    !(bool)Call(s,"ShouldRead",deadline+interval+waits[attempt],interval) &&
                    !(bool)Call(s,"BeginRead",id+1,1,deadline,true),
                    label+" retains retry origin and merges pending requests");
                started=deadline;
            }
            double success=started+1;
            Check((bool)Call(s,"CompleteRead",id,1,success,true) &&
                (int)Get(s,"FailureCount")==0 && (double)Call(s,"RetryDelay",success)==0 &&
                !(bool)Call(s,"ShouldRead",success+interval-0.01,interval) &&
                (bool)Call(s,"ShouldRead",success+interval,interval),
                states[state]+" restores its normal cadence after retry succeeds");
            s=Activator.CreateInstance(schedulerType,true);
            Call(s,"ObserveWindow",Enum.Parse(stateType,states[state]),0d);
            Call(s,"BeginRead",1000,1,0d,false);
            object[] expired={30d,0,0};
            Check((bool)schedulerType.GetMethod("ExpireRead").Invoke(s,expired) &&
                (int)expired[1]==1000 && (int)expired[2]==1,
                states[state]+" timeout tracks the expired production request");
            Check(!(bool)Call(s,"ShouldRead",59.99,interval) &&
                (bool)Call(s,"ShouldRead",60d,interval) &&
                (bool)Call(s,"BeginRead",1001,1,60d,false) && (string)Get(s,"PendingReason")=="retry",
                states[state]+" timeout retries thirty seconds later regardless of window cadence");
            s=Activator.CreateInstance(schedulerType,true);
            Call(s,"BeginRead",2000,1,0d,false);Call(s,"CompleteRead",2000,1,5d,false);
            Call(s,"Trigger",34.5,10d,"focus");
            Check((bool)Call(s,"ShouldRead",35d,interval) &&
                (bool)Call(s,"BeginRead",2001,1,35d,false) && (string)Get(s,"PendingReason")=="retry",
                states[state]+" queued focus debounce cannot postpone a due retry");
        }
    }
    private static void Control(object client, string command) {
        Call(client,"Send",new Dictionary<string,object>{{"method","test/control"},{"params",command}});
    }
    private static int Read(object client) { return (int)Call(client,"RequestRateLimits",new object[]{null}); }
    private static void ProtocolTests(Assembly assembly) {
        Type clientType=assembly.GetType("CodexRateMonitorNative.AppServerClient",true);
        object client=Activator.CreateInstance(clientType,true);
        Subscribe(client,"SnapshotReceived","CaptureSnapshot",assembly.GetType("CodexRateMonitorNative.RateSnapshot",true));
        Subscribe(client,"AccountObserved","CaptureAccount",assembly.GetType("CodexRateMonitorNative.AccountState",true));
        clientType.GetEvent("Initialized").AddEventHandler(client,new Action<int>(delegate(int g){lock(Sync){initialized++;}}));
        clientType.GetEvent("ReadCompleted").AddEventHandler(client,new Action<int,int,bool>(delegate(int i,int g,bool ok){lock(Sync){completed++;}}));
        clientType.GetEvent("UpdateNotificationReceived").AddEventHandler(client,new Action<int>(delegate(int g){lock(Sync){notifications++;}}));
        clientType.GetEvent("AccountUpdated").AddEventHandler(client,new Action<int>(delegate(int g){lock(Sync){changes++;}}));
        try {
            Type candidateType=assembly.GetType("CodexRateMonitorNative.CodexExecutable",true);
            object candidate=Activator.CreateInstance(candidateType,new object[]{Application.ExecutablePath,"--fake-server"});
            Call(client,"StartCandidate",candidate);Wait(()=>initialized==1,"initialize");
            int originalPid=((Process)Field(client,"process")).Id;
            int first=Read(client);Wait(()=>snapshots==1,"initial response");
            Check(accounts==1 && completed==1 && (int)Get(lastSnapshot,"ReadRequestId")==first,"identity check precedes successful quota read");
            Check((double)Get(Get(lastSnapshot,"Primary"),"UsedPercent")==40,"Codex bucket takes precedence over legacy bucket");
            Check((bool)Get(lastSnapshot,"ReplaceMissingWindows"),"read replaces missing windows");
            string firstFingerprint=(string)Get(lastAccount,"Fingerprint");
            int beforeHold=notifications;
            Control(client,"hold");int pending=Read(client);Wait(()=>notifications==beforeHold+1,"held quota acknowledged");
            Check(Read(client)==0,"client independently merges duplicate read attempts");
            Call(client,"CancelRateLimitsRequest",pending);
            Control(client,"release");Wait(()=>snapshots==2,"late read barrier");
            Check(completed==1 && (int)Get(lastSnapshot,"ReadRequestId")==0,"timed out reply ignored while valid native push still arrives");
            int beforeAccount=accounts, beforeAccountHold=notifications;
            Control(client,"hold-account");int oldAccountRead=Read(client);Wait(()=>notifications==beforeAccountHold+1,"held account acknowledged");
            Call(client,"CancelRateLimitsRequest",oldAccountRead);
            Control(client,"release-account");Wait(()=>snapshots==3,"late account barrier");
            Check(accounts==beforeAccount && completed==1,"late account reply cannot send quota read");
            Control(client,"normal");Read(client);Wait(()=>snapshots==4,"normal read after timeout");
            Check(((Process)Field(client,"process")).Id==originalPid,"timeouts and reads reuse one helper process");
            Control(client,"switch");Wait(()=>changes==1,"account notification");
            int beforeSwitch=snapshots, beforeSwitchAccounts=accounts, beforeSwitchNotifications=notifications;
            // The switch sends an obsolete rate notification before this read.
            Read(client);Wait(()=>accounts==beforeSwitchAccounts+1 && snapshots==beforeSwitch+1,"new account response");
            Check((string)Get(lastAccount,"Fingerprint")!=firstFingerprint,"account switch yields a different in-memory fingerprint");
            Check(notifications==beforeSwitchNotifications && (double)Get(Get(lastSnapshot,"Primary"),"UsedPercent")==5,"unverified push blocked until new account read completes");
            Control(client,"error");int beforeError=completed;Read(client);Wait(()=>completed==beforeError+1,"network error");
            Check(snapshots==beforeSwitch+1,"failed read cannot publish quota data");
            Control(client,"signed-out");int beforeLogout=completed;Read(client);Wait(()=>completed==beforeLogout+1,"signed out");
            Check(!(bool)Get(lastAccount,"CanReadQuota") && snapshots==beforeSwitch+1,"signed-out account cannot reuse quota data");
        } finally { ((IDisposable)client).Dispose(); }
    }
    private static void ContextTests(Assembly assembly) {
        Type contextType=assembly.GetType("CodexRateMonitorNative.MonitorContext",true);
        Type settingsType=assembly.GetType("CodexRateMonitorNative.MonitorSettings",true);
        Type overlayType=assembly.GetType("CodexRateMonitorNative.OverlayForm",true);
        Type snapshotType=assembly.GetType("CodexRateMonitorNative.RateSnapshot",true);
        Type windowType=assembly.GetType("CodexRateMonitorNative.WindowUsage",true);
        Type accountType=assembly.GetType("CodexRateMonitorNative.AccountState",true);
        object context=FormatterServices.GetUninitializedObject(contextType);
        object settings=Activator.CreateInstance(settingsType,true);
        object olderSettings=Json.Deserialize("{\"RefreshSeconds\":60}",settingsType);
        Check((int)Get(olderSettings,"ForegroundRefreshSeconds")==30,"older settings inherit new foreground default");
        Check(!(bool)Get(settings,"DiagnosticsEnabled") && !(bool)Get(olderSettings,"DiagnosticsEnabled"),"new and older unspecified settings default diagnostics off");
        Set(olderSettings,"ForegroundRefreshSeconds",50);Set(olderSettings,"RefreshSeconds",45);Call(olderSettings,"Normalize");
        object settingsCopy=Call(olderSettings,"Clone");
        Check((int)Get(olderSettings,"ForegroundRefreshSeconds")==45 && (int)Get(settingsCopy,"ForegroundRefreshSeconds")==45,"foreground interval normalizes and survives cloning");
        object overlay=Activator.CreateInstance(overlayType,new[]{settings});
        object client=Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.AppServerClient",true),true);
        var tray=new NotifyIcon();
        try {
            Field(context,"overlay",overlay);Field(context,"settings",settings);Field(context,"trayIcon",tray);Field(context,"appServer",client);
            Field(context,"refreshScheduler",Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.UsageRefreshScheduler",true),true));
            object stabilizer=Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.RateSnapshotStabilizer",true),true);
            Field(context,"snapshotStabilizer",stabilizer);
            object account=Activator.CreateInstance(accountType,true);Set(account,"Fingerprint","first");Set(account,"CanReadQuota",true);
            Call(context,"OnAccountObserved",account,0);
            object snapshot=Activator.CreateInstance(snapshotType,true);object window=Activator.CreateInstance(windowType,true);
            Set(snapshot,"NotificationId",1);
            Set(window,"UsedPercent",80d);Set(window,"ResetsAt",DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());Set(snapshot,"Primary",window);
            Call(context,"OnSnapshotReceived",snapshot,0);
            DateTime firstUpdated=(DateTime)Field(context,"lastSnapshotAt");
            Check(firstUpdated!=DateTime.MinValue && tray.Text.Contains(firstUpdated.ToString("HH:mm:ss")),"tooltip includes successful display time");
            object suspicious=Activator.CreateInstance(snapshotType,true), regressed=Activator.CreateInstance(windowType,true);
            Set(suspicious,"NotificationId",2);
            Set(regressed,"UsedPercent",40d);Set(regressed,"ResetsAt",Get(window,"ResetsAt"));Set(suspicious,"Primary",regressed);
            Call(context,"OnSnapshotReceived",suspicious,0);
            Check(object.ReferenceEquals(Field(context,"lastSnapshot"),snapshot) && firstUpdated.Equals(Field(context,"lastSnapshotAt")),"deferred regression keeps displayed quota and successful time");
            object scheduler=Field(context,"refreshScheduler");
            double monotonic=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            Check(!(bool)Call(scheduler,"ShouldRead",monotonic,30) && (bool)Call(scheduler,"ShouldRead",monotonic+1.1,30),"suspicious data queues one confirmation through scheduler");
            Set(account,"Fingerprint","second");Call(context,"OnAccountObserved",account,0);
            Check(Field(context,"lastSnapshot")==null && Field(context,"lastSnapshotAt").Equals(DateTime.MinValue) &&
                Field(overlay,"snapshot")==null && Field(stabilizer,"accepted")==null,"account switch clears quota, timestamp and stabilizer");
            Set(window,"UsedPercent",5d);Call(context,"OnSnapshotReceived",snapshot,0);
            Check(Field(context,"lastSnapshot")!=null,"new account lower usage is accepted immediately");
            int oldEpoch=(int)Field(context,"accountEpoch");
            Call(context,"OnAccountUpdated",0);
            Check((int)Field(context,"accountEpoch")>oldEpoch && Field(context,"lastSnapshot")==null,"account notification invalidates data and asynchronous credit epoch");
            Call(context,"OnAccountObserved",account,0);
            Call(context,"OnSnapshotReceived",snapshot,0);
            DateTime beforeFailure=(DateTime)Field(context,"lastSnapshotAt");
            Field(context,"refreshFailed",true);Call(context,"UpdateTrayText");
            Check(beforeFailure.Equals(Field(context,"lastSnapshotAt")),"failure retains last successful timestamp");
            Type i18n=assembly.GetType("CodexRateMonitorNative.I18n",true);
            foreach(string lang in new[]{"zh-CN","zh-TW","en"}) {
                i18n.GetMethod("SetLanguage").Invoke(null,new object[]{lang});
                Set(window,"UsedPercent",0d);Set(snapshot,"Secondary",window);Call(context,"UpdateTrayText");
                string failed=(string)i18n.GetMethod("T").Invoke(null,new object[]{"RefreshFailedShort"});
                Check(tray.Text.Length<=63 && tray.Text.Contains(failed) && tray.Text.Contains(((DateTime)Field(context,"lastSnapshotAt")).ToString("HH:mm:ss")),"timestamp and failure fit tray limit in "+lang);
                Field(context,"lastSnapshotMonotonic",monotonic-180);Call(context,"UpdateTrayText");
                string age=(string)i18n.GetMethod("F").Invoke(null,new object[]{"AgeMinutes",new object[]{"3"}});
                string hint=((ToolTip)Field(overlay,"usageHint")).GetToolTip((Control)overlay);
                Check(tray.Text.Length<=63 && tray.Text.Contains(failed) && tray.Text.Contains(age) && hint.Contains(age),"cached failure exposes age without losing tray state in "+lang);
                Field(context,"refreshFailed",false);
                Field(context,"lastUsageState",Enum.Parse(assembly.GetType("CodexRateMonitorNative.DesktopUsageState",true),"Minimized"));
                Call(context,"UpdateTrayText");
                string cached=(string)i18n.GetMethod("T").Invoke(null,new object[]{"CachedShort"});
                Check(!tray.Text.Contains(cached),"three-minute-old minimized data is within its expected cadence in "+lang);
                Field(context,"lastUsageState",Enum.Parse(assembly.GetType("CodexRateMonitorNative.DesktopUsageState",true),"Foreground"));
                Call(context,"UpdateTrayText");
                Check(tray.Text.Length<=63 && tray.Text.Contains(cached) && tray.Text.Contains(age),"foreground data exceeding cadence gets a stale-age hint in "+lang);
                Set(window,"UsedPercent",5d);Call(context,"OnSnapshotReceived",snapshot,0);
                Check(!tray.Text.Contains(cached) && !(bool)Field(context,"refreshFailed"),"successful unchanged data clears stale state in "+lang);
                Field(context,"refreshFailed",true);
            }
            object obsoletePush=Activator.CreateInstance(snapshotType,true);Set(obsoletePush,"NotificationId",3);
            object current=Field(context,"lastSnapshot");Call(context,"OnSnapshotReceived",obsoletePush,1);
            Check(object.ReferenceEquals(current,Field(context,"lastSnapshot")),"obsolete-generation notification cannot replace displayed quota");
            double readStarted=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency-0.025;
            Call(scheduler,"BeginRead",50,0,readStarted,true);Call(context,"OnReadCompleted",50,0,false);
            Check((bool)Field(context,"refreshFailed") && !(bool)Get(scheduler,"InFlight"),"failed completion updates diagnostic timing and cache state");
            object late=Activator.CreateInstance(snapshotType,true);Set(late,"ReadRequestId",999);
            object previouslyDisplayed=Field(context,"lastSnapshot");Call(context,"OnSnapshotReceived",late,0);
            Check(object.ReferenceEquals(previouslyDisplayed,Field(context,"lastSnapshot")),"late UI read cannot replace displayed quota");
            Set(account,"ReadRequestId",999);Set(account,"Fingerprint","obsolete");Call(context,"OnAccountObserved",account,0);
            Check(object.ReferenceEquals(previouslyDisplayed,Field(context,"lastSnapshot")) && (string)Field(context,"accountFingerprint")=="second","late UI account reply cannot clear current data");
            Set(account,"ReadRequestId",0);
            Set(account,"CanReadQuota",false);Set(account,"StatusKey","NotSignedIn");Call(context,"OnAccountObserved",account,0);
            Check((bool)Field(context,"authDead") && (double)Field(context,"authProbeNotBefore")>monotonic+299 && Field(context,"lastSnapshot")==null,"signed-out context clears display and enforces slow recovery probes");
        } finally { tray.Dispose();((IDisposable)overlay).Dispose();((IDisposable)client).Dispose(); }
    }
    private static void DiagnosticTests(Assembly assembly) {
        string history=string.Join("\n",Directory.GetFiles(logDirectory,"usage-*.log").Select(File.ReadAllText));
        Check(history.Contains("rate-notification-received") && history.Contains("reason=account-unverified") &&
            history.Contains("reason=invalid-payload"),"received notifications and pre-account or malformed rejections are observable");
        Check(history.Contains("rate-notification-displayed") && history.Contains("rate-notification-deferred") &&
            history.Contains("reason=obsolete-generation"),"validated, deferred and obsolete UI notifications have distinct dispositions");
        Check(history.Contains("app-server-response-ignored") && history.Contains("category=network") &&
            !history.Contains("network unavailable") && !history.Contains("@example.test"),"late replies and safe failure categories are recorded without private error or account text");
        Check(history.Contains("rate-refresh-completed id=50") && history.Contains("reason=manual state=failure elapsed_ms="),"completed cycles include origin, result and elapsed time");
        Type clientType=assembly.GetType("CodexRateMonitorNative.AppServerClient",true);
        MethodInfo describe=clientType.GetMethod("DescribeError",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(var pair in new[]{new object[]{401,"auth"},new object[]{429,"rate-limited"},new object[]{504,"timeout"},new object[]{503,"server"},new object[]{-32602,"protocol"}}) {
            string safe=(string)describe.Invoke(null,new object[]{new Dictionary<string,object>{{"code",int.Parse(pair[0].ToString())},{"message","private-sentinel"}}});
            Check(safe.Contains("category="+pair[1]) && !safe.Contains("private-sentinel"),"numeric error classification "+pair[1]);
        }
        string unknown=(string)describe.Invoke(null,new object[]{new Dictionary<string,object>{{"code","private-sentinel"},{"message","private-sentinel"},{"data",new Dictionary<string,object>{{"token","private-sentinel"}}}}});
        Check(unknown.Contains("category=unknown") && unknown.Contains("code=unavailable") && !unknown.Contains("private-sentinel"),"unknown errors cannot leak arbitrary codes, data or messages");
        Type storeType=assembly.GetType("CodexRateMonitorNative.DiagnosticLogStore",true);
        DateTime now=DateTime.UtcNow;
        string retention=Path.Combine(logDirectory,"retention");Directory.CreateDirectory(retention);
        string expired=Path.Combine(retention,"usage-old.log"),recent=Path.Combine(retention,"usage-recent.log"),unrelated=Path.Combine(retention,"keep.txt");
        File.WriteAllText(expired,"old");File.SetLastWriteTimeUtc(expired,now.AddDays(-8));
        File.WriteAllText(recent,"recent");File.SetLastWriteTimeUtc(recent,now.AddDays(-2));File.WriteAllText(unrelated,"keep");
        object store=Activator.CreateInstance(storeType,new object[]{retention,4096L,512L});Call(store,"Cleanup",now,7,0L);
        Check(!File.Exists(expired) && File.Exists(recent) && File.Exists(unrelated),"retention removes only expired usage log files");
        string pressure=Path.Combine(logDirectory,"pressure");
        store=Activator.CreateInstance(storeType,new object[]{pressure,4096L,512L});
        for(int i=0;i<120;i++) if(!(bool)Call(store,"TryAppend",now.AddMilliseconds(i),7,"event","item="+i+" "+new string('x',150))) throw new Exception("rotation write failed");
        FileInfo[] files=new DirectoryInfo(pressure).GetFiles("usage-*.log");
        Check(files.Length>1 && files.Sum(f=>f.Length)<=4096 && files.All(f=>f.Length<=512),"repeated writes rotate files and obey the total disk budget");
        Check(files.Any(f=>File.ReadAllText(f.FullName).Contains("item=119 ")),"capacity cleanup preserves the latest diagnostic entry");
        string lockedDirectory=Path.Combine(logDirectory,"locked");Directory.CreateDirectory(lockedDirectory);
        string lockedPath=Path.Combine(lockedDirectory,"usage-locked.log");File.WriteAllBytes(lockedPath,new byte[4096]);
        store=Activator.CreateInstance(storeType,new object[]{lockedDirectory,4096L,512L});
        using(var held=new FileStream(lockedPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) {
            Check(!(bool)Call(store,"TryAppend",now,7,"event","blocked") && Directory.GetFiles(lockedDirectory).Length==1,"an undeletable full log directory stops additional writes");
        }
        string malformed=Path.Combine(logDirectory,"entries");store=Activator.CreateInstance(storeType,new object[]{malformed,4096L,4096L});
        Call(store,"TryAppend",now,7,"event\r\nextra","a\n\tb"+new string('x',4000));
        string entry=File.ReadAllText(Directory.GetFiles(malformed,"usage-*.log")[0]);
        Check(entry.Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries).Length==1 && entry.Length<1200,"diagnostic entries sanitize line breaks and cap field length");
        string disabledOld=Path.Combine(logDirectory,"usage-expired.log");File.WriteAllText(disabledOld,"old");File.SetLastWriteTimeUtc(disabledOld,now.AddDays(-8));
        loggerType.GetMethod("Configure").Invoke(null,new object[]{false,7});
        Check(!File.Exists(disabledOld),"disabling diagnostics still performs retention cleanup");
        long before=new DirectoryInfo(logDirectory).GetFiles("usage-*.log").Sum(f=>f.Length);
        loggerType.GetMethod("Write").Invoke(null,new object[]{"disabled-event","should-not-write"});
        Check(new DirectoryInfo(logDirectory).GetFiles("usage-*.log").Sum(f=>f.Length)==before,"disabled diagnostics generate no new entries");
        Check((long)loggerType.GetField("MaximumBytes",BindingFlags.Static|BindingFlags.NonPublic).GetRawConstantValue()==20L*1024*1024,"production diagnostics have a twenty-MiB budget");
    }
    private static void Output(object message) { Console.WriteLine(Json.Serialize(message)); }
    private static object Usage(int used) { return new Dictionary<string,object>{{"primary",new Dictionary<string,object>{{"usedPercent",used},{"windowDurationMins",300},{"resetsAt",1792000000}}}}; }
    private static void Quota(int id, int used) { Output(new Dictionary<string,object>{{"id",id},{"result",new Dictionary<string,object>{{"rateLimits",Usage(60)},{"rateLimitsByLimitId",new Dictionary<string,object>{{"codex",Usage(used)},{"other",Usage(99)}}}}}}); }
    private static void Account(int id,string who) { Output(new Dictionary<string,object>{{"id",id},{"result",new Dictionary<string,object>{{"requiresOpenaiAuth",true},{"account",who=="signed-out"?null:new Dictionary<string,object>{{"type","chatgpt"},{"email",who+"@example.test"},{"planType","plus"}}}}}}); }
    private static void Push() { Output(new Dictionary<string,object>{{"method","account/rateLimits/updated"},{"params",new Dictionary<string,object>{{"rateLimits",Usage(41)}}}}); }
    private static void FakeServer() {
        string mode="normal",who="first",line;int held=0,heldAccount=0;
        while((line=Console.ReadLine())!=null) {
            var message=Json.DeserializeObject(line) as Dictionary<string,object>;
            string method=(string)message["method"];int id=message.ContainsKey("id")?Convert.ToInt32(message["id"]):0;
            if(method=="initialize") Output(new Dictionary<string,object>{{"id",id},{"result",new Dictionary<string,object>()}});
            else if(method=="account/read") {
                var parameters=(Dictionary<string,object>)message["params"];
                if((bool)parameters["refreshToken"]) throw new Exception("Unexpected proactive refresh");
                if(mode=="hold-account") {heldAccount=id;Output(new Dictionary<string,object>{{"method","account/rateLimits/updated"},{"params",new Dictionary<string,object>()}});}else Account(id,who);
            } else if(method=="account/rateLimits/read") {
                if(mode=="hold") {held=id;Output(new Dictionary<string,object>{{"method","account/rateLimits/updated"},{"params",new Dictionary<string,object>()}});}
                else if(mode=="error") Output(new Dictionary<string,object>{{"id",id},{"error",new Dictionary<string,object>{{"message","network unavailable"}}}});
                else Quota(id,who=="first"?40:5);
            } else if(method=="test/control") {
                string command=(string)message["params"];
                if(command=="release") { Quota(held,40);held=0;mode="normal";Push(); }
                else if(command=="release-account") { Account(heldAccount,who);heldAccount=0;mode="normal";Push(); }
                else if(command=="switch") {who="second";mode="normal";Output(new Dictionary<string,object>{{"method","account/updated"},{"params",new Dictionary<string,object>{{"authMode","chatgpt"},{"planType","plus"}}}});Push();}
                else if(command=="signed-out") {who="signed-out";mode="normal";}
                else mode=command;
            }
        }
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }
& $csc /nologo /target:exe /out:"$testExe" /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $testSource (Join-Path $repoRoot 'src\UsageRefreshScheduler.cs')
if ($LASTEXITCODE -ne 0) { throw 'Usage refresh test compilation failed.' }
& $testExe $monitorExe
if ($LASTEXITCODE -ne 0) { throw 'Usage refresh test failed.' }
