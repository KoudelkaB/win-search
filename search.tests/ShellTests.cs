using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;

namespace search.Tests
{
    /// <summary>Terminals and the root shell of A, S on Linux</summary>
    public class ShellTests
    {
        static readonly string[] Command = { "sh", "-c", "echo 'a b'", "sh", "it's" };

        [Fact]
        public void EachTerminalRunsTheCommandItsOwnWay()
        {
            string[] With(params string[] options) => options.Concat(Command).ToArray();
            var joined = new[] { "-e", @"sh -c 'echo '\''a b'\''' sh 'it'\''s'" };

            Assert.Equal(With("--working-directory=/f", "--"), Shell.TerminalArguments("gnome-terminal", "/f", Command));
            Assert.Equal(With("--working-directory=/f", "--"), Shell.TerminalArguments("ptyxis", "/f", Command));
            //Before version 43 kgx takes one string; tilix joins the rest of its line into one
            Assert.Equal(joined.Prepend("--working-directory=/f"), Shell.TerminalArguments("kgx", "/f", Command));
            Assert.Equal(joined.Prepend("--working-directory=/f"), Shell.TerminalArguments("tilix", "/f", Command));
            Assert.Equal(With("--workdir", "/f", "-e"), Shell.TerminalArguments("konsole", "/f", Command));
            Assert.Equal(With("--working-directory=/f", "-x"), Shell.TerminalArguments("xfce4-terminal", "/f", Command));
            Assert.Equal(With("--working-directory=/f", "-x"), Shell.TerminalArguments("mate-terminal", "/f", Command));
            Assert.Equal(With("--directory", "/f"), Shell.TerminalArguments("kitty", "/f", Command));
            Assert.Equal(With("start", "--cwd", "/f", "--"), Shell.TerminalArguments("wezterm", "/f", Command));
            Assert.Equal(With("--working-directory=/f"), Shell.TerminalArguments("foot", "/f", Command));
            Assert.Equal(With("-e"), Shell.TerminalArguments("xterm", "/f", Command));
            Assert.Equal(With("-e"), Shell.TerminalArguments("x-terminal-emulator", "/f", Command));
            //$TERMINAL may be a path
            Assert.Equal(With("--working-directory", "/f", "-e"), Shell.TerminalArguments("/usr/bin/alacritty", "/f", Command));
            Assert.Equal(With("-x"), Shell.TerminalArguments("/usr/bin/terminator", "/f", Command));
        }

        [Fact]
        public void WithoutACommandATerminalRunsTheUsersShellInTheFolder()
        {
            Assert.Equal(new[] { "--working-directory=/f" }, Shell.TerminalArguments("gnome-terminal", "/f", null));
            Assert.Equal(new[] { "--working-directory=/f" }, Shell.TerminalArguments("tilix", "/f", null));
            Assert.Equal(new[] { "--workdir", "/f" }, Shell.TerminalArguments("konsole", "/f", null));
            Assert.Empty(Shell.TerminalArguments("xterm", "/f", null));
            Assert.Empty(Shell.TerminalArguments("my-terminal", "/f", null));
        }

        [Fact]
        public void APosixWordIsQuotedOnlyWhenItHasToBe()
        {
            Assert.Equal("plain", Shell.PosixQuote("plain"));
            Assert.Equal("/a/b-c_d.e", Shell.PosixQuote("/a/b-c_d.e"));
            Assert.Equal("''", Shell.PosixQuote(""));
            Assert.Equal("'a b'", Shell.PosixQuote("a b"));
            Assert.Equal(@"'it'\''s'", Shell.PosixQuote("it's"));
            Assert.Equal("'$HOME'", Shell.PosixQuote("$HOME"));
        }

        /// <summary>The one string of kgx and tilix comes apart into the very words again</summary>
        [Fact]
        public void TheJoinedCommandSplitsBackAsTheShellReadsIt()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "No POSIX shell");
            var words = Shell.RootShell("Press Enter, it's \"over\" $HOME").Concat(new[] { "", "a\tb", "\\x" }).ToArray();
            var joined = string.Join(" ", words.Select(Shell.PosixQuote));

            var output = Run(new[] { "-c", "eval \"set -- $1\"; printf '%s\\0' \"$@\"", "sh", joined }, null, "");

            Assert.Equal(words, output.Split('\0')[..^1]);
        }

        /// <summary>
        /// sudo refuses (a wrong password, no right to it): the window stays with the reason
        /// until Enter instead of closing at once. Let in, the shell is root's.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheRootShellWaitsWhenSudoRefuses(bool allowed)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "No POSIX shell");
            var folder = Path.Combine(Path.GetTempPath(), "fsm-sudo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                //sudo -v checks the password, sudo -s is the shell
                var sudo = Path.Combine(folder, "sudo");
                File.WriteAllText(sudo, allowed
                    ? "#!/bin/sh\n[ \"$1\" = -s ] && echo 'root shell'\nexit 0\n"
                    : "#!/bin/sh\necho 'not in the sudoers file' >&2\nexit 1\n");
                File.SetUnixFileMode(sudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var command = Shell.RootShell("Press Enter to close.");

                var output = Run(command.Skip(1), folder + ":/usr/bin:/bin", "\n");

                Assert.Equal(allowed ? "root shell\n" : "\nPress Enter to close. ", output);
            }
            finally { Directory.Delete(folder, true); }
        }

        static string Run(System.Collections.Generic.IEnumerable<string> arguments, string path, string input)
        {
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            if (path != null) start.Environment["PATH"] = path;
            using var process = Process.Start(start);
            process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            return output;
        }
    }
}
