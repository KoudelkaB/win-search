using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.IO;
using System.DirectoryServices.ActiveDirectory;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Diagnostics;

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
        public static List<string> UniversalCopyOrMove(this string file, string dest, bool overwrite, bool move = false)
        {
            var errors = new List<string>();
            if (file == dest)
            {
                // Create a copy if the same
                dest = Path.Combine(Path.GetDirectoryName(dest), Path.GetFileNameWithoutExtension(dest) + "-Copy" + Path.GetExtension(dest));
            }

            // Precreate the destination directory if it does not exist yet
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest));
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
            catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                // Try copy from unelevated (the file can exist in unelevated app only!)
                try
                {
                    // TODO:  If the destination is not seen on elevated => do everything on unelevated only! (rename, move, copy)
                    // file.CopyInUnevevated(dest, overwrite, move);
                    // For now we gan only copy from unelevated drive
                    file.CopyFromUnevevated(dest, overwrite, move);
                }
                catch (Exception ex)
                {
                    $"CopyFromUnevevated threw {ex}".Debug();
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
                // If the source is directory
                if (a.HasFlag(FileAttributes.Directory))
                {
                    if (move)
                    {
                        // Move the directory to a new path
                        if (overwrite) dest.DeletePathIfExists();
                        System.IO.Directory.Move(file, dest);
                    }
                    else errors.AddRange(new DirectoryInfo(file).CopyFolder(new DirectoryInfo(dest), overwrite));
                }
                else
                {
                    if (move) File.Move(file, dest, overwrite);
                    else File.Copy(file, dest, overwrite);
                }
            }
            catch (Exception e)
            {
                $"Copy failed {e}".Debug();
                errors.Add(e.Message);
            }
            return errors;
        }

        /// <summary>
        /// Copy all from source directory into destination firectory
        /// return message instead on exception
        /// </summary>
        /// <param name="source"></param>
        /// <param name="target"></param>
        /// <param name="overwrite"></param>
        /// <param name="move"></param>
        /// <returns>error messages or null</returns>
        static List<string> CopyFolder(this DirectoryInfo source, DirectoryInfo target, bool overwrite, bool move = false)
        {
            var errors = new List<string>();
            foreach (DirectoryInfo dir in source.GetDirectories())
                errors.AddRange(CopyFolder(dir, target.CreateSubdirectory(dir.Name), overwrite, move));

            foreach (FileInfo file in source.GetFiles())
                errors.AddRange(file.FullName.UniversalCopyOrMove(Path.Combine(target.FullName, file.Name), overwrite, move));

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
