using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace search
{
    /// <summary>
    /// Starting programs and showing files outside Windows. Inside a Flatpak sandbox the
    /// user's programs live on the host, so everything except the portal-backed xdg-open
    /// goes through "flatpak-spawn --host" (the manifest grants org.freedesktop.Flatpak).
    /// </summary>
    internal static class Shell
    {
        public static readonly bool IsFlatpak =
            !OperatingSystem.IsWindows() && (Environment.GetEnvironmentVariable("FLATPAK_ID") != null
                || File.Exists("/.flatpak-info"));

        static readonly ConcurrentDictionary<string, string> whichCache = new(StringComparer.Ordinal);

        /// <summary>
        /// Full path of a program on the PATH (of the host, when sandboxed), null when the
        /// program is not installed
        /// </summary>
        public static string Which(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (Path.IsPathRooted(name)) return IsFlatpak || File.Exists(name) ? name : null;
            return whichCache.GetOrAdd(name, Find);

            static string Find(string program)
            {
                if (IsFlatpak)
                {
                    try
                    {
                        var start = new ProcessStartInfo("flatpak-spawn")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        };
                        foreach (var a in new[] { "--host", "sh", "-c", "command -v \"$0\"", program }) start.ArgumentList.Add(a);
                        using var p = Process.Start(start);
                        var output = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(3000);
                        return p.ExitCode == 0 && output.Length > 0 ? output : null;
                    }
                    catch { return null; }
                }
                var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries)
                    .Concat(new[] { Path.Combine(home, ".local", "bin"), "/snap/bin", "/var/lib/flatpak/exports/bin",
                        Path.Combine(home, ".local", "share", "flatpak", "exports", "bin") }))
                {
                    var candidate = Path.Combine(dir, program);
                    try
                    {
                        if (File.Exists(candidate)
                            && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                            return candidate;
                    }
                    catch { }
                }
                return null;
            }
        }

        /// <summary>The first installed program of the names</summary>
        public static string FirstInstalled(params string[] names) => names.Select(Which).FirstOrDefault(p => p != null);

        /// <summary>Start a program on the host (arguments in the usual quoted command-line form)</summary>
        public static Process Start(string program, string arguments = "", string workDir = null)
        {
            var start = IsFlatpak
                ? new ProcessStartInfo("flatpak-spawn")
                {
                    Arguments = (string.IsNullOrEmpty(workDir) ? "" : $"--directory={Quote(workDir)} ")
                        + $"--host {Quote(program)} {arguments}"
                }
                : new ProcessStartInfo(program, arguments ?? "");
            start.UseShellExecute = false;
            if (!IsFlatpak && !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) start.WorkingDirectory = workDir;
            return Process.Start(start);
        }

        /// <summary>Start a program with an explicit argument list on the host</summary>
        public static Process Start(string program, IEnumerable<string> arguments, string workDir = null)
        {
            var start = new ProcessStartInfo(IsFlatpak ? "flatpak-spawn" : program) { UseShellExecute = false };
            if (IsFlatpak)
            {
                if (!string.IsNullOrEmpty(workDir)) start.ArgumentList.Add("--directory=" + workDir);
                start.ArgumentList.Add("--host");
                start.ArgumentList.Add(program);
            }
            else if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) start.WorkingDirectory = workDir;
            foreach (var a in arguments) start.ArgumentList.Add(a);
            return Process.Start(start);
        }

        /// <summary>Open a file or folder with its default application (xdg-open / portal)</summary>
        public static Process OpenDefault(string path)
            => Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });

        /// <summary>
        /// Run a program as root through polkit. GUI programs need the display variables,
        /// which pkexec deliberately drops.
        /// </summary>
        public static Process StartElevated(string program, string arguments, string workDir)
        {
            var env = new StringBuilder("env");
            foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    env.Append(' ').Append(Quote($"{name}={value}"));
            return Start("pkexec", $"{env} {Quote(program)} {arguments}", workDir);
        }

        /// <summary>
        /// Show the item selected in the desktop's file manager (org.freedesktop.FileManager1),
        /// falling back to opening its folder
        /// </summary>
        public static Process Reveal(string path, bool isFolder)
        {
            var uri = new Uri(path).AbsoluteUri;
            var gdbus = Which("gdbus");
            try
            {
                if (gdbus != null)
                {
                    var p = Start(gdbus, new[] { "call", "--session", "--dest", "org.freedesktop.FileManager1",
                        "--object-path", "/org/freedesktop/FileManager1",
                        "--method", isFolder ? "org.freedesktop.FileManager1.ShowFolders" : "org.freedesktop.FileManager1.ShowItems",
                        $"['{uri.Replace("'", "%27")}']", "" });
                    if (p != null && p.WaitForExit(3000) && p.ExitCode == 0) return p;
                }
                var dbusSend = Which("dbus-send");
                if (dbusSend != null)
                {
                    var p = Start(dbusSend, new[] { "--session", "--print-reply", "--dest=org.freedesktop.FileManager1",
                        "/org/freedesktop/FileManager1",
                        isFolder ? "org.freedesktop.FileManager1.ShowFolders" : "org.freedesktop.FileManager1.ShowItems",
                        $"array:string:{uri}", "string:" });
                    if (p != null && p.WaitForExit(3000) && p.ExitCode == 0) return p;
                }
            }
            catch (Exception e) { $"FileManager1 reveal failed: {e.Message}".Debug(); }
            return OpenDefault(isFolder ? path : Path.GetDirectoryName(path) ?? path);
        }

        /// <summary>A terminal window in the folder: $TERMINAL, then the common emulators</summary>
        public static Process OpenTerminal(string folder)
        {
            var preferred = Environment.GetEnvironmentVariable("TERMINAL");
            var candidates = new List<(string Program, string[] Args)>();
            if (!string.IsNullOrWhiteSpace(preferred)) candidates.Add((preferred, Array.Empty<string>()));
            candidates.AddRange(new (string, string[])[]
            {
                ("x-terminal-emulator", Array.Empty<string>()),
                ("gnome-terminal", new[] { "--working-directory=" + folder }),
                ("kgx", new[] { "--working-directory=" + folder }),
                ("ptyxis", new[] { "--working-directory=" + folder }),
                ("konsole", new[] { "--workdir", folder }),
                ("xfce4-terminal", new[] { "--working-directory=" + folder }),
                ("mate-terminal", new[] { "--working-directory=" + folder }),
                ("tilix", new[] { "--working-directory=" + folder }),
                ("kitty", new[] { "--directory", folder }),
                ("alacritty", new[] { "--working-directory", folder }),
                ("wezterm", new[] { "start", "--cwd", folder }),
                ("foot", new[] { "--working-directory=" + folder }),
                ("xterm", Array.Empty<string>())
            });
            foreach (var (program, args) in candidates)
            {
                var path = Which(program);
                if (path == null) continue;
                return Start(path, args, folder);
            }
            throw new FileNotFoundException(L.Text("NoTerminal"));
        }

        /// <summary>
        /// Quote one argument for ProcessStartInfo.Arguments (.NET splits that string by the
        /// same rules on every platform: double quotes group, \" is a literal quote)
        /// </summary>
        internal static string Quote(string value)
            => value.Length > 0 && value.IndexOfAny(new[] { ' ', '"', '\t' }) < 0
                ? value
                : "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
