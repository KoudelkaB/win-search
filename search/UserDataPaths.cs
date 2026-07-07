using System;
using System.IO;

namespace search
{
    internal static class UserDataPaths
    {
        private static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "win-search");

        public static string For(string fileName)
        {
            Directory.CreateDirectory(Root);
            return Path.Combine(Root, fileName);
        }
    }
}
