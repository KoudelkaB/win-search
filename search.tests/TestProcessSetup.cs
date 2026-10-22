using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(search.Tests.TestAppBuilder))]

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

    /// <summary>
    /// UI tests ([AvaloniaFact]) run on the headless Avalonia UI thread with the real
    /// application styles - no window ever appears on the desktop.
    /// </summary>
    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
