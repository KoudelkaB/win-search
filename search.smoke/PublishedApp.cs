using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace search.Smoke
{
    /// <summary>
    /// The build under test and the folders of one run. WIN_SEARCH_APP names a build made
    /// elsewhere (an installed copy, the release pipeline's output); otherwise the app is
    /// published here the way the installer publishes it.
    /// </summary>
    internal static class PublishedApp
    {
        /// <summary>The app's own variable for another user-data folder (Program.DataFolderVariable)</summary>
        public const string DataFolderVariable = "WIN_SEARCH_DATA";

        static readonly Lazy<string> executable = new(Locate);

        public static string Executable => executable.Value;

        static string Locate()
        {
            if (Environment.GetEnvironmentVariable("WIN_SEARCH_APP") is { Length: > 0 } given)
                return File.Exists(given) ? given : throw new FileNotFoundException("WIN_SEARCH_APP names no file", given);

            var repo = RepositoryRoot();
            var rid = RuntimeInformation.RuntimeIdentifier.StartsWith("win") ? "win-x64"
                : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
            var output = Path.Combine(repo, "search.smoke", "bin", "app", rid);
            Run("dotnet", $"publish \"{Path.Combine(repo, "search", "search.csproj")}\" -c Release -r {rid} --self-contained true -o \"{output}\"", repo);
            var exe = Path.Combine(output, OperatingSystem.IsWindows() ? "FileSearchManager.exe" : "FileSearchManager");
            return File.Exists(exe) ? exe : throw new FileNotFoundException("The publish made no executable", exe);
        }

        static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "search", "search.csproj"))) return dir.FullName;
            throw new DirectoryNotFoundException("The repository with search/search.csproj was not found above " + AppContext.BaseDirectory);
        }

        public static string Run(string program, string arguments, string workingDirectory = null, int timeoutSeconds = 600)
        {
            var start = new ProcessStartInfo(program, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };
            using var process = Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{program} {arguments} did not finish in {timeoutSeconds} s");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{program} {arguments} failed ({process.ExitCode}):\n{output.Result}\n{errors.Result}");
            return output.Result;
        }

        /// <summary>
        /// The folder for the files a run creates and finds: WIN_SEARCH_SMOKE_WORK (CI mounts
        /// a small file system there, so the index stays small), else beside the tests. Never
        /// under %LOCALAPPDATA% on Windows - WebView2 reads no file from there.
        /// </summary>
        public static string WorkFolder(string token) => Path.Combine(
            Environment.GetEnvironmentVariable("WIN_SEARCH_SMOKE_WORK") is { Length: > 0 } work ? work : Path.Combine(AppContext.BaseDirectory, "runs"),
            token);

        /// <summary>
        /// A data folder for one run: English texts (the tests look for them), the elevated
        /// helper off, and only the drives (mounts on Linux) of the work and temporary folders indexed
        /// </summary>
        public static void PrepareData(string data, string work)
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "language-settings.json"), "{\"Culture\":\"en\"}");
            File.WriteAllText(Path.Combine(data, "elevation-settings.json"), "{\"StartHelperAtLaunch\":false}");
            var drives = new Dictionary<string, bool>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                var used = new[] { work, Path.GetTempPath() }.Select(p => Path.GetPathRoot(Path.GetFullPath(p))!.TrimEnd('\\')).ToArray();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var key = drive.Name.TrimEnd('\\');
                    drives[key] = used.Contains(key, StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                //The app's roots are the mount points: only the one holding the work folder
                var mounts = File.ReadLines("/proc/self/mountinfo")
                    .Select(l => l.Split(' ')).Where(f => f.Length > 4)
                    .Select(f => f[4].Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\"))
                    .Distinct().ToArray();
                var full = Path.GetFullPath(work).TrimEnd('/') + "/";
                var own = mounts.Where(m => full.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal))
                    .OrderByDescending(m => m.Length).First();
                foreach (var mount in mounts) drives[mount] = mount == own;
            }
            File.WriteAllText(Path.Combine(data, "drive-selection.json"),
                JsonSerializer.Serialize(new { Version = 1, Drives = drives }));
        }

        /// <summary>Delete a run's folder; the app may hold a file a moment after it exits</summary>
        public static void Delete(string folder)
        {
            for (var attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { System.Threading.Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(250); }
            }
        }
    }
}
