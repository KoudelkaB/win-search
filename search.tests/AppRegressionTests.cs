using SharpCompress.Archives;
using SharpCompress.Writers.Zip;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class AppRegressionTests
    {
        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData(@"C:\", true)]                       // .NET substitutes the watcher root for a rename half lost to buffer pressure
        [InlineData(@"C:", true)]
        [InlineData(@"C:\Users", false)]
        [InlineData(@"C:\Users\Bohdan\Downloads\a.zip", false)]
        public void HalfDeliveredWatcherEventsAreRecognizedByTheirRootPath(string path, bool root)
            => Assert.Equal(root, SearchModel.IsDriveRoot(path));

        [Theory]
        [InlineData(100_000, "+Name", true, true)]   //Refill the capped window after visible removals
        [InlineData(100_000, "+Name", false, false)] //An unseen removal cannot leave a hole
        [InlineData(50_000, "+Size", false, true)]   //Surviving ancestor sizes may need reordering
        [InlineData(50_000, "-Size", true, true)]
        [InlineData(50_000, "+Name", true, false)]
        public void BulkRemovalRefreshesOnlyForWindowBackfillOrSizeOrdering(
            int itemCount, string sort, bool visibleRowsRemoved, bool expected)
            => Assert.Equal(expected, SearchModel.BulkRemovalNeedsRefresh(itemCount, sort, visibleRowsRemoved));

        [Theory]
        [InlineData(10, 94, 10)] //The row following a deleted middle block moves to its first index
        [InlineData(98, 98, 97)] //Deleting the tail falls back to the new last row
        [InlineData(0, 0, -1)]   //No row remains to receive the keyboard caret
        [InlineData(-1, 10, -1)]
        public void DeletedSelectionContinuesAtItsFormerPosition(
            int firstSelectedIndex, int remainingCount, int expected)
            => Assert.Equal(expected, MainWindow.SelectionContinuationIndex(firstSelectedIndex, remainingCount));

        [Fact]
        public void NodeOfAVanishedPathReportsNotExisting()
        {
            // The ghost signature of the reported bug: FileInfo on a missing path yields
            // FILETIME 0 (1601 times) and zero size - such a node must never be indexed
            var node = new FileNode(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "ghost.zip"));
            Assert.False(node.Exists);

            var real = new FileNode(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            Assert.True(real.Exists);
            Assert.True(real.IsDirectory);
        }

        [Fact]
        public void ContentSearchFindsNeedlesLargerThanTheDefaultBuffer()
        {
            var path = Path.GetTempFileName();
            try
            {
                var needle = Enumerable.Repeat((byte)'x', 70_000).ToArray();
                File.WriteAllBytes(path, new byte[] { (byte)'a' }.Concat(needle).Concat(new byte[] { (byte)'z' }).ToArray());

                Assert.True(SearchModel.FindFileContents(path, needle) == true);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CaseInsensitiveContentSearchMatchesAcrossBufferBoundary()
        {
            var path = Path.GetTempFileName();
            try
            {
                var prefix = Enumerable.Repeat((byte)'x', (1 << 16) - 2);
                File.WriteAllBytes(path, prefix.Concat(Encoding.ASCII.GetBytes("AbCd")).ToArray());

                Assert.True(SearchModel.FindFileContents(path, Encoding.ASCII.GetBytes("abcd"), caseInsensitive: true) == true);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(@"..\outside.txt")]
        [InlineData(@"folder\..\..\outside.txt")]
        [InlineData(@"C:\outside.txt")]
        [InlineData(@"\server\share\outside.txt")]
        public void ArchiveExtractionRejectsPathsOutsideTheDestination(string entry)
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-extract");
            Assert.Null(ZipExtensions.SafeExtractionPath(root, entry));
            Assert.False(ZipExtensions.IsSafeArchivePath(entry));
        }

        [Fact]
        public void ArchiveExtractionAcceptsNestedRelativePaths()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-extract");
            var result = ZipExtensions.SafeExtractionPath(root, @"folder\child.txt");

            Assert.NotNull(result);
            Assert.StartsWith(Path.GetFullPath(root), result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyArchiveCanBeInspected()
        {
            using var archive = ArchiveFactory.CreateArchive<ZipWriterOptions>();
            Assert.Same(archive, archive.StructuredSubArchive());
        }

        [Fact]
        public void WindowsPathIdentityIgnoresCaseAndRelativeSpelling()
        {
            var path = Path.Combine(Path.GetTempPath(), "Win-Search", "File.txt");
            var alternate = Path.Combine(Path.GetDirectoryName(path), ".", "file.TXT");

            Assert.True(search.Models.Extensions.PathsReferToSameLocation(path, alternate));
        }

        [Fact]
        public void WorkspaceSettingsRoundTripPinnedFiltersAndTargets()
        {
            var path = Path.Combine(Path.GetTempPath(), $"win-search-{Guid.NewGuid():N}.json");
            try
            {
                var settings = new WorkspaceSettings
                {
                    PinnedFilters = { new PinnedFilter { Name = "Reports", Filter = "pdf: reports\\" } },
                    BasketTargets = { new BasketTarget { Path = Path.GetTempPath(), Kind = BasketTargetKind.Folder } }
                };

                WorkspaceSettingsStore.Export(settings, path);
                var restored = WorkspaceSettingsStore.Import(path);

                Assert.Equal("Reports", Assert.Single(restored.PinnedFilters).Name);
                Assert.Equal("pdf: reports\\", restored.PinnedFilters[0].Filter);
                Assert.Equal(Path.GetFullPath(Path.GetTempPath()), Assert.Single(restored.BasketTargets).Path);
                Assert.Equal(BasketTargetKind.Folder, restored.BasketTargets[0].Kind);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void WorkspaceSettingsRejectUnsupportedVersions()
        {
            var path = Path.Combine(Path.GetTempPath(), $"win-search-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, JsonSerializer.Serialize(new WorkspaceSettings { Version = 99 }));
                Assert.Throws<InvalidDataException>(() => WorkspaceSettingsStore.Import(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ExistingZipTargetCanReceiveFilesWithoutSevenZip()
        {
            var root = Path.Combine(Path.GetTempPath(), $"win-search-zip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "target.zip");
            var source = Path.Combine(root, "new.txt");
            try
            {
                File.WriteAllText(source, "new content");
                using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                    zip.CreateEntry("old.txt");

                ZipExtensions.AddToArchive(archive, source);

                using var updated = System.IO.Compression.ZipFile.OpenRead(archive);
                Assert.Contains(updated.Entries, x => x.FullName == "old.txt");
                Assert.Contains(updated.Entries, x => x.FullName == "new.txt");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ZipDirectoryEntryBecomesANamedFolderNotABlankZeroSizeRow()
        {
            // A zip carries directory entries with a trailing slash ("Logs/"). Left untrimmed the
            // node's path ended with a separator, so Name was empty and the grid showed a blank,
            // zero-size ghost row next to the real folder synthesized from the child files.
            var root = Path.Combine(Path.GetTempPath(), $"win-search-zipdir-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "logs.zip");
            try
            {
                using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntry("Logs/");                    // explicit directory entry
                    using var w = new StreamWriter(zip.CreateEntry("Logs/app.txt").Open());
                    w.Write("hello");
                }

                var container = new FileNode(archive);
                using var opened = ArchiveFactory.OpenArchive(archive, new SharpCompress.Readers.ReaderOptions());
                // Select the folder entry by its trailing separator - not every writer sets the
                // IsDirectory flag, which is exactly the case the fix has to survive.
                var dirEntry = opened.Entries.First(e => e.Key != null && e.Key.TrimEnd('/', '\\') == "Logs");
                var node = new ZipNode(container, dirEntry);

                Assert.Equal("Logs", node.Name);
                Assert.True(node.IsDirectory);
                Assert.False(node.FullName.EndsWith("\\"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ZipDirectoryReportsAggregatedChildSizeNotZero()
        {
            // Folder rows must total their contained entries (like the on-disk folder sizing),
            // not stay at 0 the way raw archive directory entries report.
            var root = Path.Combine(Path.GetTempPath(), $"win-search-zipsize-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "logs.zip");
            try
            {
                using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntry("Logs/");
                    using (var w = new StreamWriter(zip.CreateEntry("Logs/app.txt").Open())) w.Write("hello");         // 5 bytes
                    using (var w = new StreamWriter(zip.CreateEntry("Logs/sub/b.txt").Open())) w.Write("worldworld"); // 10 bytes
                }

                Assert.True(SearchModel.AddArchive(new FileNode(archive)));

                var logs = SearchModel.FindByPath(Path.Combine(archive, "Logs"));
                var sub = SearchModel.FindByPath(Path.Combine(archive, "Logs", "sub"));

                Assert.NotNull(logs);
                Assert.True(logs.IsDirectory);
                Assert.Equal(15ul, logs.Size);
                Assert.NotNull(sub);
                Assert.Equal(10ul, sub.Size);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DropDefaultsToMoveOnOneVolumeAndCopyForSeveralTargets()
        {
            var sources = new[] { @"C:\source\file.txt" };

            Assert.Equal(FileTransferAction.Move,
                MainWindow.SuggestedTransferAction(ModifierKeys.None, sources, new[] { @"C:\target" }));
            Assert.Equal(FileTransferAction.Copy,
                MainWindow.SuggestedTransferAction(ModifierKeys.None, sources,
                    new[] { @"C:\target", @"C:\backup" }));
        }

        [Theory]
        [InlineData(ModifierKeys.Control, FileTransferAction.Copy)]
        [InlineData(ModifierKeys.Shift, FileTransferAction.Move)]
        [InlineData(ModifierKeys.Alt, FileTransferAction.SymbolicLink)]
        public void DropModifiersSelectTheTransferAction(ModifierKeys modifiers, FileTransferAction expected)
        {
            Assert.Equal(expected, MainWindow.SuggestedTransferAction(modifiers,
                new[] { @"C:\source\file.txt" }, new[] { @"D:\target" }));
        }
    }
}
