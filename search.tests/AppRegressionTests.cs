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
