using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace search
{
    public sealed class PinnedFilter : INotifyPropertyChanged
    {
        string name = "";
        string filter = "";
        bool isEditing;

        public string Name
        {
            get => name;
            set { if (name != value) { name = value; Changed(); } }
        }

        public string Filter
        {
            get => filter;
            set { if (filter != value) { filter = value; Changed(); } }
        }

        [JsonIgnore]
        public bool IsEditing
        {
            get => isEditing;
            set { if (isEditing != value) { isEditing = value; Changed(); } }
        }

        [JsonIgnore]
        public bool IsDraft { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        void Changed([CallerMemberName] string property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public enum BasketTargetKind
    {
        Folder,
        Archive,
        Executable,
        File
    }

    public sealed class BasketTarget
    {
        public string Path { get; set; } = "";
        public BasketTargetKind Kind { get; set; }

        public string Name => string.IsNullOrWhiteSpace(System.IO.Path.GetFileName(Path))
            ? Path
            : System.IO.Path.GetFileName(Path);

        public string Icon => Kind switch
        {
            BasketTargetKind.Folder => "📁",
            BasketTargetKind.Archive => "📦",
            BasketTargetKind.Executable => "▶",
            _ => "📄"
        };

        public string Description => Kind switch
        {
            BasketTargetKind.Folder => "Dropped items will be transferred into this folder.",
            BasketTargetKind.Archive => "Dropped items will be added to this archive.",
            BasketTargetKind.Executable => "This program will be started with dropped paths as arguments.",
            _ => "This file type is not currently a supported drop target."
        };
    }

    public sealed class WorkspaceSettings
    {
        public int Version { get; set; } = 1;
        public List<PinnedFilter> PinnedFilters { get; set; } = new();
        public List<BasketTarget> BasketTargets { get; set; } = new();
    }

    internal static class WorkspaceSettingsStore
    {
        private static readonly string SettingsPath = UserDataPaths.For("workspace-settings.json");
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static WorkspaceSettings Load() => Load(SettingsPath, tolerateFailure: true);

        public static WorkspaceSettings Import(string path) => Load(path, tolerateFailure: false);

        private static WorkspaceSettings Load(string path, bool tolerateFailure)
        {
            try
            {
                if (!File.Exists(path))
                    return new WorkspaceSettings();
                var settings = JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(path), JsonOptions)
                    ?? new WorkspaceSettings();
                Validate(settings);
                return settings;
            }
            catch when (tolerateFailure)
            {
                return new WorkspaceSettings();
            }
        }

        public static void Save(WorkspaceSettings settings) => Save(settings, SettingsPath);

        public static void Export(WorkspaceSettings settings, string path) => Save(settings, path);

        private static void Save(WorkspaceSettings settings, string path)
        {
            Validate(settings);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }

        internal static void Validate(WorkspaceSettings settings)
        {
            if (settings == null)
                throw new InvalidDataException("Settings are empty.");
            if (settings.Version != 1)
                throw new InvalidDataException($"Unsupported settings version {settings.Version}.");
            settings.PinnedFilters ??= new List<PinnedFilter>();
            settings.BasketTargets ??= new List<BasketTarget>();
            if (settings.PinnedFilters.Any(x => x == null || string.IsNullOrWhiteSpace(x.Name)))
                throw new InvalidDataException("Every pinned filter must have a name.");
            if (settings.BasketTargets.Any(x => x == null || string.IsNullOrWhiteSpace(x.Path)))
                throw new InvalidDataException("Every basket target must have a path.");
            if (settings.BasketTargets.Any(x => !Enum.IsDefined(x.Kind)))
                throw new InvalidDataException("A basket target has an unsupported type.");

            settings.PinnedFilters = settings.PinnedFilters
                .Select(x => new PinnedFilter { Name = x.Name.Trim(), Filter = x.Filter ?? "" })
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
            settings.BasketTargets = settings.BasketTargets
                .Select(x => new BasketTarget { Path = NormalizePath(x.Path), Kind = x.Kind })
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First()).ToList();
        }

        internal static string NormalizePath(string path)
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            try { return System.IO.Path.GetFullPath(path); }
            catch (Exception ex) { throw new InvalidDataException($"Invalid target path '{path}'.", ex); }
        }
    }
}
