using System;
using System.IO;

namespace search.Models
{
    /// <summary>
    /// Base of every file-system entry the search works with (MFT records, watcher events, archive entries).
    /// </summary>
    public abstract class INode
    {
        public abstract FileAttributes Attributes { get; protected set; }
        public abstract string Name { get; }
        public abstract ulong Size { get; protected set; }
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

        public abstract DateTime CreationTime { get; protected set; }
        public abstract DateTime LastChangeTime { get; protected set; }
        public abstract DateTime LastAccessTime { get; protected set; }

        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

        /// <summary>
        /// False only when the node was built from a path that did not resolve on disk
        /// (FileNode of a vanished or never-existing path). Such a node must never be
        /// indexed - it carries no metadata (FILETIME 0 = 1601 times, zero size).
        /// </summary>
        public virtual bool Exists => true;

        /// <summary>
        /// Adjust the size by a signed watcher-event delta. Saturates at 0 - aggregated
        /// directory sizes are best-effort between MFT reloads and a missed event must not
        /// wrap the unsigned size to exabytes.
        /// </summary>
        public void AddSizeDelta(long delta) =>
            Size = delta >= 0 ? Size + (ulong)delta : Size - Math.Min(Size, (ulong)-delta);

        /// <summary>
        /// Re-read size, times and the directory flag from the file system
        /// </summary>
        public void Refresh()
        {
            try
            {
                var fi = new FileInfo(FullName);
                if (fi.Exists)
                {
                    CreationTime = fi.CreationTime;
                    LastAccessTime = fi.LastAccessTime;
                    LastChangeTime = fi.LastWriteTime;
                    Size = (ulong)fi.Length;
                    Attributes &= ~FileAttributes.Directory;
                    return;
                }

                var di = new DirectoryInfo(FullName);
                if (di.Exists)
                {
                    CreationTime = di.CreationTime;
                    LastAccessTime = di.LastAccessTime;
                    LastChangeTime = di.LastWriteTime;
                    Attributes |= FileAttributes.Directory;
                }
            }
            catch { }
        }
    }
}
