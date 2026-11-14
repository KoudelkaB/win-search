#if WINDOWS_HOST
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace search.Smoke
{
    /// <summary>The end of a session: a start of its own, closed through its window</summary>
    public sealed class WindowsExitSmokeTests
    {
        [Fact]
        public async Task ClosingTheWindowEndsTheAppAndKeepsItsSettings()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows desktop only");
            var session = new WindowsApp();
            await session.InitializeAsync();
            try
            {
                session.Filter.Text = session.Token;

                var exitCode = session.CloseMainWindow(TimeSpan.FromSeconds(30));

                Assert.Equal(0, exitCode);
                Assert.True(File.Exists(Path.Combine(session.Data, "window-layout.json")));
                Assert.True(File.Exists(Path.Combine(session.Data, "workspace-settings.json")));
                //The temporary folders of the process are gone with it
                Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"search.{session.App.ProcessId}.*"));
            }
            finally { await session.DisposeAsync(); }
        }
    }
}
#endif
