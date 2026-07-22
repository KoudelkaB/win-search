using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace search
{
    /// <summary>
    /// Which drives get indexed. Drives without an explicit user choice default to local
    /// NTFS only. Network mappings always require an explicit opt-in because their directory
    /// walk can dominate the whole load even when the server reports an NTFS backing volume.
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
        public static event Action<IReadOnlyList<string>> SelectionChanged;

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

        public static void Save(DriveSelection selection, IReadOnlyList<string> changedRoots)
        {
            current = selection;
            try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(selection, JsonOptions)); }
            catch { }
            if (changedRoots?.Count > 0) SelectionChanged?.Invoke(changedRoots);
        }

        public static bool TryGetExplicit(string root, out bool enabled)
            => Load().Drives.TryGetValue(
                root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "",
                out enabled);

        /// <summary>
        /// Apply the choices made in the drive dialog and return only roots whose effective
        /// selection actually changed. The caller can then refresh those roots without
        /// restarting an unrelated multi-million-entry C: scan.
        /// </summary>
        internal static IReadOnlyList<string> ApplyChoices(DriveSelection selection,
            IEnumerable<(string Key, string Root, bool Enabled)> choices)
        {
            var changed = new List<string>();
            foreach (var choice in choices)
            {
                //Compare with the effective value before mutating the shared settings object.
                //For an unknown network mapping IsEnabled returns false without touching SMB.
                var wasEnabled = selection.Drives.TryGetValue(choice.Key, out var saved)
                    ? saved : IsEnabled(choice.Root);
                if (wasEnabled != choice.Enabled) changed.Add(choice.Root);
                selection.Drives[choice.Key] = choice.Enabled;
            }
            return changed;
        }

        /// <summary>
        /// True when the drive should be indexed: the user's explicit choice, or local NTFS
        /// by default. Network mappings are rejected before DriveFormat is queried, avoiding
        /// both an implicit network scan and an SMB timeout merely to calculate the default.
        /// </summary>
        public static bool IsEnabled(string root)
        {
            if (TryGetExplicit(root, out var enabled)) return enabled;
            try
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network) return false;
                return DefaultEnabled(drive.DriveType, drive.DriveFormat);
            }
            catch { return false; }
        }

        internal static bool DefaultEnabled(DriveType type, string format)
            => type != DriveType.Network
            && string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A bounded readiness probe for removable and mapped drives. Accessing the root first is
    /// intentional: Windows can report a persistent SMB mapping as disconnected until the
    /// first real path access reconnects it, while DriveInfo.IsReady alone keeps returning false.
    /// </summary>
    internal static class DriveAvailability
    {
        internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(15);

        public static bool IsReady(DriveInfo drive)
        {
            if (drive == null) return false;
            try
            {
                if (drive.DriveType == DriveType.Fixed) return drive.IsReady;
                return ProbeWithTimeout(() =>
                {
                    if (drive.DriveType == DriveType.Network
                        && Directory.Exists(drive.RootDirectory.FullName)) return true;
                    return drive.IsReady;
                }, NetworkTimeout);
            }
            catch { return false; }
        }

        internal static bool ProbeWithTimeout(Func<bool> probe, TimeSpan timeout)
        {
            if (probe == null) return false;
            var ready = false;
            var thread = new Thread(() =>
            {
                try { ready = probe(); }
                catch { }
            }) { IsBackground = true };
            thread.Start();
            return thread.Join(timeout) && ready;
        }
    }
}
