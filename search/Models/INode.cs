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

        public virtual string ParentName => Path.GetFileName(Path.GetDirectoryName(FullName)) ?? "";

        public abstract DateTime CreationTime { get; protected set; }
        public abstract DateTime LastChangeTime { get; protected set; }
        public abstract DateTime LastAccessTime { get; protected set; }

        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

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
