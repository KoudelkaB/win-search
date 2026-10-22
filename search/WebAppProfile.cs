using System;
using System.IO;

namespace search
{
    /// <summary>
    /// WPF and Avalonia can use different browser process settings (including DPI awareness).
    /// WebView2 rejects incompatible hosts sharing a user-data folder with ERROR_INVALID_STATE.
    /// Give this host a persistent profile and migrate the old localStorage only while idle.
    /// </summary>
    internal static class WebAppProfile
    {
        internal const string FolderName = "WebView2.Avalonia";

        public static string Prepare(string userDataRoot)
        {
            userDataRoot = Path.GetFullPath(userDataRoot);
            var target = Path.Combine(userDataRoot, FolderName);
            if (Directory.Exists(target)) return target;
            var source = Path.Combine(userDataRoot, "WebView2", "EBWebView", "Default", "Local Storage", "leveldb");
            var staging = target + ".migration-" + Guid.NewGuid().ToString("N");
            try
            {
                if (OperatingSystem.IsWindows() && Directory.Exists(source))
                {
                    //Chromium holds an exclusive byte-range lock while its LevelDB is open.
                    //Never copy an active database, or replace an existing Avalonia profile.
                    using var lease = new FileStream(Path.Combine(source, "LOCK"), FileMode.Open,
                        FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                    lease.Lock(0, 1);
                    var destination = Path.Combine(staging, "EBWebView", "Default", "Local Storage", "leveldb");
                    Directory.CreateDirectory(destination);
                    foreach (var file in Directory.EnumerateFiles(source))
                        if (!string.Equals(Path.GetFileName(file), "LOCK", StringComparison.OrdinalIgnoreCase))
                            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
                    Directory.Move(staging, target);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StorageMaintenance.AppendDiagnostic($"Web app profile: old localStorage was not copied ({error.Message}); original data retained.");
            }
            finally
            {
                //Only this call's uniquely named staging directory can be removed.
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Directory.CreateDirectory(target);
            return target;
        }
    }
}
