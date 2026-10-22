using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Input;

namespace search
{
    public static class Extensions
    {
        /// <summary>
        /// Safe Get returning default value if the key not found
        /// </summary>
        public static V Get<K, V>(this Dictionary<K, V> d, K key, V def = default(V)) => d.TryGetValue(key, out var v) ? v : def;

        public static void Debug(this string text)
        {
            System.Diagnostics.Debug.WriteLine(text);
#if DEBUG
            try
            {
                text = $"{Program.Role,-6} " + text;
                var log = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + text;
                System.Diagnostics.Debug.WriteLine(log);
                //Write to user data so unelevated runs never depend on install-folder access.
                StorageMaintenance.AppendLog("search.run.log", log + Environment.NewLine);
            }
            catch { } //Do not break application
#endif
        }

        public static IEnumerable<T> AsEnum<T>(this T val, params T[] rest)
        {
            yield return val;
            foreach (var x in rest) yield return x;
        }

        /// <summary>
        /// Open file by shell or run process.
        /// Windows: elevated opens go through the broker when it is available (no prompt);
        /// otherwise each one shows its own UAC prompt. Linux: a document opens in its default
        /// application, a program runs (on the host when sandboxed), "as admin" asks polkit.
        /// </summary>
        public static Process Open(this string name, string args = "", string workDir = "", bool elevated = false)
        {
            if (!OperatingSystem.IsWindows()) return OpenUnix(name, args, workDir, elevated);
            if (elevated && !Program.IsProcessElevated)
            {
                if (Broker.Available) return Broker.OpenElevated(name, args, workDir);

                // Startup skipped the broker because the service was serving the index. This is
                // the first action that genuinely needs admin rights, so raise the offer now.
                if (Broker.CanOffer)
                {
                    if (Broker.EnsureStarted(Broker.ElevationWait))
                        return Broker.OpenElevated(name, args, workDir);
                    // Declining the offer declines this open too. Falling through would prompt a
                    // second time for the same action the user just refused.
                    if (Broker.Declined) return null;
                }
                try
                {
                    return Process.Start(new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = args,
                        WorkingDirectory = workDir,
                        UseShellExecute = true,
                        Verb = "runas" // Per-open UAC prompt when the broker was declined
                    });
                }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED
                {
                    return null; // User declined this one
                }
            }

            var extension = Path.GetExtension(name);
            var directExecutable = File.Exists(name) &&
                (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".com", StringComparison.OrdinalIgnoreCase));
            return Process.Start(new ProcessStartInfo
            {
                FileName = name,
                Arguments = args,
                // Explicit application paths should bypass shell association and
                // compatibility elevation. Document paths still use the shell.
                UseShellExecute = !directExecutable,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            });
        }

        static Process OpenUnix(string name, string args, string workDir, bool elevated)
        {
            if (string.IsNullOrEmpty(workDir)) workDir = null;
            var isDocument = string.IsNullOrWhiteSpace(args)
                && (Directory.Exists(name) || File.Exists(name) && !IsUnixExecutable(name));
            if (isDocument && !elevated) return Shell.OpenDefault(name);
            var program = Shell.Which(name) ?? name;
            if (elevated && !Program.IsProcessElevated)
                return Shell.StartElevated(program, args, workDir ?? (isDocument ? Path.GetDirectoryName(name) : null));
            return Shell.Start(program, args, workDir);
        }

        static bool IsUnixExecutable(string path)
        {
            try
            {
                return (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Show the path in the file manager, selected in its folder.
        /// "explorer /select,X" silently opens Explorer's default page (Quick access /
        /// This PC) when X does not exist - which is exactly what a stale grid row or a
        /// file renamed away meanwhile hits, and it looks like the app did nothing.
        /// Fall back to the nearest ancestor folder that does exist, so the user still
        /// lands where the item was.
        /// </summary>
        /// <param name="path">File or folder to reveal</param>
        /// <param name="elevated">Run Explorer elevated - the user asked to open as admin</param>
        /// <returns>The started file manager process, or null when nothing could be shown</returns>
        public static Process Reveal(this string path, bool elevated = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var exists = File.Exists(path) || Directory.Exists(path);
            if (!OperatingSystem.IsWindows())
            {
                if (exists) return Shell.Reveal(path, isFolder: false);
                var nearest = NearestExistingFolder(path);
                return nearest == null ? null : Shell.Reveal(nearest, isFolder: true);
            }
            if (exists)
                return Apps.Explorer.Open($"/select,\"{path}\"", elevated: elevated);
            var folder = NearestExistingFolder(path);
            //No ancestor left (the whole drive is gone) - opening the default page would
            //only pretend that something was shown
            return folder == null ? null
                : Apps.Explorer.Open($"\"{folder}\"", elevated: elevated);
        }

        /// <summary>
        /// Closest existing ancestor directory of the path, or null when even the drive
        /// root is unavailable. The path itself is never returned - the caller already
        /// established that it does not exist.
        /// </summary>
        public static string NearestExistingFolder(this string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                var folder = Path.GetDirectoryName(path.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                while (!string.IsNullOrEmpty(folder))
                {
                    if (Directory.Exists(folder)) return folder;
                    var parent = Path.GetDirectoryName(folder);
                    if (string.Equals(parent, folder, StringComparison.OrdinalIgnoreCase))
                        return null; //Root of a drive that is not mounted
                    folder = parent;
                }
            }
            catch { } //Malformed path (invalid characters, too long) - nothing to show
            return null;
        }

        /// <summary>
        /// Copies only number of bytes from stream to another
        /// </summary>
        public static void CopyStream(this Stream input, Stream output, long bytes)
        {
            var buffer = new byte[32768];
            int read;
            while (bytes > 0 &&
                   (read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes))) > 0)
            {
                output.Write(buffer, 0, read);
                bytes -= read;
            }
        }

        /// <summary>
        /// Delete Directory or file with given path - if it exists
        /// </summary>
        public static void DeletePathIfExists(this string path)
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        /// <summary>
        /// Readable string representation of the key
        /// </summary>
        public static string Str(this Key key) => key switch
        {
            // special chars
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemPipe => "|",
            Key.Divide => "/",
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Space => " ",
            // decimal numbers 0-9
            >= Key.D0 and <= Key.D9 => $"{(int)key - (int)Key.D0}",
            >= Key.NumPad0 and <= Key.NumPad9 => $"{(int)key - (int)Key.NumPad0}",
            // what ever it is
            _ => key.ToString()
        };

        public static string ReadTill(this IEnumerable<Key> e, params Key[] stoppers) => e.GetEnumerator().ReadTill(stoppers);

        /// <summary>
        /// Read until one of the given keys is encountered or the end
        /// </summary>
        public static string ReadTill(this IEnumerator<Key> e, params Key[] stoppers)
        {
            var sb = new StringBuilder();
            bool upper = false, upperPermanent = false;
            while (e.MoveNext() && !stoppers.Contains(e.Current))
            {
                switch (e.Current)
                {
                    case Key.CapsLock:
                        upperPermanent = !upperPermanent;
                        break;
                    case Key.RightShift:
                    case Key.LeftShift:
                        if (upperPermanent) upper = upperPermanent = false;
                        else if (upper) upperPermanent = true;
                        else upper = true;
                        break;
                    default:
                        var c = e.Current.Str();
                        if (c.Length > 1) c = $"<{c}>";
                        else if (!upper) c = c.ToLower();
                        sb.Append(c);
                        if (!upperPermanent) upper = false;
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Read digits keys
        /// </summary>
        public static long ReadDigits(this IEnumerator<Key> e)
        {
            long i = 0;
            try
            {
                // Both digit rows carry the same value; the raw enum value is not the digit
                // (Key.D0 is 34), and no digit key is below Key.HangulMode - the old bounds
                // check therefore stopped before the first digit and always returned 0.
                while (e.MoveNext() && TryDigit(e.Current, out var digit)) i = checked(10 * i + digit);
            }
            catch (OverflowException) { } // Return the longest number that still fits
            return i;
        }

        static bool TryDigit(Key key, out int digit)
        {
            digit = key switch
            {
                >= Key.D0 and <= Key.D9 => key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
                _ => -1
            };
            return digit >= 0;
        }

        /// <summary>
        /// Ensure to have at least n-elements
        /// </summary>
        public static bool AtLeast<T>(this IEnumerable<T> e, int n)
        {
            var x = e.GetEnumerator();
            while (n-- > 0) if (!x.MoveNext()) return false;
            return true;
        }

        /// <summary>
        /// Fast Count comparison
        /// </summary>
        public static bool IsCount<T>(this IEnumerable<T> e, int n)
        {
            var x = e.GetEnumerator();
            while (n-- > 0) if (!x.MoveNext()) return false;
            return !x.MoveNext(); // No next available
        }
    }
}
