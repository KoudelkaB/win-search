using System;
using System.IO;
using System.Threading;

namespace search
{
    internal static class UserDataPaths
    {
        private static string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "win-search");

        /// <summary>
        /// Redirect every user-data file written by this process. The test host uses this
        /// before running any tests so its diagnostics and persisted test state can never
        /// mix with those of a concurrently running application.
        /// </summary>
        internal static void UseRootForCurrentProcess(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A user-data root is required.", nameof(path));
            Interlocked.Exchange(ref root, Path.GetFullPath(path));
        }

        public static string For(string fileName)
        {
            var currentRoot = Volatile.Read(ref root);
            Directory.CreateDirectory(currentRoot);
            return Path.Combine(currentRoot, fileName);
        }
    }
}
