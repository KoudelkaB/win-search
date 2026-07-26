using System;
using System.IO;
using System.Text.Json;

namespace search
{
    internal sealed class ElevationSettings { public bool StartHelperAtLaunch { get; set; } }

    /// <summary>
    /// Whether the elevated broker is spawned at startup (one UAC prompt for the whole session)
    /// or lazily on the first action that actually needs admin rights.
    ///
    /// Its own tiny file rather than a field in workspace-settings.json: this is read on the
    /// startup path before the UI exists, and workspace settings are bigger and load later.
    /// </summary>
    internal static class ElevationSettingsStore
    {
        static readonly string Path = UserDataPaths.For("elevation-settings.json");

        public static bool StartHelperAtLaunch()
        {
            try { return JsonSerializer.Deserialize<ElevationSettings>(File.ReadAllText(Path))?.StartHelperAtLaunch ?? false; }
            catch { return false; }
        }

        public static void Save(bool startHelperAtLaunch)
        {
            try { File.WriteAllText(Path, JsonSerializer.Serialize(new ElevationSettings { StartHelperAtLaunch = startHelperAtLaunch })); }
            catch (Exception e) { $"saving elevation settings failed: {e.Message}".Debug(); }
        }

        /// <summary>
        /// Spawn the broker up front only when something at startup actually needs it: indexing
        /// has to read the raw $MFT, and without the service the broker is the only unprompted
        /// way to do that. With the service serving, nothing at startup needs admin rights, so
        /// asking then would be speculative - the 'A' key and admin-only file access raise their
        /// own prompt at the moment they are used (see Broker.EnsureStarted).
        ///
        /// The explicit setting wins: users who prefer one predictable prompt at launch over an
        /// unexpected one mid-workflow can keep the old behaviour.
        /// </summary>
        public static bool ShouldStartHelperAtLaunch() =>
            StartHelperAtLaunch() || !ServiceProbe.IsServing();
    }
}
