using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.DirectoryServices.ActiveDirectory;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;

namespace search.Models
{
    public static class Extensions
    {
        /// <summary>
        /// Returns true if both collections refer to the same objects
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        public static bool IsIdentical<T>(this IReadOnlyList<T> a, IReadOnlyList<T> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!object.ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static string ParentFolder(this string s) => s.AsSpan().TrimEnd("/\\").LastIndexOfAny("/\\") switch { -1 => "", var i => s[0..i] };

        /// <summary>
        /// Return parent folder of  item
        /// The function differs from Path.GetDirectoryName by not switching / => \
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public static ReadOnlyMemory<char> ParentFolder(this ReadOnlyMemory<char> s) =>
            s.TrimEnd("/\\").Span.LastIndexOfAny("/\\") switch { -1 => ReadOnlyMemory<char>.Empty, var i => s.Slice(0, i) };

        /// <summary>
        /// Do action for each element
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="e"></param>
        /// <param name="a"></param>
        public static void ForEach<T>(this IEnumerable<T> e, Action<T> a)
        {
            foreach (var x in e) a(x);
        }

        public static void AddNew<T>(this ICollection<T> l, T value, IEqualityComparer<T> c)
        {
            if (l.All(x => !c.Equals(x, value))) l.Add(value);
        }

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes
        );

        /// <summary>
        /// Create hard link (or junction for directories) for file at dest
        /// </summary>
        /// <param name="file"></param>
        /// <param name="dest"></param>
        /// <returns></returns>
        public static List<string> Hardlink(this string file, string dest, bool overwrite = false)
        {
            var errors = new List<string>();
            try
            {
                if (overwrite) dest.DeletePathIfExists();
                if (System.IO.Directory.Exists(file))
                {
                    // Hardlink to directory i.e. NTFS Junction
                    var e = Process.Start(new ProcessStartInfo("cmd.exe", $" /C mklink /J \"{dest}\" \"{file}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                    }).StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(e)) errors.Add(e);
                }
                // Hardlink to file
                else if (!CreateHardLink(dest, file, IntPtr.Zero))
                {
                    var e = Marshal.GetLastWin32Error();
                    if (e == 0 && File.Exists(dest)) throw new Exception($"Destination file '{dest}' allready exists.");
                    throw new System.ComponentModel.Win32Exception(e);
                }
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
            }
            return errors;
        }

        /// <summary>
        /// Create soft link
        /// </summary>
        /// <param name="file"></param>
        /// <param name="dest"></param>
        /// <returns></returns>
        public static List<string> Softlink(this string file, string dest, bool overwrite = false)
        {
            var errors = new List<string>();
            try
            {
                if (overwrite) dest.DeletePathIfExists();
                if (System.IO.Directory.Exists(file))
                {
                    // Create symbolic link for directory
                    System.IO.Directory.CreateSymbolicLink(dest, file);
                }
                else
                {
                    // Create symbolic link for file
                    File.CreateSymbolicLink(dest, file);
                }
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
            }
            return errors;
        }

        /// <summary>
        /// Copy or move file 
        /// Resutns message instead on exception
        /// elevated x unelevated source x dest copiing implemented
        /// return message instead on exception
        /// </summary>
        /// <param name="file"></param>
        /// <param name="dest">Destination file or directory name (if the source is directory)</param>
        /// <param name="overwrite"></param>
        /// <param name="move"></param>
        /// <returns>error message or null if OK</returns>
        public static List<string> UniversalCopyOrMove(
            this string file,
            string dest,
            bool overwrite,
            bool move = false,
            CancellationToken cancellationToken = default)
        {
            using var nativeCancellation = cancellationToken.CanBeCanceled
                ? new NativeCopyCancellation(cancellationToken)
                : null;
            return UniversalCopyOrMove(
                file,
                dest,
                overwrite,
                move,
                cancellationToken,
                nativeCancellation,
                batchEcho: false);
        }

        static List<string> UniversalCopyOrMove(
            string file,
            string dest,
            bool overwrite,
            bool move,
            CancellationToken cancellationToken,
            NativeCopyCancellation nativeCancellation,
            bool batchEcho)
        {
            var errors = new List<string>();
            cancellationToken.ThrowIfCancellationRequested();
            if (!move && PathsReferToSameLocation(file, dest))
            {
                // Create a copy if the same
                dest = Path.Combine(Path.GetDirectoryName(dest), Path.GetFileNameWithoutExtension(dest) + "-Copy" + Path.GetExtension(dest));
            }

            // Precreate the destination directory if it does not exist yet
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(dest));
                if (!string.IsNullOrEmpty(parent)) System.IO.Directory.CreateDirectory(parent);
            }
            catch (Exception e)
            {
                // TODO:: If the destination directory does not exist on elevated => do it on unelevated
                // CopyToUnelevated... or do it completely on unelevated
                errors.Add(e.Message);
                return errors;
            }

            // Check if the source exists
            FileAttributes a = FileAttributes.Normal;
            try
            {
                a = File.GetAttributes(file);
            }
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException || e is UnauthorizedAccessException)
            {
                // The source is not visible to this unelevated process (ACL-protected shows
                // as access denied) => pull it through the elevated broker
                try
                {
                    if (!Broker.Available)
                        throw new UnauthorizedAccessException($"Access denied to '{file}' - the elevated helper is not running (it was declined at startup).");
                    Broker.CopyFromElevated(file, dest, overwrite, move);
                    EchoTransferred(file, dest, move, batchEcho);
                }
                catch (Exception ex)
                {
                    $"CopyFromElevated threw {ex}".Debug();
                    errors.Add(ex.Message);

                }
                return errors;
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
                return errors;
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // If the source is directory
                if (a.HasFlag(FileAttributes.Directory))
                {
                    if (move)
                    {
                        // Move the directory to a new path
                        if (overwrite) dest.DeletePathIfExists();
                        System.IO.Directory.Move(file, dest);
                    }
                    else if (a.HasFlag(FileAttributes.ReparsePoint))
                        errors.AddRange(CopyDirectoryLink(
                            new DirectoryInfo(file), dest, overwrite));
                    else errors.AddRange(new DirectoryInfo(file).CopyFolder(
                        new DirectoryInfo(dest),
                        overwrite,
                        cancellationToken,
                        nativeCancellation));
                }
                else
                {
                    if (move) File.Move(file, dest, overwrite);
                    else if (nativeCancellation != null)
                        CopyFileCancellable(file, dest, overwrite, nativeCancellation);
                    else
                        File.Copy(file, dest, overwrite);
                }
                EchoTransferred(file, dest, move, batchEcho);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                $"Copy failed {e}".Debug();
                errors.Add(e.Message);
            }
            return errors;
        }

        static List<string> CopyDirectoryLink(
            DirectoryInfo source,
            string destination,
            bool overwrite)
        {
            var errors = new List<string>();
            try
            {
                if (overwrite)
                    destination.DeletePathIfExists();
                var rawTarget = source.LinkTarget;
                if (string.IsNullOrEmpty(rawTarget))
                    throw new IOException($"Cannot read the target of directory link '{source.FullName}'.");
                try
                {
                    System.IO.Directory.CreateSymbolicLink(destination, rawTarget);
                }
                catch (UnauthorizedAccessException)
                {
                    // Creating a symbolic link may require Developer Mode or elevation.
                    // A junction needs neither, and preserves directory-link behavior.
                    var resolved = source.ResolveLinkTarget(returnFinalTarget: false)
                        ?? throw new IOException(
                            $"Cannot resolve the target of directory link '{source.FullName}'.");
                    errors.AddRange(resolved.FullName.Hardlink(destination, overwrite: false));
                }
            }
            catch (Exception e)
            {
                errors.Add(e.Message);
            }
            return errors;
        }

        static void CopyFileCancellable(
            string source,
            string destination,
            bool overwrite,
            NativeCopyCancellation cancellation)
        {
            var flags = overwrite ? CopyFileFlags.None : CopyFileFlags.FailIfExists;
            if (CopyFileEx(
                source,
                destination,
                IntPtr.Zero,
                IntPtr.Zero,
                cancellation.Pointer,
                flags))
                return;

            var error = Marshal.GetLastWin32Error();
            if (cancellation.Token.IsCancellationRequested || error == ErrorRequestAborted)
                throw new OperationCanceledException(cancellation.Token);
            throw new IOException(
                $"Cannot copy '{source}' to '{destination}': {new Win32Exception(error).Message}",
                new Win32Exception(error));
        }

        sealed class NativeCopyCancellation : IDisposable
        {
            readonly CancellationTokenRegistration registration;

            public NativeCopyCancellation(CancellationToken token)
            {
                Token = token;
                Pointer = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(Pointer, 0);
                registration = token.Register(
                    static state => Marshal.WriteInt32((IntPtr)state, 1),
                    Pointer);
            }

            public CancellationToken Token { get; }
            public IntPtr Pointer { get; }

            public void Dispose()
            {
                registration.Dispose();
                Marshal.FreeHGlobal(Pointer);
            }
        }

        const int ErrorRequestAborted = 1235;

        [Flags]
        enum CopyFileFlags : uint
        {
            None = 0,
            FailIfExists = 0x00000001
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CopyFileEx(
            string existingFileName,
            string newFileName,
            IntPtr progressRoutine,
            IntPtr data,
            IntPtr cancel,
            CopyFileFlags copyFlags);

        /// <summary>
        /// Report the app's own successful delete to the index so the row leaves the grid
        /// instantly - the watcher/journal event confirming it later is idempotent. Guarded
        /// by a stat: a delete the user canceled in an error dialog must not echo.
        /// </summary>
        public static void EchoDeleted(INode n)
        {
            if (n is ZipNode) return; //Archive entries are not file-system items
            var path = n.FullName;
            if (!File.Exists(path) && !System.IO.Directory.Exists(path))
            {
                //On a fully mapped NTFS volume, USN supplies an ordered delete for every
                //descendant. Mark this app echo as exact: the just-finished top-level
                //folder disappears from the grid immediately, while its already queued
                //journal records remove children without a full-index RemoveTrees pass.
                //If the journal is unavailable (or its baseline is not ready), retain the
                //conservative FileSystemWatcher behavior for correctness.
                var descendantsReported = n.IsDirectory
                    && FSChangeProcessor.ReportsCompleteDirectoryDeletes(path);
                _ = FSChangeProcessor.Echo(new FsEvent(WatcherChangeTypes.Deleted, path,
                    descendantDeletesReported: descendantsReported));
            }
        }

        /// <summary>
        /// Report the app's own successful copy/move (rename included) to the index - see
        /// <see cref="EchoDeleted"/>. A move echoes as a rename, so a moved directory's
        /// descendants are re-indexed under the new path at once.
        /// </summary>
        internal static void EchoTransferred(
            string source,
            string dest,
            bool move,
            bool batched = false)
        {
            FsEvent change = null;
            if (move && !File.Exists(source) && !System.IO.Directory.Exists(source))
                change = new FsEvent(WatcherChangeTypes.Renamed, dest, source);
            else if (!move && (File.Exists(dest) || System.IO.Directory.Exists(dest)))
                change = new FsEvent(WatcherChangeTypes.Created, dest);
            if (change != null)
                _ = batched
                    ? FSChangeProcessor.PostBatched(change)
                    : FSChangeProcessor.Echo(change);
        }

        /// <summary>
        /// Windows paths are case-insensitive. Comparing the input strings directly makes a
        /// case-only spelling of the same path reach File.Copy/File.Move, which then reports
        /// that source and destination are the same file.
        /// </summary>
        internal static bool PathsReferToSameLocation(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try
            {
                static string Normalize(string path) => Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Copy all from source directory into destination firectory
        /// return message instead on exception
        /// </summary>
        /// <param name="source"></param>
        /// <param name="target"></param>
        /// <param name="overwrite"></param>
        /// <returns>error messages or null</returns>
        static List<string> CopyFolder(
            this DirectoryInfo source,
            DirectoryInfo target,
            bool overwrite,
            CancellationToken cancellationToken,
            NativeCopyCancellation nativeCancellation)
        {
            var errors = new List<string>();
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.Exists)
                target.Create();
            foreach (DirectoryInfo dir in source.GetDirectories())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    errors.AddRange(CopyDirectoryLink(
                        dir,
                        Path.Combine(target.FullName, dir.Name),
                        overwrite));
                else
                    errors.AddRange(CopyFolder(
                        dir,
                        target.CreateSubdirectory(dir.Name),
                        overwrite,
                        cancellationToken,
                        nativeCancellation));
            }

            foreach (FileInfo file in source.GetFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                errors.AddRange(UniversalCopyOrMove(
                    file.FullName,
                    Path.Combine(target.FullName, file.Name),
                    overwrite,
                    move: false,
                    cancellationToken: cancellationToken,
                    nativeCancellation: nativeCancellation,
                    batchEcho: true));
            }

            return errors;
        }

        /// <summary>
        /// If the path is file return parent folder instead
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static string Directory(this string path) => File.Exists(path) ? Path.GetDirectoryName(path) : path;

    }

    /// <summary>
    /// To be obsolete in .NET Core 3.0 => https://github.com/dotnet/corefx/issues/31526 and https://github.com/dotnet/corefx/issues/31942
    /// </summary>
    class MemoryCharComparer : EqualityComparer<ReadOnlyMemory<char>>
    {
        StringComparison sc;
        MemoryCharComparer(StringComparison comparsion) => sc = comparsion;

        public static MemoryCharComparer CaseSensitive { get; } = new MemoryCharComparer(StringComparison.Ordinal);
        public static MemoryCharComparer IgnoreCase { get; } = new MemoryCharComparer(StringComparison.OrdinalIgnoreCase);

        public override bool Equals(ReadOnlyMemory<char> a, ReadOnlyMemory<char> b) => a.Span.Equals(b.Span, sc);

        public override int GetHashCode(ReadOnlyMemory<char> m) => string.GetHashCode(m.Span, sc);
    }
}
