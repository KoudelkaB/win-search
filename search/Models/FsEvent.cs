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
        /// <summary>
        /// True for NTFS USN delete records. The journal reports deletion of every file and
        /// directory in a recursive operation, so the consumer can remove this exact path
        /// without scanning the complete index for hypothetical unreported descendants.
        /// FileSystemWatcher and app echoes keep the conservative default: their directory
        /// delete/rename may be the only event for the whole subtree.
        /// </summary>
        public bool DescendantDeletesReported { get; }
        internal NodeMetadataSnapshot? MetadataSnapshot { get; private set; }
        internal INode MetadataNode { get; private set; }
        internal long MetadataReadMs { get; private set; }
        internal bool IsMetadataResult => MetadataSnapshot.HasValue;

        public FsEvent(WatcherChangeTypes changeType, string fullPath, string oldFullPath = null,
            bool descendantDeletesReported = false)
        {
            ChangeType = changeType;
            FullPath = fullPath;
            OldFullPath = oldFullPath;
            DescendantDeletesReported = descendantDeletesReported;
        }

        internal static FsEvent MetadataResult(string path, INode expectedNode,
            NodeMetadataSnapshot snapshot, long readMs)
            => new FsEvent(WatcherChangeTypes.Changed, path)
            {
                MetadataNode = expectedNode,
                MetadataSnapshot = snapshot,
                MetadataReadMs = readMs
            };

        public static FsEvent From(FileSystemEventArgs e) => e is RenamedEventArgs r
            ? new FsEvent(e.ChangeType, e.FullPath, r.OldFullPath)
            : new FsEvent(e.ChangeType, e.FullPath);

        public override string ToString() => OldFullPath == null
            ? $"{ChangeType} {FullPath}"
            : $"{ChangeType} {OldFullPath} -> {FullPath}";
    }
}
