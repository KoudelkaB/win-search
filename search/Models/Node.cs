using SharpCompress.Archives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace search.Models
{

    /// <summary>
    /// File/directory node contained in ZIP archive
    /// </summary>
    class ZipNode : FileNode
    {
        public ZipNode(INode zip, string path)
        {
            ZIP = zip;
            SetPath(Key = path);
            Attributes |= FileAttributes.Directory;
        }
        public ZipNode(INode zip, IArchiveEntry e)
        {
            ZIP = zip;
            // Directory entries carry a trailing '/' (e.g. "Logs/PHP/"). Left in place the
            // combined path ends with a separator, so Name (Path.GetFileName) is empty and the
            // grid shows a blank, zero-size ghost row that also duplicates the real folder.
            // Detect the folder by that trailing separator as well as IsDirectory, because some
            // writers (e.g. System.IO.Compression) mark a directory by name only, not by flag.
            var key = e.Key ?? "_";
            var isDir = e.IsDirectory || key.EndsWith('/') || key.EndsWith('\\');
            if (isDir) key = key.TrimEnd('/', '\\');
            SetPath(Key = string.IsNullOrEmpty(key) ? "_" : key);
            Attributes = FileAttributes.Compressed;
            if (isDir) Attributes |= FileAttributes.Directory;
            if (e.IsEncrypted) Attributes |= FileAttributes.Encrypted;
            Size = (ulong)e.Size;
            LastChangeTime = e.LastModifiedTime ?? DateTime.MinValue;
        }

        void SetPath(string p)
        {
            if (!ZipExtensions.IsSafeArchivePath(p))
                throw new InvalidDataException($"Unsafe archive entry path '{p}'.");
            path = Path.Combine(ZIP.FullName, p.Replace("/", "\\"));
        }

        public INode ZIP { get; protected set; }

        public string Key { get; protected set; }

        /// <summary>
        /// Get archive entry corresponding to this node
        /// </summary>
        /// <param name="a"></param>
        /// <returns></returns>
        public IArchiveEntry GetArchiveEntry(IArchive a) => a?.Entries.FirstOrDefault(e => e.Key == Key);

        /// <summary>
        /// Get the archive this node is in
        /// </summary>
        /// <returns></returns>
        public IArchive GetArchive() => (ZIP as INode).ToArchive();

        /// <summary>
        /// Get ZIP archive that the node represents or throw
        /// </summary>
        /// <returns></returns>
        public IArchive ToArchive()
        {
            //Sub-archive is independent of source archive => will work after source disposal!
            using var a = GetArchive();
            return GetArchiveEntry(a)?.ToArchive()?.StructuredSubArchive();
        }

        public void Delete() => throw new Exception("Deleting archive entry not yet supported");
    }

    public static class INodeExtensions
    {
        /// <summary>
        /// Delete file or directory
        /// </summary>
        /// <param name="n"></param>
        public static void Delete(this INode n)
        {
            if (n is ZipNode zn) zn.Delete();
            else if (n.IsDirectory) Directory.Delete(n.FullName, true);
            else File.Delete(n.FullName);
        }

        /// <summary>
        /// Paste here file or directory from clipboard
        /// </summary>
        /// <param name="n"></param>
        public static void Paste(this INode n)
        {
            if (n.IsDirectory)
            {
                //paste in this directory
            }
        }

        /// <summary>
        /// Get ZIP archive that the node represents or throw
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public static IArchive ToArchive(this INode n) => n is ZipNode zn ? zn.ToArchive() :
            ArchiveFactory.OpenArchive(n.FullName, new SharpCompress.Readers.ReaderOptions()).StructuredSubArchive();

        /// <summary>
        /// Get path to file or tempfile for archive content file
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public static string GetFileOrTempPath(this INode n, bool preserveAfterExit = false)
        {
            if (n is ZipNode zn)
            {
                //Save to the temp file
                string path = preserveAfterExit
                    ? App.CreateClipboardTempFolder()
                    : App.CreateTempFolder();
                path = Path.Combine(path, n.Name);
                using var a = zn.GetArchive();
                if (zn.IsDirectory)
                {
                    //Extract whole directory
                    var r = a.ExtractAllEntries();
                    try
                    {
                        int i = 0;
                        while (r.MoveToNextEntry())
                        {
                            if ((++i % 1000) == 0) { }
                            if (!r.Entry.IsDirectory && r.Entry.Key.StartsWith(zn.Key, StringComparison.OrdinalIgnoreCase) &&
                                r.Entry.Key.AsSpan(zn.Key.Length).IndexOfAny("/\\")==0)
                            {
                                var ePath = ZipExtensions.SafeExtractionPath(path, r.Entry.Key.Substring(zn.Key.Length + 1));
                                if (ePath == null) continue;
                                Directory.CreateDirectory(Path.GetDirectoryName(ePath));
                                using (var fs = File.Create(ePath)) r.WriteEntryTo(fs);
                            }
                        }
                    }
                    catch { } //We are at the end when the r.MoveToNextEntry() thows
                }
                else
                {
                    //Create the file
                    using (var fileStream = File.Create(path))
                        zn.GetArchiveEntry(a).OpenEntryStream().CopyTo(fileStream);
                }
                return path;
            }
            else return n.FullName;
        }
    }

    /// <summary>
    /// File/Directory Node from filesystem
    /// </summary>
    class FileNode : INode
    {
        protected string path;
        public FileNode() { }

        public FileNode(string path)
        {
            this.path = path;
            Exists = false; //Set only from a successful stat below
            try
            {
                FileSystemInfo fi = new FileInfo(path);
                if (fi.Exists)
                {
                    Size = (ulong)((FileInfo)fi).Length;
                }
                else
                {
                    // Mark as directory only when it really is one (not when the file just vanished)
                    fi = new DirectoryInfo(path);
                    if (fi.Exists) Attributes |= FileAttributes.Directory;
                }
                Exists = fi.Exists;
                LastChangeTime = fi.LastWriteTime;
            }
            catch { }
        }

        /// <summary>
        /// From an already enumerated entry - no extra stat call (used by the directory walk)
        /// </summary>
        public FileNode(FileSystemInfo info)
        {
            path = info.FullName; //Enumerated entries exist by definition => Exists stays true
            try
            {
                Attributes = info.Attributes;
                if (info is FileInfo f) Size = (ulong)f.Length;
                LastChangeTime = info.LastWriteTime;
            }
            catch { }
        }

        public void AddSize(ulong size) => Size += size;

        override public bool Exists { get; } = true; //ZipNodes and enumerated entries exist
        override public FileAttributes Attributes { get; protected set; } = 0;
        override public string Name => Path.GetFileName(path);
        override public ulong Size { get; protected set; }

        override public string FullName => path;

        override public DateTime LastChangeTime { get; protected set; }
    }
}
