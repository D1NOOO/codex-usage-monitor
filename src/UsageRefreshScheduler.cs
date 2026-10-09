using System;

namespace CodexRateMonitorNative
{
    internal enum DesktopUsageState { Foreground, Background, Minimized }

    // All deadlines use a monotonic clock. Window events, retries, and periodic
    // reads share one pending request instead of creating separate timers.
    internal sealed class UsageRefreshScheduler
    {
        internal const double RequestTimeoutSeconds = 30;
        private double lastReadAt = double.NegativeInfinity;
        private double startedAt;
        private double triggerAt = double.PositiveInfinity;
        private double retryAt = double.NegativeInfinity;
        private bool observedWindow;
        private DesktopUsageState windowState;
        private int requestId;
        private int generation;
        private string triggerReason = "event";
        private readonly ResetWindow primaryReset = new ResetWindow();
        private readonly ResetWindow secondaryReset = new ResetWindow();

        public bool InFlight { get; private set; }
        public int PendingRequestId { get { return requestId; } }
        public int PendingGeneration { get { return generation; } }
        public int FailureCount { get; private set; }
        public string PendingReason { get; private set; }

        public double PendingAge(double now) { return Math.Max(0, now - startedAt); }
        public double RetryDelay(double now) { return Math.Max(0, retryAt - now); }

        public void ObserveWindow(DesktopUsageState state, double now)
        {
            if (observedWindow && state != windowState &&
                (state == DesktopUsageState.Foreground ||
                 windowState == DesktopUsageState.Minimized && state != DesktopUsageState.Minimized))
                Trigger(now, 1, state == DesktopUsageState.Foreground ? "focus" : "restore");
            windowState = state;
            observedWindow = true;
        }

        public void Trigger(double now, double delaySeconds, string reason = "event")
        {
            triggerAt = now + Math.Max(0, delaySeconds);
            triggerReason = reason;
        }

        public void ObserveResets(long? primary, long? secondary, DateTimeOffset utcNow, double now,
            double? primaryUsed = null, double? secondaryUsed = null)
        {
            double unixNow = (utcNow - DateTimeOffset.FromUnixTimeSeconds(0)).TotalSeconds;
            primaryReset.Observe(primary, primaryUsed, unixNow, now);
            secondaryReset.Observe(secondary, secondaryUsed, unixNow, now);
        }

        public void ClearResetTargets()
        {
            primaryReset.Clear();
            secondaryReset.Clear();
        }

        private bool ResetDue(double now)
        {
            return primaryReset.IsDue(now) || secondaryReset.IsDue(now);
        }

        private string ReadReason(double now, bool manual)
        {
            if (manual) return "manual";
            if (FailureCount > 0) return "retry";
            if (ResetDue(now)) return "reset_due";
            return double.IsPositiveInfinity(triggerAt) ? "periodic" : triggerReason;
        }

        public bool ShouldRead(double now, int intervalSeconds)
        {
            if (InFlight || now < retryAt)
                return false;
            // A due retry has its own deadline, independent of window cadence
            // and queued event debounce. Successful reads restore normal polling.
            if (FailureCount > 0) return true;
            if (ResetDue(now)) return true;
            // Let focus/restore events settle even if the periodic read is due.
            if (!double.IsPositiveInfinity(triggerAt))
                return now >= triggerAt;
            return now - lastReadAt >= intervalSeconds;
        }

        public bool BeginRead(int id, int currentGeneration, double now, bool manual)
        {
            if (id <= 0 || InFlight || !manual && now < retryAt)
                return false;
            InFlight = true;
            requestId = id;
            generation = currentGeneration;
            startedAt = now;
            PendingReason = ReadReason(now, manual);
            lastReadAt = now;
            if (triggerAt <= now)
                triggerAt = double.PositiveInfinity;
            return true;
        }

        public bool CompleteRead(int id, int currentGeneration, double now, bool success)
        {
            if (!InFlight || requestId != id || generation != currentGeneration)
                return false;
            InFlight = false;
            requestId = 0;
            // Any completed read can cover a due reset; retain at most one
            // confirmation if the following snapshot still has the old reset.
            primaryReset.Complete(now);
            secondaryReset.Complete(now);
            if (success)
            {
                FailureCount = 0;
                retryAt = double.NegativeInfinity;
                lastReadAt = now;
                // A response arriving after the queued event covers that event.
                if (triggerAt <= now)
                    triggerAt = double.PositiveInfinity;
            }
            else
            {
                FailureCount = Math.Min(10, FailureCount + 1);
                retryAt = now + Math.Min(300, 30 * (1 << Math.Min(4, FailureCount - 1)));
            }
            return true;
        }

        public bool ExpireRead(double now, out int id, out int currentGeneration)
        {
            id = requestId;
            currentGeneration = generation;
            if (!InFlight || now - startedAt < RequestTimeoutSeconds)
                return false;
            CompleteRead(id, currentGeneration, now, false);
            return true;
        }

        public void Reset(double now, double delaySeconds, string reason = "account")
        {
            InFlight = false;
            requestId = 0;
            FailureCount = 0;
            lastReadAt = double.NegativeInfinity;
            retryAt = double.NegativeInfinity;
            ClearResetTargets();
            Trigger(now, delaySeconds, reason);
        }

        private sealed class ResetWindow
        {
            private long? target;
            private double dueAt = double.PositiveInfinity;
            private int attempts;
            private double? lastUsed;

            public bool IsDue(double now) { return now >= dueAt; }
            public void Clear() { target = null; lastUsed = null; dueAt = double.PositiveInfinity; attempts = 0; }
            public void Observe(long? reset, double? used, double unixNow, double now)
            {
                if (target == reset)
                {
                    // A confirmed usage drop after the reset also covers it,
                    // even if the server has not advanced the reset timestamp.
                    if (target.HasValue && unixNow >= target.Value && used.HasValue &&
                        lastUsed.HasValue && used.Value + 0.5 < lastUsed.Value)
                        dueAt = double.PositiveInfinity;
                    lastUsed = used;
                    return;
                }
                target = reset;
                lastUsed = used;
                attempts = 0;
                dueAt = reset.HasValue ? now + Math.Max(0, reset.Value - unixNow + 2) : double.PositiveInfinity;
            }
            public void Complete(double now)
            {
                if (!IsDue(now)) return;
                attempts++;
                dueAt = attempts == 1 ? now + 10 : double.PositiveInfinity;
            }
        }

        public static int Interval(DesktopUsageState state, int foreground, int background, int minimized)
        {
            return state == DesktopUsageState.Foreground ? foreground :
                state == DesktopUsageState.Background ? background : minimized;
        }
    }
}
