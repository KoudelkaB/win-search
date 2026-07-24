using System.IO;
using System.Runtime.CompilerServices;

namespace search.Tests
{
    internal static class TestProcessSetup
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            UserDataPaths.UseRootForCurrentProcess(
                Path.Combine(Path.GetTempPath(), "win-search-tests"));
        }
    }
}
