using System;
using System.IO;
using System.Text.Json;

namespace search
{
    internal sealed class LinuxServiceSettings { public bool ShowAllFiles { get; set; } = true; }

    /// <summary>
    /// Whether the app asks the Linux file system service for every file (administrators
    /// only - the service decides) or only for the files the user can access
    /// </summary>
    internal static class LinuxServiceSettingsStore
    {
        static readonly string Path = UserDataPaths.For("linux-service-settings.json");
        static bool? showAllFiles;

        public static bool ShowAllFiles
        {
            get
            {
                if (showAllFiles is { } cached) return cached;
                try { showAllFiles = JsonSerializer.Deserialize<LinuxServiceSettings>(File.ReadAllText(Path))?.ShowAllFiles ?? true; }
                catch { showAllFiles = true; }
                return showAllFiles.Value;
            }
        }

        public static void Save(bool showAll)
        {
            showAllFiles = showAll;
            try { File.WriteAllText(Path, JsonSerializer.Serialize(new LinuxServiceSettings { ShowAllFiles = showAll })); }
            catch (Exception e) { $"saving Linux service settings failed: {e.Message}".Debug(); }
        }
    }
}
