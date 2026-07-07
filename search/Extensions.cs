using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Windows.Input;

namespace search
{
    public static class Extensions
    {
        /// <summary>
        /// Safe Get returning default value if the key not found
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <typeparam name="V"></typeparam>
        /// <param name="d"></param>
        /// <param name="key"></param>
        /// <param name="def">Default value to return when key not present</param>
        /// <returns></returns>
        public static V Get<K, V>(this Dictionary<K, V> d, K key, V def = default(V)) => d.TryGetValue(key, out var v) ? v : def;

        public static void Debug(this string text, bool msgBox = false)
        {
            System.Diagnostics.Debug.WriteLine(text);
#if DEBUG
            try
            {
                text = $"{Program.Role,-6} " + text;
                var log = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + text;
                System.Diagnostics.Debug.WriteLine(log);
                //Write to user data so unelevated runs never depend on install-folder access.
                File.AppendAllText(UserDataPaths.For("search.run.log"), log + "\n");
                if (msgBox) MessageBox.Show(text, "Debug", MessageBoxButton.OK, MessageBoxImage.Information);
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
        /// Elevated opens go through the broker when it is available (no prompt);
        /// otherwise each one shows its own UAC prompt.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Process Open(this string name, string args = "", string workDir = "", bool elevated = false)
        {
            if (elevated && !Program.IsProcessElevated)
            {
                if (Broker.Available) return Broker.OpenElevated(name, args, workDir);
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

            return Process.Start(new ProcessStartInfo
            {
                FileName = name,
                Arguments = args,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            });
        }

        /// <summary>
        /// Returns Visual parent control of events sender
        /// </summary>
        /// <param name="o"></param>
        /// <returns></returns>
        public static Control ParentControl(this object o)
        {
            var p = VisualTreeHelper.GetParent(o as Visual);
            if (p == null) return null;
            if (p is Control c) return c;
            return ParentControl(p);
        }

        /// <summary>
        /// Not needed remove
        /// </summary>
        /// <param name="files"></param>
        public static void FilesToClipBoard(this IEnumerable<string> files)
        {
            StringCollection paths = new StringCollection();
            foreach (var file in files) paths.Add(file);
            System.Windows.Clipboard.SetFileDropList(paths);
        }

        /// <summary>
        /// Copies only number of bytes from stream to another
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <param name="bytes"></param>
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

        public static void Select(this ListView lv, System.Collections.IEnumerable items)
        {
            // Usable up to few tenth of thousands selected items i.e. 5s
            // For more we need to implement out own selecting:
            // https://stackoverflow.com/questions/21940875/how-can-i-get-around-this-poor-wpf-listview-selecteditems-performance
            var setter = typeof(ListView).GetMethod("SetSelectedItems", BindingFlags.Instance | BindingFlags.NonPublic);
            setter.Invoke(lv, new[] { items });
        }
        public static void ToogleSelectAll(this ListView lv)
        {
            if (lv.SelectedItems.Count == lv.Items.Count) lv.UnselectAll(); else lv.SelectAll();
        }

        /// <summary>
        /// Delete Directory or file with given path - if it exis
        /// </summary>
        /// <param name="path"></param>
        public static void DeletePathIfExists(this string path)
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        /// <summary>
        /// Readable string representation of the key
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="key"></param>
        /// <returns></returns>
        public static string Str(this System.Windows.Input.Key key) => key switch
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
        /// Read until one of the given keys is encontered or the end
        /// </summary>
        /// <param name="e"></param>
        /// <param name="stoppers"></param>
        /// <returns></returns>
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
        /// <param name="e"></param>
        /// <returns></returns>
        public static long ReadDigits(this IEnumerator<Key> e)
        {
            long i = 0;
            try
            {
                while (e.MoveNext() && e.Current <= Key.HangulMode) i = 10 * i + (long)e.Current;

            }
            catch { } // Return the longest int possible
            return i;
        }

        /// <summary>
        /// Enusure to have at least n-elements 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="e"></param>
        /// <param name="n"></param>
        /// <returns></returns>
        public static bool AtLeast<T>(this IEnumerable<T> e, int n)
        {
            var x = e.GetEnumerator();
            while (n-- > 0) if (!x.MoveNext()) return false;
            return true;
        }

        /// <summary>
        /// Fast Count comparison
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="e"></param>
        /// <param name="n"></param>
        /// <returns></returns>
        public static bool IsCount<T>(this IEnumerable<T> e, int n)
        {
            var x = e.GetEnumerator();
            while (n-- > 0) if (!x.MoveNext()) return false;
            return !x.MoveNext(); // No next available
        }
    }
}
