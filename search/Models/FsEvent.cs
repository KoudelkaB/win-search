using System.IO;

namespace search.Models
{
    /// <summary>
    /// One file-system change flowing through the processing pipeline. Carrier-agnostic:
    /// produced from FileSystemWatcher events, from USN journal records and from the app's
    /// own operations (the local echo) - unlike RenamedEventArgs it can express a rename
    /// whose old path lies in a different directory (a move).
    /// </summary>
    class FsEvent
    {
        public WatcherChangeTypes ChangeType { get; }
        public string FullPath { get; }
        /// <summary>Renames only - the path the item had before</summary>
        public string OldFullPath { get; }

        public FsEvent(WatcherChangeTypes changeType, string fullPath, string oldFullPath = null)
        {
            ChangeType = changeType;
            FullPath = fullPath;
            OldFullPath = oldFullPath;
        }

        public static FsEvent From(FileSystemEventArgs e) => e is RenamedEventArgs r
            ? new FsEvent(e.ChangeType, e.FullPath, r.OldFullPath)
            : new FsEvent(e.ChangeType, e.FullPath);

        public override string ToString() => OldFullPath == null
            ? $"{ChangeType} {FullPath}"
            : $"{ChangeType} {OldFullPath} -> {FullPath}";
    }
}
