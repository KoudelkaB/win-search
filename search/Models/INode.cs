using System;
using System.Collections.Generic;
using System.IO;

namespace search.Models
{
    internal readonly record struct NodeMetadataSnapshot(
        FileAttributes Attributes, ulong Size, DateTime LastChangeTime)
    {
        //Compatibility/convenience constructor for synthetic snapshots and tests.
        public NodeMetadataSnapshot(bool isDirectory, ulong size, DateTime lastChangeTime)
            : this(isDirectory ? FileAttributes.Directory : 0, size, lastChangeTime) { }

        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

        public static NodeMetadataSnapshot From(INode node)
            => new(node.Attributes, node.Size, node.LastChangeTime);
    }

    internal readonly record struct MftLoadTiming(
        long ReadParseMs, long LinkMs, long AggregateDirectoryHashMs, long FileHashDenseMs,
        long NamesSeen, long UniqueNames, long NameBytesSaved);

    /// <summary>
    /// Base of every file-system entry the search works with (MFT records, watcher events, archive entries).
    /// </summary>
    public abstract class INode
    {
        public abstract FileAttributes Attributes { get; protected set; }
        public abstract string Name { get; }
        public abstract ulong Size { get; protected set; }
        /// <summary>
        /// Number of entries below a directory, or one for a file. Files use this
        /// allocation-free default; only directory node types override it with storage.
        /// </summary>
        public virtual uint Count { get => IsDirectory ? 0U : 1U; protected set { } }
        public abstract string FullName { get; }

        /// <summary>
        /// Non-null => this node's path is PathParent's path + '\' + Name (a component
        /// chain that never stores the full path). Null => the node is path-terminal and
        /// FullName is its stored path (FileNode, ZipNode, MFT root and orphans).
        /// NodePath keys, compares and filters nodes through this decomposition.
        /// </summary>
        public virtual INode PathParent => null;

        public virtual string ParentName => Path.GetFileName(Path.GetDirectoryName(FullName)) ?? "";
        public virtual string Folder => Path.GetDirectoryName(FullName) ?? "";

        public abstract DateTime LastChangeTime { get; protected set; }

        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

        /// <summary>
        /// False only when the node was built from a path that did not resolve on disk
        /// (FileNode of a vanished or never-existing path). Such a node must never be
        /// indexed - it carries no metadata (FILETIME 0 = 1601 times, zero size).
        /// </summary>
        public virtual bool Exists => true;

        /// <summary>
        /// NTFS file reference number (MFT entry + sequence) when known - lets the USN
        /// journal watcher name deleted files, whose records carry no path. 0 = unknown
        /// (walked/watcher-added nodes); the watcher then falls back to resolving by id
        /// or reconciling the parent directory.
        /// </summary>
        public virtual ulong Frn => 0;

        /// <summary>
        /// Immutable scan nodes may cache their canonical path hash. Dynamic/path-backed
        /// nodes return false and are hashed from their current path.
        /// </summary>
        internal virtual bool TryGetPathHash(out int hash)
        {
            hash = 0;
            return false;
        }

        /// <summary>
        /// Adjust the size by a signed watcher-event delta. Saturates at 0 - aggregated
        /// directory sizes are best-effort between MFT reloads and a missed event must not
        /// wrap the unsigned size to exabytes.
        /// </summary>
        public void AddSizeDelta(long delta) =>
            Size = delta >= 0 ? Size + (ulong)delta : Size - Math.Min(Size, (ulong)-delta);

        /// <summary>
        /// Adjust a directory's descendant count. The unsigned value saturates at both
        /// ends so a missed or duplicated watcher event cannot wrap the grid value.
        /// The default file setter is intentionally a no-op: files always report one.
        /// </summary>
        public void AddCountDelta(long delta)
        {
            if (delta >= 0)
                Count = (uint)Math.Min(uint.MaxValue, (ulong)Count + (ulong)delta);
            else
            {
                var magnitude = (ulong)(-(delta + 1)) + 1;
                Count = (uint)((ulong)Count - Math.Min(Count, magnitude));
            }
        }

        /// <summary>
        /// Re-read size, displayed modification time and the directory flag from the file system
        /// </summary>
        public void Refresh()
        {
            if (TryReadMetadata(FullName, out var snapshot)) ApplyMetadata(snapshot);
        }

        /// <summary>
        /// Read metadata without mutating an indexed node. Potentially blocking filesystem
        /// calls can therefore run away from the serialized change queue and the snapshot
        /// can later be applied only if the same node identity is still indexed.
        /// </summary>
        internal static bool TryReadMetadata(string path, out NodeMetadataSnapshot snapshot)
        {
            snapshot = default;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                {
                    snapshot = new NodeMetadataSnapshot(fi.Attributes, (ulong)fi.Length,
                        fi.LastWriteTime);
                    return true;
                }

                var di = new DirectoryInfo(path);
                if (di.Exists)
                {
                    snapshot = new NodeMetadataSnapshot(di.Attributes, 0, di.LastWriteTime);
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Apply a previously read snapshot on the serialized model queue.</summary>
        internal void ApplyMetadata(NodeMetadataSnapshot snapshot)
        {
            var wasDirectory = IsDirectory;
            LastChangeTime = snapshot.LastChangeTime;
            Attributes = snapshot.Attributes;
            if (!snapshot.IsDirectory)
            {
                Size = snapshot.Size;
                Count = 1;
            }
            else if (!wasDirectory)
            {
                //A newly-created directory has no known descendants yet. Existing
                //directory aggregates survive ordinary metadata refreshes.
                Size = 0;
                Count = 0;
            }
        }
    }

    /// <summary>
    /// Dense MFT result whose record-number table can also answer FRN lookups. The USN
    /// watcher retains this source instead of allocating a second full node/reference map.
    /// </summary>
    internal interface IFrnNodeSource : IReadOnlyCollection<INode>
    {
        IReadOnlyList<INode> DenseNodes { get; }
        MftLoadTiming LoadTiming { get; }
        bool TryGetByFrn(ulong frn, out INode node);
        bool HasMultipleLinks(ulong frn);
    }
}
