using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace search
{
    /// <summary>
    /// Owns disk-bounded diagnostics and process-scoped temporary files.
    /// All maintenance is best effort: inability to clean diagnostics must never
    /// prevent the application from starting or shutting down.
    /// </summary>
    internal static class StorageMaintenance
    {
        internal const long MaxLogBytes = 2 * 1024 * 1024;
        internal const int LogBackupCount = 3;
        internal const long MaxHealthLogBytes = 512 * 1024;
        internal const int HealthLogBackupCount = 2;
        internal static readonly TimeSpan ClipboardRetention = TimeSpan.FromDays(1);

        private static readonly SemaphoreSlim LogLock = new SemaphoreSlim(1, 1);
        private static readonly string[] KnownLogs = { "search.run.log", "log.txt", "health.log" };

        internal static void RunStartupCleanup(string tempRoot, string currentTempFolder, DateTime utcNow)
        {
            CleanupTempFolders(tempRoot, currentTempFolder, utcNow);
            foreach (var log in KnownLogs)
            {
                var path = UserDataPaths.For(log);
                if (log == "health.log") TryRotateLog(path, 0, MaxHealthLogBytes, HealthLogBackupCount);
                else TryRotateLog(path);
            }
        }

        internal static void CleanupTempFolders(string tempRoot, string currentTempFolder, DateTime utcNow)
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(tempRoot, "search.*"))
                {
                    if (PathsEqual(directory, currentTempFolder) || IsOwnerStillRunning(directory))
                        continue;

                    var clipboard = Path.GetFileName(directory)
                        .StartsWith("search.clipboard.", StringComparison.OrdinalIgnoreCase);
                    if (clipboard && GetDirectoryTimestampUtc(directory) > utcNow - ClipboardRetention)
                        continue;

                    TryDeleteDirectory(directory);
                }
            }
            catch { }
        }

        internal static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch { }
        }

        internal static void CleanupClipboardMaterializations(string clipboardRoot, IEnumerable<string> retainedPaths)
        {
            try
            {
                if (!Directory.Exists(clipboardRoot)) return;
                var retained = (retainedPaths ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(TryGetFullPath)
                    .Where(path => path != null)
                    .ToArray();

                foreach (var directory in Directory.EnumerateDirectories(clipboardRoot))
                {
                    var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (!retained.Any(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                        TryDeleteDirectory(directory);
                }

                if (!Directory.EnumerateFileSystemEntries(clipboardRoot).Any())
                    TryDeleteDirectory(clipboardRoot);
            }
            catch { }
        }

        internal static void AppendLog(string fileName, string text)
            => AppendLog(fileName, text, MaxLogBytes, LogBackupCount);

        internal static void AppendHealthLog(string text)
            => AppendLog("health.log", text, MaxHealthLogBytes, HealthLogBackupCount);

        static void AppendLog(string fileName, string text, long maxBytes, int backupCount)
        {
            LogLock.Wait();
            try
            {
                var path = UserDataPaths.For(fileName);
                TryRotateLog(path, Encoding.UTF8.GetByteCount(text), maxBytes, backupCount);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch { }
            finally
            {
                LogLock.Release();
            }
        }

        internal static async Task AppendLogAsync(string fileName, string text)
        {
            await LogLock.WaitAsync();
            try
            {
                var path = UserDataPaths.For(fileName);
                TryRotateLog(path, Encoding.UTF8.GetByteCount(text));
                await File.AppendAllTextAsync(path, text, Encoding.UTF8);
            }
            catch { }
            finally
            {
                LogLock.Release();
            }
        }

        internal static void TryRotateLog(string path, int incomingBytes = 0)
            => TryRotateLog(path, incomingBytes, MaxLogBytes, LogBackupCount);

        internal static void TryRotateLog(string path, int incomingBytes, long maxBytes, int backupCount)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length + incomingBytes <= maxBytes) return;

                var oldest = path + "." + backupCount;
                if (File.Exists(oldest)) File.Delete(oldest);
                for (var i = backupCount - 1; i >= 1; i--)
                {
                    var source = path + "." + i;
                    if (File.Exists(source)) File.Move(source, path + "." + (i + 1));
                }
                File.Move(path, path + ".1");
            }
            catch { }
        }

        private static bool IsOwnerStillRunning(string directory)
        {
            try
            {
                var name = Path.GetFileName(directory);
                var marker = name.StartsWith("search.clipboard.", StringComparison.OrdinalIgnoreCase)
                    ? name.Substring("search.clipboard.".Length)
                    : name.Substring("search.".Length);
                var parts = marker.Split('.');
                if (!int.TryParse(parts[0], out var processId)) return false;

                using var process = Process.GetProcessById(processId);
                if (parts.Length < 2 || !long.TryParse(parts[1], out var startTicks))
                    return true; // Compatibility with folders created by older versions.
                return process.StartTime.ToUniversalTime().Ticks == startTicks;
            }
            catch
            {
                return false;
            }
        }

        private static DateTime GetDirectoryTimestampUtc(string directory)
        {
            try { return Directory.GetLastWriteTimeUtc(directory); }
            catch { return DateTime.MinValue; }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string TryGetFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return null; }
        }
    }
}
