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
        /// <summary>
        /// Exact NTFS file reference (sequence + MFT entry) when the event came from
        /// the USN journal. Zero for FileSystemWatcher and app-echo events.
        /// </summary>
        public ulong Frn { get; }
        /// <summary>FILE_ATTRIBUTE_* bits carried by an NTFS USN record.</summary>
        public uint NtfsAttributes { get; }
        internal virtual NodeMetadataSnapshot? MetadataSnapshot => null;
        internal virtual INode MetadataNode => null;
        internal virtual long MetadataReadMs => 0;
        internal bool IsMetadataResult => this is MetadataFsEvent;

        public FsEvent(WatcherChangeTypes changeType, string fullPath, string oldFullPath = null,
            bool descendantDeletesReported = false, ulong frn = 0, uint ntfsAttributes = 0)
        {
            ChangeType = changeType;
            FullPath = fullPath;
            OldFullPath = oldFullPath;
            DescendantDeletesReported = descendantDeletesReported;
            Frn = frn;
            NtfsAttributes = ntfsAttributes;
        }

        internal static FsEvent MetadataResult(string path, INode expectedNode,
            NodeMetadataSnapshot snapshot, long readMs)
            => new MetadataFsEvent(path, expectedNode, snapshot, readMs);

        public static FsEvent From(FileSystemEventArgs e) => e is RenamedEventArgs r
            ? new FsEvent(e.ChangeType, e.FullPath, r.OldFullPath)
            : new FsEvent(e.ChangeType, e.FullPath);

        public override string ToString() => OldFullPath == null
            ? $"{ChangeType} {FullPath}"
            : $"{ChangeType} {OldFullPath} -> {FullPath}";

        /// <summary>
        /// Only deferred refresh results pay for the 24-byte snapshot, node identity
        /// and timing. Raw watcher/USN events dominate bursts and remain compact.
        /// </summary>
        sealed class MetadataFsEvent : FsEvent
        {
            readonly NodeMetadataSnapshot snapshot;
            readonly INode node;
            readonly long readMs;

            public MetadataFsEvent(string path, INode node,
                NodeMetadataSnapshot snapshot, long readMs)
                : base(WatcherChangeTypes.Changed, path)
            {
                this.node = node;
                this.snapshot = snapshot;
                this.readMs = readMs;
            }

            internal override NodeMetadataSnapshot? MetadataSnapshot => snapshot;
            internal override INode MetadataNode => node;
            internal override long MetadataReadMs => readMs;
        }
    }
}
