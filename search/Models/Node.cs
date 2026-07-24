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
            else if (n.IsDirectory)
            {
                try
                {
                    // Keep the original fast path. In particular, do not pre-enumerate the
                    // tree as Explorer does before deleting it.
                    Directory.Delete(n.FullName, true);
                }
                catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
                {
                    // ReadOnly is a general Windows filesystem attribute (Git loose objects
                    // are merely a common source). After any ordinary filesystem failure,
                    // finish the remaining tree in one pass and report all items that could
                    // not be removed only after every reachable sibling has been attempted.
                    var failures = DeleteTreeBestEffort(n.FullName);
                    if (failures.Count != 0) throw new PartialDeleteException(failures);
                }
            }
            else
            {
                try
                {
                    File.Delete(n.FullName);
                }
                catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
                {
                    var file = new FileInfo(n.FullName);
                    var failure = TryDeleteEntry(file);
                    if (failure != null)
                        throw new PartialDeleteException(new[] {
                            new DeleteFailure(n.FullName, failure)
                        });
                }
            }
        }

        /// <summary>
        /// Move a filesystem file or directory to the Recycle Bin.
        /// </summary>
        public static void Recycle(this INode n) => Recycle(n, RecyclePath);

        // The injectable operation keeps the best-effort traversal testable without putting
        // test artifacts into the user's real Recycle Bin.
        internal static void Recycle(this INode n, Action<string, bool> recycle)
        {
            if (n is ZipNode)
                throw new InvalidOperationException(
                    "Archive entries cannot be moved to the Recycle Bin.");

            try
            {
                recycle(n.FullName, n.IsDirectory);
            }
            catch (Exception ex) when (n.IsDirectory
                && IsRecoverableDeleteFailure(ex))
            {
                var failures = ProcessTreeBestEffort(
                    n.FullName,
                    (entry, _) => TryRecycleEntry(entry, recycle),
                    tryWholeDirectories: true);
                if (failures.Count != 0) throw new PartialDeleteException(failures);
            }
        }

        static void RecyclePath(string path, bool isDirectory)
        {
            if (isDirectory)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        }

        static List<DeleteFailure> DeleteTreeBestEffort(string path)
            => ProcessTreeBestEffort(path, TryDeleteEntry,
                tryWholeDirectories: false);

        static List<DeleteFailure> ProcessTreeBestEffort(
            string path,
            Func<FileSystemInfo, FileAttributes?, Exception> tryEntry,
            bool tryWholeDirectories)
        {
            var failures = new List<DeleteFailure>();
            var root = new DirectoryInfo(path);
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = root.Attributes;
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                return failures;
            }
            catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
            {
                failures.Add(new DeleteFailure(path, ex));
                return failures;
            }

            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                AddFailure(failures, root.FullName,
                    tryEntry(root, rootAttributes));
                return failures;
            }

            var pending = new Stack<(DirectoryInfo Directory,
                FileAttributes Attributes, bool Delete, int FailureCount)>();
            pending.Push((root, rootAttributes, false, 0));

            while (pending.Count != 0)
            {
                var (directory, attributes, delete, failureCount) = pending.Pop();
                if (delete)
                {
                    var failure = tryEntry(directory, attributes);
                    // A parent directory is expected to remain non-empty when one of its
                    // descendants could not be deleted. Keep the actionable leaf/enumeration
                    // error instead of adding a redundant error for every parent.
                    if (failures.Count == failureCount)
                        AddFailure(failures, directory.FullName, failure);
                    continue;
                }

                pending.Push((directory, attributes, true, failures.Count));
                try
                {
                    foreach (var entry in directory.EnumerateFileSystemInfos())
                    {
                        FileAttributes entryAttributes;
                        try
                        {
                            entryAttributes = entry.Attributes;
                        }
                        catch (Exception ex) when (IsMissing(ex))
                        {
                            continue;
                        }
                        catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
                        {
                            // Without attributes it is unsafe to enter a DirectoryInfo:
                            // it might be a reparse point. The configured operation can still
                            // remove or recycle the entry without our traversal entering it.
                            AddFailure(failures, entry.FullName,
                                tryEntry(entry, null));
                            continue;
                        }

                        if (entry is DirectoryInfo child
                            && (entryAttributes & FileAttributes.ReparsePoint) == 0)
                        {
                            if (tryWholeDirectories
                                && tryEntry(child, entryAttributes) == null)
                                continue;
                            pending.Push((child, entryAttributes, false, 0));
                        }
                        else
                            AddFailure(failures, entry.FullName,
                                tryEntry(entry, entryAttributes));
                    }
                }
                catch (Exception ex) when (IsMissing(ex))
                {
                    // The directory was removed concurrently.
                }
                catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
                {
                    failures.Add(new DeleteFailure(directory.FullName, ex));
                }
            }

            return failures;
        }

        static Exception TryRecycleEntry(FileSystemInfo entry,
            Action<string, bool> recycle)
        {
            try
            {
                recycle(entry.FullName, entry is DirectoryInfo);
                return null;
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                return null;
            }
            catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
            {
                return ex;
            }
        }

        static Exception TryDeleteEntry(FileSystemInfo entry,
            FileAttributes? knownAttributes = null)
        {
            try
            {
                var attributes = knownAttributes ?? entry.Attributes;
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes = attributes & ~FileAttributes.ReadOnly;
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                return null;
            }
            catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
            {
                // Attribute access may be denied while deletion is still permitted.
                // Let the delete itself determine whether the entry must remain.
            }

            try
            {
                entry.Delete();
                return null;
            }
            catch (Exception ex) when (IsMissing(ex))
            {
                return null;
            }
            catch (UnauthorizedAccessException first)
            {
                // The attribute may have changed since enumeration. Retry once after
                // clearing ReadOnly; other access failures are returned to the collector.
                try
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReadOnly) == 0) return first;
                    entry.Attributes = attributes & ~FileAttributes.ReadOnly;
                    entry.Delete();
                    return null;
                }
                catch (Exception ex) when (IsMissing(ex))
                {
                    return null;
                }
                catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
                {
                    return ex;
                }
            }
            catch (Exception ex) when (IsRecoverableDeleteFailure(ex))
            {
                return ex;
            }
        }

        static void AddFailure(List<DeleteFailure> failures, string path,
            Exception failure)
        {
            if (failure != null) failures.Add(new DeleteFailure(path, failure));
        }

        static bool IsMissing(Exception ex)
            => ex is FileNotFoundException || ex is DirectoryNotFoundException;

        static bool IsRecoverableDeleteFailure(Exception ex)
            => ex is IOException || ex is UnauthorizedAccessException;

        readonly struct DeleteFailure
        {
            public DeleteFailure(string path, Exception error)
            {
                Path = path;
                Error = error;
            }

            public string Path { get; }
            public Exception Error { get; }
        }

        sealed class PartialDeleteException : IOException
        {
            public PartialDeleteException(IReadOnlyList<DeleteFailure> failures)
                : base(BuildMessage(failures), new AggregateException(failures.Select(f =>
                    new IOException($"{f.Path}: {f.Error.Message}", f.Error))))
            {
            }

            static string BuildMessage(IReadOnlyList<DeleteFailure> failures)
            {
                var first = failures[0];
                return $"{failures.Count} undeletable item(s). First error at "
                    + $"{first.Path}: {first.Error.Message}";
            }
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
                if (Exists) Attributes = fi.Attributes;
                LastChangeTime = fi.LastWriteTime;
            }
            catch { }
        }

        /// <summary>Build from metadata already read by the deferred refresh worker.</summary>
        internal FileNode(string path, NodeMetadataSnapshot snapshot)
        {
            this.path = path;
            ApplyMetadata(snapshot);
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

    /// <summary>
    /// Path-backed node that must retain an exact NTFS identity after a create or
    /// rename. Kept separate so ordinary walked FileNodes and every ZipNode do not
    /// pay eight bytes for an FRN they can never have.
    /// </summary>
    sealed class FrnFileNode : FileNode
    {
        readonly ulong frn;

        internal FrnFileNode(string path, NodeMetadataSnapshot snapshot, ulong frn)
            : base(path, snapshot) => this.frn = frn;

        public override ulong Frn => frn;
    }
}
