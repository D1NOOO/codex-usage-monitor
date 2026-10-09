using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexRateMonitorNative
{
    internal static class DiagnosticLog
    {
        internal const long MaximumBytes = 20 * 1024 * 1024;
        private static readonly object Sync = new object();
        private static bool enabled;
        private static int retentionDays = 7;
        private static DateTime nextCleanupUtc = DateTime.MinValue;
        private static DiagnosticLogStore store = new DiagnosticLogStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRateMonitor", "logs"), MaximumBytes, 2 * 1024 * 1024);

        public static string DirectoryPath { get { return store.DirectoryPath; } }
        public static void Configure(bool isEnabled, int days)
        {
            lock (Sync)
            {
                enabled = isEnabled;
                retentionDays = Math.Max(1, Math.Min(30, days));
                nextCleanupUtc = DateTime.MinValue;
            }
            CleanupIfDue();
        }

        public static void Write(string eventName, string details)
        {
            lock (Sync)
            {
                if (!enabled) return;
                try { store.TryAppend(DateTime.UtcNow, retentionDays, eventName, details); }
                catch { /* Diagnostics must not interrupt monitoring. */ }
            }
        }

        public static void CleanupIfDue()
        {
            lock (Sync)
            {
                if (DateTime.UtcNow < nextCleanupUtc) return;
                nextCleanupUtc = DateTime.UtcNow.AddHours(1);
                try { store.Cleanup(DateTime.UtcNow, retentionDays, 0); }
                catch { }
            }
        }
    }

    // Kept separate so retention, rotation and capacity can be exercised against
    // a temporary directory without touching a user's diagnostic history.
    internal sealed class DiagnosticLogStore
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);
        private readonly long maximumBytes;
        private readonly long maximumFileBytes;
        private long knownBytes = -1;
        private string activePath;
        private string activeDay;
        private long activeBytes;
        public string DirectoryPath { get; private set; }

        public DiagnosticLogStore(string directory, long budget, long fileBudget)
        {
            DirectoryPath = Path.GetFullPath(directory);
            maximumBytes = budget;
            maximumFileBytes = Math.Min(budget, fileBudget);
        }

        public bool TryAppend(DateTime utcNow, int days, string eventName, string details)
        {
            string line = utcNow.ToString("o", CultureInfo.InvariantCulture) + " " + Clean(eventName, 64);
            if (!string.IsNullOrWhiteSpace(details)) line += " " + Clean(details, 1024);
            byte[] entry = Utf8.GetBytes(line + Environment.NewLine);
            if (entry.Length > maximumFileBytes) return false;
            Directory.CreateDirectory(DirectoryPath);
            if (knownBytes < 0 || knownBytes + entry.Length > maximumBytes)
                Cleanup(utcNow, days, entry.Length);
            // If files cannot be deleted, stop writing rather than grow past
            // the budget. Existing files remain available for diagnosis.
            if (knownBytes + entry.Length > maximumBytes) return false;
            SelectActiveFile(utcNow, entry.Length);
            File.AppendAllText(activePath, Utf8.GetString(entry), Utf8);
            activeBytes += entry.Length;
            knownBytes += entry.Length;
            return true;
        }

        private void SelectActiveFile(DateTime utcNow, int entryBytes)
        {
            string day = utcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (activeDay == day && activePath != null && File.Exists(activePath) &&
                activeBytes + entryBytes <= maximumFileBytes) return;
            string prefix = "usage-" + day;
            FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles(prefix + "*.log");
            FileInfo latest = files.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest != null && latest.Length + entryBytes <= maximumFileBytes)
            {
                activePath = latest.FullName;
                activeBytes = latest.Length;
            }
            else
            {
                int suffix = 0;
                string candidate;
                do
                {
                    candidate = Path.Combine(DirectoryPath, prefix +
                        (suffix == 0 ? "" : "-" + suffix.ToString("D3", CultureInfo.InvariantCulture)) + ".log");
                    suffix++;
                } while (File.Exists(candidate));
                activePath = candidate;
                activeBytes = 0;
            }
            activeDay = day;
        }

        public void Cleanup(DateTime utcNow, int days, long reserveBytes)
        {
            knownBytes = 0;
            if (!Directory.Exists(DirectoryPath)) return;
            DateTime cutoff = utcNow.Date.AddDays(1 - Math.Max(1, Math.Min(30, days)));
            FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles("usage-*.log")
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            foreach (FileInfo file in files)
            {
                if (file.LastWriteTimeUtc < cutoff && TryDelete(file)) continue;
                knownBytes += file.Length;
            }
            foreach (FileInfo file in files)
            {
                if (knownBytes + reserveBytes <= maximumBytes) break;
                if (!File.Exists(file.FullName)) continue;
                long bytes = file.Length;
                if (TryDelete(file)) knownBytes -= bytes;
            }
            if (activePath != null)
            {
                var active = new FileInfo(activePath);
                if (!active.Exists) activePath = null;
                else activeBytes = active.Length;
            }
        }

        private bool TryDelete(FileInfo file)
        {
            try { File.Delete(file.FullName); return true; }
            catch { return false; }
        }

        private static string Clean(string value, int limit)
        {
            string result = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return result.Length <= limit ? result : result.Substring(0, limit);
        }
    }
}
