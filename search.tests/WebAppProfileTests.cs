using System;
using System.IO;
using Xunit;

namespace search.Tests
{
    public class WebAppProfileTests
    {
        [Fact]
        public void IdleLegacyLocalStorageIsCopiedWithoutChangingTheOriginal()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var data = new ProfileData();
            var target = WebAppProfile.Prepare(data.Root);

            Assert.NotEqual(Path.Combine(data.Root, "WebView2"), target);
            Assert.Equal(data.Value, File.ReadAllText(Path.Combine(target, ProfileData.Storage, "000003.log")));
            Assert.Equal(data.Value, File.ReadAllText(data.SourceFile));
            Assert.False(File.Exists(Path.Combine(target, ProfileData.Storage, "LOCK")));
            Assert.Empty(Directory.GetDirectories(data.Root, "*.migration-*"));
        }

        [Fact]
        public void ActiveLegacyDatabaseIsLeftUntouchedAndTheNewHostCanStillStart()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var data = new ProfileData();
            using var browser = new FileStream(Path.Combine(Path.GetDirectoryName(data.SourceFile), "LOCK"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            browser.Lock(0, 1);

            var target = WebAppProfile.Prepare(data.Root);

            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(Path.Combine(target, ProfileData.Storage)));
            Assert.Equal(data.Value, File.ReadAllText(data.SourceFile));
            Assert.Empty(Directory.GetDirectories(data.Root, "*.migration-*"));
        }

        [Fact]
        public void ExistingNewProfileIsNeverOverwrittenByLegacyData()
        {
            using var data = new ProfileData();
            var target = Path.Combine(data.Root, WebAppProfile.FolderName);
            var storage = Path.Combine(target, ProfileData.Storage);
            Directory.CreateDirectory(storage);
            var currentFile = Path.Combine(storage, "000003.log");
            File.WriteAllText(currentFile, "new host settings");

            Assert.Equal(target, WebAppProfile.Prepare(data.Root));
            Assert.Equal("new host settings", File.ReadAllText(currentFile));
            Assert.Equal(data.Value, File.ReadAllText(data.SourceFile));
        }

        sealed class ProfileData : IDisposable
        {
            public static readonly string Storage = Path.Combine("EBWebView", "Default", "Local Storage", "leveldb");
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "win-search-profile-" + Guid.NewGuid().ToString("N"));
            public string SourceFile { get; }
            public string Value => "saved HTML app preferences";

            public ProfileData()
            {
                var storage = Path.Combine(Root, "WebView2", Storage);
                Directory.CreateDirectory(storage);
                SourceFile = Path.Combine(storage, "000003.log");
                File.WriteAllText(SourceFile, Value);
                File.WriteAllText(Path.Combine(storage, "LOCK"), "");
            }

            public void Dispose() => Directory.Delete(Root, recursive: true);
        }
    }
}
