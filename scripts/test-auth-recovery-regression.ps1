[CmdletBinding()]
param([string]$MonitorPath = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$monitorExe = if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
    Join-Path $repoRoot 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
} else { [IO.Path]::GetFullPath($MonitorPath) }
if (-not (Test-Path -LiteralPath $monitorExe)) {
    throw 'Build the monitor with scripts/build.ps1 before running this test.'
}

$testDirectory = Join-Path $repoRoot '.build\auth-recovery-test'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$testSource = Join-Path $testDirectory 'AuthRecoverySmoke.cs'
$testExe = Join-Path $testDirectory 'AuthRecoverySmoke.exe'
@'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;

internal static class AuthRecoverySmoke
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static object capturedSnapshot;

    private static void CaptureSnapshot<T>(T snapshot, int generation)
    {
        capturedSnapshot = snapshot;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("Failed: " + name);
        Console.WriteLine("PASS " + name);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            Environment.ExitCode = 1;
        }
    }

    private static void Run(string[] args)
    {
        Assembly assembly = Assembly.LoadFrom(args[0]);
        Type clientType = assembly.GetType("CodexRateMonitorNative.AppServerClient", true);
        MethodInfo isRevoked = clientType.GetMethod("IsAuthRevokedError", PrivateStatic);
        Func<Dictionary<string, object>, bool> match = delegate(Dictionary<string, object> error)
        {
            return (bool)isRevoked.Invoke(null, new object[] { error });
        };
        Check(match(new Dictionary<string, object> { { "message", "401 Unauthorized: token_revoked" } }),
            "message identifies revoked token");
        Check(match(new Dictionary<string, object> { { "code", "token_revoked" } }),
            "structured code identifies revoked token");
        Check(match(new Dictionary<string, object> {
            { "data", new Dictionary<string, object> { { "code", "token_revoked" } } }
        }), "nested code identifies revoked token");
        Check(!match(new Dictionary<string, object> { { "message", "401 Unauthorized" } }),
            "unrelated 401 is ignored");

        object client = Activator.CreateInstance(clientType, true);
        int revokedCount = 0;
        int latestIssued = 0;
        Action<int, int> onRevoked = delegate(int generation, int requestId)
        {
            revokedCount++;
            latestIssued = requestId;
        };
        clientType.GetEvent("AuthRevoked").AddEventHandler(client, onRevoked);
        HashSet<int> requests = (HashSet<int>)clientType.GetField(
            "rateLimitRequests", PrivateInstance).GetValue(client);
        MethodInfo process = clientType.GetMethod("ProcessMessage", PrivateInstance);
        clientType.GetField("requestId", PrivateInstance).SetValue(client, 41);
        requests.Add(41);
        const string revokedResponse = "{\"id\":41,\"error\":{\"message\":\"401 Unauthorized: token_revoked\"}}";
        process.Invoke(client, new object[] { revokedResponse, 1 });
        Check(requests.Contains(41) && revokedCount == 0,
            "old process response is ignored");
        process.Invoke(client, new object[] { revokedResponse, 0 });
        Check(!requests.Contains(41) && revokedCount == 1,
            "current process emits one auth event");
        Check(latestIssued >= 41, "auth event carries issued request boundary");

        Type snapshotType = assembly.GetType("CodexRateMonitorNative.RateSnapshot", true);
        EventInfo snapshotEvent = clientType.GetEvent("SnapshotReceived");
        MethodInfo capture = typeof(AuthRecoverySmoke).GetMethod(
            "CaptureSnapshot", PrivateStatic).MakeGenericMethod(snapshotType);
        snapshotEvent.AddEventHandler(client,
            Delegate.CreateDelegate(snapshotEvent.EventHandlerType, capture));
        clientType.GetField("requestId", PrivateInstance).SetValue(client, 42);
        requests.Add(42);
        const string successResponse = "{\"id\":42,\"result\":{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300,\"resetsAt\":1790000000}}}}";
        process.Invoke(client, new object[] { successResponse, 0 });
        Check(capturedSnapshot != null &&
            (int)snapshotType.GetProperty("ReadRequestId").GetValue(capturedSnapshot, null) == 42 &&
            (bool)snapshotType.GetProperty("ReplaceMissingWindows").GetValue(capturedSnapshot, null),
            "successful read carries request id for recovery filtering");

        Type settingsType = assembly.GetType("CodexRateMonitorNative.MonitorSettings", true);
        Type overlayType = assembly.GetType("CodexRateMonitorNative.OverlayForm", true);
        Type windowType = assembly.GetType("CodexRateMonitorNative.WindowUsage", true);
        object settings = Activator.CreateInstance(settingsType, true);
        object overlay = Activator.CreateInstance(overlayType, new object[] { settings });
        try
        {
            object first = Activator.CreateInstance(snapshotType, true);
            snapshotType.GetProperty("Primary").SetValue(first,
                Activator.CreateInstance(windowType, true), null);
            overlayType.GetMethod("SetSnapshot").Invoke(overlay, new object[] { first });
            overlayType.GetMethod("ClearSnapshot").Invoke(overlay, new object[] { "Connecting" });
            Check(overlayType.GetField("snapshot", PrivateInstance).GetValue(overlay) == null,
                "revoked state removes visible snapshot");
            object next = Activator.CreateInstance(snapshotType, true);
            snapshotType.GetProperty("Secondary").SetValue(next,
                Activator.CreateInstance(windowType, true), null);
            overlayType.GetMethod("SetSnapshot").Invoke(overlay, new object[] { next });
            Check(snapshotType.GetProperty("Primary").GetValue(next, null) == null,
                "recovery snapshot does not merge old window");

            // Skip MonitorContext's live timers and background clients. Exercise
            // its real snapshot handler with a hidden overlay and inert client.
            Type contextType = assembly.GetType("CodexRateMonitorNative.MonitorContext", true);
            object context = FormatterServices.GetUninitializedObject(contextType);
            object tray = Activator.CreateInstance(overlayType.BaseType.Assembly.GetType(
                "System.Windows.Forms.NotifyIcon", true));
            try
            {
                contextType.GetField("overlay", PrivateInstance).SetValue(context, overlay);
                contextType.GetField("trayIcon", PrivateInstance).SetValue(context, tray);
                contextType.GetField("settings", PrivateInstance).SetValue(context, settings);
                contextType.GetField("appServer", PrivateInstance).SetValue(context, client);
                contextType.GetField("refreshScheduler", PrivateInstance).SetValue(context,
                    Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.UsageRefreshScheduler", true), true));
                contextType.GetField("snapshotStabilizer", PrivateInstance).SetValue(context,
                    Activator.CreateInstance(assembly.GetType("CodexRateMonitorNative.RateSnapshotStabilizer", true), true));
                contextType.GetField("lastSnapshot", PrivateInstance).SetValue(context, first);
                contextType.GetField("identicalReads", PrivateInstance).SetValue(context, 9);
                object changed = Activator.CreateInstance(snapshotType, true);
                object usage = Activator.CreateInstance(windowType, true);
                windowType.GetProperty("UsedPercent").SetValue(usage, 12d, null);
                snapshotType.GetProperty("Primary").SetValue(changed, usage, null);
                MethodInfo receive = contextType.GetMethod("OnSnapshotReceived", PrivateInstance);
                receive.Invoke(context, new object[] { changed, 0 });
                Check((int)contextType.GetField("identicalReads", PrivateInstance).GetValue(context) == 0,
                    "changed usage clears pinned-read count before storing snapshot");
                object repeated = Activator.CreateInstance(snapshotType, true);
                snapshotType.GetProperty("Primary").SetValue(repeated, usage, null);
                receive.Invoke(context, new object[] { repeated, 0 });
                Check((int)contextType.GetField("identicalReads", PrivateInstance).GetValue(context) == 1,
                    "unchanged usage increments pinned-read count");
            }
            finally { ((IDisposable)tray).Dispose(); }
        }
        finally
        {
            ((IDisposable)overlay).Dispose();
            ((IDisposable)client).Dispose();
        }
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding UTF8

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw '.NET Framework C# compiler was not found.' }
& $csc /nologo /target:exe /out:"$testExe" $testSource
if ($LASTEXITCODE -ne 0) { throw 'Auth recovery smoke test compilation failed.' }
& $testExe $monitorExe
if ($LASTEXITCODE -ne 0) { throw 'Auth recovery smoke test failed.' }
