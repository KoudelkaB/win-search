using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace search
{
    /// <summary>
    /// Files File Search Manager may not read (C:\Windows\Panther\setupact.log,
    /// /var/log/secure) - and so neither may an application it starts for them. Only a denial
    /// counts: a missing file or one in use by another program says nothing. A file on a
    /// network drive is not tried: an administrator is the same user to the server, and a
    /// long list of found files on a share costs nothing.
    /// </summary>
    internal static class ReadAccess
    {
        /// <summary>
        /// The files the app may not read, tried in parallel. <paramref name="first"/>: up to
        /// the first denied one. None when the app is an administrator itself.
        /// </summary>
        public static string[] Denied(IEnumerable<string> files, bool first = false)
        {
            if (Program.IsProcessElevated) return Array.Empty<string>();
            var types = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool OnNetwork(string file)
            {
                if (OperatingSystem.IsWindows() && file.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                var root = Models.FsRoots.RootOf(file);
                if (root == null) return false;
                if (!types.TryGetValue(root, out var network))
                    types[root] = network = Models.FsRoots.TypeOfRoot(root) == DriveType.Network;
                return network;
            }
            var local = files.Where(file => !string.IsNullOrEmpty(file) && !OnNetwork(file)).ToArray();
            var denied = new bool[local.Length];
            Parallel.For(0, local.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (i, loop) =>
            {
                if (!IsDenied(local[i])) return;
                denied[i] = true;
                if (first) loop.Stop();
            });
            return local.Where((_, i) => denied[i]).ToArray();
        }

        static bool IsDenied(string file)
        {
            try
            {
                using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                //A folder is no file to read
                return !Directory.Exists(file);
            }
            catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
            {
                return false;
            }
        }
    }
}
