using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace search
{
    /// <summary>
    /// Which drives get indexed. Drives without an explicit user choice default to NTFS
    /// only - other file systems (network mounts, FUSE drives, FAT sticks) are walked
    /// file by file, which over the wire can dominate the whole load.
    /// </summary>
    public sealed class DriveSelection
    {
        public int Version { get; set; } = 1;

        /// <summary>
        /// Explicit user choices keyed by drive root without the slash ("C:")
        /// </summary>
        public Dictionary<string, bool> Drives { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal static class DriveSelectionStore
    {
        static readonly string SettingsPath = UserDataPaths.For("drive-selection.json");
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
        static volatile DriveSelection current;

        public static DriveSelection Load()
        {
            if (current != null) return current;
            var loaded = new DriveSelection();
            try
            {
                if (File.Exists(SettingsPath))
                    loaded = JsonSerializer.Deserialize<DriveSelection>(File.ReadAllText(SettingsPath), JsonOptions) ?? loaded;
            }
            catch { } //Unreadable settings must never prevent indexing - fall back to the default
            //The deserializer loses the case-insensitive comparer => rebuild the dictionary
            loaded.Drives = new Dictionary<string, bool>(loaded.Drives ?? new(), StringComparer.OrdinalIgnoreCase);
            return current = loaded;
        }

        public static void Save(DriveSelection selection)
        {
            current = selection;
            try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(selection, JsonOptions)); }
            catch { }
        }

        /// <summary>
        /// True when the drive should be indexed: the user's explicit choice, or NTFS by
        /// default. May query DriveFormat, which blocks on flaky network mounts - call it
        /// from the drive's own task, never from the UI thread.
        /// </summary>
        public static bool IsEnabled(string root)
        {
            if (Load().Drives.TryGetValue(root.TrimEnd(Path.DirectorySeparatorChar), out var enabled)) return enabled;
            try { return string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
