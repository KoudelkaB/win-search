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
        public static Process Start(string program, string arguments = "", string workDir = null, bool redirectInput = false)
        {
            var start = IsFlatpak
                ? new ProcessStartInfo("flatpak-spawn")
                {
                    Arguments = (string.IsNullOrEmpty(workDir) ? "" : $"--directory={Quote(workDir)} ")
                        + $"--host {Quote(program)} {arguments}"
                }
                : new ProcessStartInfo(program, arguments ?? "");
            start.UseShellExecute = false;
            //flatpak-spawn passes its standard input on, as pkexec does
            start.RedirectStandardInput = redirectInput;
            if (!IsFlatpak && !string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) start.WorkingDirectory = workDir;
            return ProcessLauncher.Start(start);
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
            return ProcessLauncher.Start(start);
        }

        /// <summary>Open a file or folder with its default application (xdg-open / portal)</summary>
        public static Process OpenDefault(string path)
            => ProcessLauncher.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });

        /// <summary>
        /// Run a program as root through polkit. GUI programs need the display variables,
        /// which pkexec deliberately drops. Its standard input it keeps: with
        /// <paramref name="redirectInput"/> the program reads what is written to it.
        /// </summary>
        public static Process StartElevated(string program, string arguments, string workDir, bool redirectInput = false)
        {
            var env = new StringBuilder("env");
            foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                    env.Append(' ').Append(Quote($"{name}={value}"));
            return Start("pkexec", $"{env} {Quote(program)} {arguments}", workDir, redirectInput);
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

        static readonly string[] Terminals = { "x-terminal-emulator", "gnome-terminal", "kgx", "ptyxis", "konsole",
            "xfce4-terminal", "mate-terminal", "tilix", "kitty", "alacritty", "wezterm", "foot", "xterm" };

        /// <summary>
        /// A terminal window in the folder: $TERMINAL, then the common emulators.
        /// <paramref name="asRoot"/>: with a root shell in that folder (<see cref="RootShell"/>).
        /// </summary>
        public static Process OpenTerminal(string folder, bool asRoot = false)
        {
            var command = asRoot ? RootShell(L.Text("RootShellFailed")) : null;
            var preferred = Environment.GetEnvironmentVariable("TERMINAL");
            foreach (var terminal in string.IsNullOrWhiteSpace(preferred) ? Terminals : Terminals.Prepend(preferred))
            {
                var path = Which(terminal);
                if (path == null) continue;
                return Start(path, TerminalArguments(terminal, folder, command), folder);
            }
            throw new FileNotFoundException(L.Text("NoTerminal"));
        }

        /// <summary>
        /// The arguments of a terminal: the folder, where it takes one (the others start in
        /// their working directory), and the command it runs - each terminal its own way.
        /// Without a command it runs the user's shell.
        /// </summary>
        internal static string[] TerminalArguments(string terminal, string folder, string[] command)
        {
            var run = command ?? Array.Empty<string>();
            string[] After(string option) => command == null ? run : run.Prepend(option).ToArray();
            //One string split by the rules of the shell: tilix joins the rest of its line so, and
            //kgx takes no more before version 43
            string[] AsOne() => command == null ? run : new[] { "-e", string.Join(" ", run.Select(PosixQuote)) };
            static string[] With(string[] options, string[] rest) => options.Concat(rest).ToArray();
            return Path.GetFileName(terminal) switch
            {
                "gnome-terminal" or "ptyxis" => With(new[] { "--working-directory=" + folder }, After("--")),
                "kgx" or "tilix" => With(new[] { "--working-directory=" + folder }, AsOne()),
                "konsole" => With(new[] { "--workdir", folder }, After("-e")),
                "xfce4-terminal" or "mate-terminal" => With(new[] { "--working-directory=" + folder }, After("-x")),
                "terminator" => After("-x"),
                "kitty" => With(new[] { "--directory", folder }, run),
                "alacritty" => With(new[] { "--working-directory", folder }, After("-e")),
                "wezterm" => With(new[] { "start", "--cwd", folder }, After("--")),
                "foot" => With(new[] { "--working-directory=" + folder }, run),
                //x-terminal-emulator (as Debian requires of it), xterm and the like
                _ => After("-e")
            };
        }

        /// <summary>
        /// A root shell in the working directory: sudo -s (sudo -i would start in root's home),
        /// su where there is no sudo. The password is asked first: a terminal closes with its
        /// command, so a refusal (a wrong password, a user without the right to sudo) would
        /// only flash a window. The window then stays with the reason and
        /// <paramref name="failed"/> until Enter.
        /// </summary>
        internal static string[] RootShell(string failed) => new[]
        {
            "sh", "-c",
            @"if command -v sudo >/dev/null; then sudo -v && exec sudo -s; else exec su; fi; printf '\n%s ' ""$1""; read -r _",
            "sh", failed
        };

        /// <summary>One word for a POSIX shell: as it is when nothing in it is special, else in single quotes</summary>
        internal static string PosixQuote(string value)
            => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "_-./=:,+@%".Contains(c))
                ? value
                : "'" + value.Replace("'", @"'\''") + "'";

        /// <summary>
        /// Quote one argument for ProcessStartInfo.Arguments (.NET splits that string by the
        /// same rules on every platform, and so do Windows programs: double quotes group,
        /// \" is a literal quote, and backslashes count only right before a quote - so a
        /// trailing one, "C:\", is doubled or it would escape the closing quote)
        /// </summary>
        internal static string Quote(string value)
        {
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '"', '\t' }) < 0) return value;
            var quoted = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
                backslashes = 0;
            }
            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}
