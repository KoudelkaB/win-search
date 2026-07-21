using System;
using System.IO;
using search;
using Xunit;

namespace search.Tests
{
    public class StorageMaintenanceTests
    {
        [Fact]
        public void StartupCleanupDeletesCrashedRunButKeepsRecentClipboardMaterialization()
        {
            var root = CreateRoot();
            try
            {
                var staleRun = Directory.CreateDirectory(Path.Combine(root, "search.999999.1"));
                var recentClipboard = Directory.CreateDirectory(Path.Combine(root, "search.clipboard.999999.1"));
                var oldClipboard = Directory.CreateDirectory(Path.Combine(root, "search.clipboard.999998.1"));
                Directory.SetLastWriteTimeUtc(oldClipboard.FullName, DateTime.UtcNow.AddDays(-2));

                StorageMaintenance.CleanupTempFolders(root, null, DateTime.UtcNow);

                Assert.False(staleRun.Exists);
                Assert.True(recentClipboard.Exists);
                Assert.False(oldClipboard.Exists);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void StartupCleanupNeverDeletesCurrentSessionFolder()
        {
            var root = CreateRoot();
            try
            {
                var current = Directory.CreateDirectory(Path.Combine(root, "search.999999.1"));

                StorageMaintenance.CleanupTempFolders(root, current.FullName, DateTime.UtcNow);

                Assert.True(current.Exists);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void OversizedLogIsRotatedAndBackupWindowIsBounded()
        {
            var root = CreateRoot();
            try
            {
                var log = Path.Combine(root, "app.log");
                File.WriteAllBytes(log, new byte[StorageMaintenance.MaxLogBytes + 1]);
                for (var i = 1; i <= StorageMaintenance.LogBackupCount; i++)
                    File.WriteAllText(log + "." + i, i.ToString());

                StorageMaintenance.TryRotateLog(log);

                Assert.False(File.Exists(log));
                Assert.True(File.Exists(log + ".1"));
                Assert.Equal("2", File.ReadAllText(log + ".3"));
                Assert.False(File.Exists(log + ".4"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void HealthLogUsesItsSmallerIndependentRotationWindow()
        {
            var root = CreateRoot();
            try
            {
                var log = Path.Combine(root, "health.log");
                File.WriteAllBytes(log, new byte[StorageMaintenance.MaxHealthLogBytes + 1]);
                for (var i = 1; i <= StorageMaintenance.HealthLogBackupCount; i++)
                    File.WriteAllText(log + "." + i, i.ToString());

                StorageMaintenance.TryRotateLog(log, 0,
                    StorageMaintenance.MaxHealthLogBytes, StorageMaintenance.HealthLogBackupCount);

                Assert.False(File.Exists(log));
                Assert.True(File.Exists(log + ".1"));
                Assert.Equal("1", File.ReadAllText(log + ".2"));
                Assert.False(File.Exists(log + ".3"));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void ClipboardCleanupKeepsOnlyMaterializationsStillReferenced()
        {
            var root = CreateRoot();
            try
            {
                var retained = Directory.CreateDirectory(Path.Combine(root, "retained"));
                var obsolete = Directory.CreateDirectory(Path.Combine(root, "obsolete"));
                var retainedFile = Path.Combine(retained.FullName, "file.txt");
                File.WriteAllText(retainedFile, "data");

                StorageMaintenance.CleanupClipboardMaterializations(root, new[] { retainedFile });

                Assert.True(Directory.Exists(retained.FullName));
                Assert.False(Directory.Exists(obsolete.FullName));
            }
            finally { Directory.Delete(root, true); }
        }

        private static string CreateRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-maintenance-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
