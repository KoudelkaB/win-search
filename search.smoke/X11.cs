using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace search.Smoke
{
    /// <summary>
    /// The X server as the Linux smoke tests use it: xdotool for windows and real keys
    /// (XTEST), xclip for the clipboard, wmctrl to close a window the way a window manager does.
    /// Xvfb with any window manager is enough - no accessibility bus is needed.
    /// </summary>
    internal static class X11
    {
        static readonly string[] Tools = { "xdotool", "xclip", "wmctrl" };

        /// <summary>Why the X tests cannot run here, null when they can</summary>
        public static string Missing()
        {
            if (!OperatingSystem.IsLinux()) return "Linux only";
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return "No X display (DISPLAY is empty; xvfb-run gives one)";
            var absent = Tools.Where(t => Try("sh", $"-c \"command -v {t}\"").ExitCode != 0).ToArray();
            return absent.Length == 0 ? null : $"Missing {string.Join(", ", absent)} (apt install {string.Join(" ", absent)})";
        }

        /// <summary>The visible window whose title matches, null when there is none yet</summary>
        public static string FindWindow(string titlePattern, int? processId = null)
        {
            var result = Try("xdotool", $"search --onlyvisible {(processId is { } pid ? $"--pid {pid} " : "")}--name \"{titlePattern}\"");
            return result.ExitCode == 0 ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        }

        public static string ActiveWindow()
        {
            var active = Try("xdotool", "getactivewindow");
            if (active.ExitCode == 0) return active.Output.Trim();
            var focused = Try("xdotool", "getwindowfocus");
            return focused.ExitCode == 0 ? focused.Output.Trim() : null;
        }

        /// <summary>Make the window the one that gets the keys (without a window manager: the focus alone)</summary>
        public static void Activate(string window)
        {
            if (Try("xdotool", $"windowactivate --sync {window}").ExitCode != 0)
                Try("xdotool", $"windowfocus --sync {window}");
        }

        /// <summary>Real key presses in the xdotool syntax: "ctrl+c", "F2", "Shift_R"</summary>
        public static void Key(string keys) => Run("xdotool", $"key --clearmodifiers --delay 60 {keys}");

        public static void Type(string text) => Run("xdotool", $"type --clearmodifiers --delay 30 -- \"{text}\"");

        /// <summary>Ask the window to close, as its close button does</summary>
        public static void Close(string window) => Try("wmctrl", $"-i -c {window}");

        public static string Clipboard(string target = null) =>
            Try("xclip", $"-selection clipboard -o{(target == null ? "" : $" -t {target}")}").Output;

        /// <summary>Own the clipboard with this content (xclip keeps serving it in the background)</summary>
        public static void SetClipboard(string text, string target = null) => SetClipboard(Encoding.UTF8.GetBytes(text), target);

        static void SetClipboard(byte[] data, string target)
        {
            var start = new ProcessStartInfo("xclip") { RedirectStandardInput = true, UseShellExecute = false };
            foreach (var argument in new[] { "-selection", "clipboard", "-i" }) start.ArgumentList.Add(argument);
            if (target != null)
            {
                start.ArgumentList.Add("-t");
                start.ArgumentList.Add(target);
            }
            using var process = Process.Start(start);
            process.StandardInput.BaseStream.Write(data);
            process.StandardInput.Close();
            process.WaitForExit(5000);
        }

        /// <summary>
        /// The forms kept of what the clipboard holds, best first: xclip serves a single form, so
        /// files stay files (Nautilus' list, else the URI list) and an image an image, at the cost
        /// of the text the owner offered beside them
        /// </summary>
        static readonly string[] Kept =
        {
            "x-special/gnome-copied-files", "text/uri-list", "image/png",
            "UTF8_STRING", "text/plain;charset=utf-8", "STRING", "text/plain"
        };

        /// <summary>Not content: what X and clipboard managers ask an owner about it</summary>
        static readonly string[] Meta = { "TARGETS", "TIMESTAMP", "MULTIPLE", "SAVE_TARGETS", "DELETE", "INSERT_SELECTION", "INSERT_PROPERTY" };

        /// <summary>The clipboard's content in its best form, null when nobody owns the clipboard</summary>
        public static (string Target, byte[] Data)? SaveClipboard()
        {
            var listing = Try("xclip", "-selection clipboard -o -t TARGETS");
            if (listing.ExitCode != 0) return null;
            var targets = listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var target = Kept.FirstOrDefault(targets.Contains) ?? targets.FirstOrDefault(t => !Meta.Contains(t));
            if (target == null) return null;
            var start = new ProcessStartInfo("xclip") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-selection", "clipboard", "-o", "-t", target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            using var data = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(data);
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                return null;
            }
            copy.Wait();
            return process.ExitCode == 0 ? (target, data.ToArray()) : null;
        }

        /// <summary>
        /// Put saved content back; with none, an empty text owns the clipboard, so nothing a
        /// test put there can be pasted afterwards
        /// </summary>
        public static void RestoreClipboard((string Target, byte[] Data)? saved)
        {
            if (saved is { } content) SetClipboard(content.Data, content.Target);
            else SetClipboard(Array.Empty<byte>(), null);
        }

        static void Run(string program, string arguments) => PublishedApp.Run(program, arguments, timeoutSeconds: 30);

        static (int ExitCode, string Output) Try(string program, string arguments)
        {
            try
            {
                var start = new ProcessStartInfo(program, arguments)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var process = Process.Start(start);
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    return (-1, "");
                }
                return (process.ExitCode, output.Result);
            }
            catch (System.ComponentModel.Win32Exception) { return (-1, ""); } //Not installed
        }
    }
}
