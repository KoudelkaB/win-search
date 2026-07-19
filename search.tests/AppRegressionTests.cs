using SharpCompress.Archives;
using SharpCompress.Writers.Zip;
using System;
using System.ComponentModel;
using System.Collections.Specialized;
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
        [InlineData(true, "+Name", true, true)]   //Refill the capped window after visible removals
        [InlineData(true, "+Name", false, false)] //An unseen removal cannot leave a hole
        [InlineData(false, "+Size", false, true)] //Surviving ancestor sizes may need reordering
        [InlineData(false, "-Size", true, true)]
        [InlineData(false, "+Name", true, false)]
        public void BulkRemovalRefreshesOnlyForWindowBackfillOrSizeOrdering(
            bool truncated, string sort, bool visibleRowsRemoved, bool expected)
            => Assert.Equal(expected, SearchModel.BulkRemovalNeedsRefresh(truncated, sort, visibleRowsRemoved));

        [Theory]
        [InlineData("+Name", "-Name", true, true)]
        [InlineData("+Name", "+Size", true, true)]
        [InlineData("+Name", "-Name", false, false)] //Exactly MaxItems can still be complete
        [InlineData("+Name", "+Name", true, false)]
        public void SortRefreshUsesPersistentTruncationState(
            string currentSort, string newSort, bool truncated, bool expected)
            => Assert.Equal(expected, SearchModel.SortChangeNeedsFullSource(currentSort, newSort, truncated));

        [Theory]
        [InlineData(false, false, true, true, false, true, true)]  //Complete 7k view: sort now, reconcile later
        [InlineData(false, false, true, true, true, true, false)]  //Capped view needs the full source immediately
        [InlineData(false, true, true, true, false, true, false)]  //A filter change cannot reuse current rows
        [InlineData(true, false, true, true, false, true, false)]  //A data refresh remains authoritative
        [InlineData(false, false, true, false, false, true, false)] //Canceled/partial publishing cannot be reused
        [InlineData(false, false, true, true, false, false, false)] //Nothing to reconcile
        public void CompleteSmallResultsCanSortBeforePendingReconciliation(bool dataRefresh,
            bool filterChanged, bool sortChanged, bool complete, bool truncated, bool pending, bool expected)
            => Assert.Equal(expected, SearchModel.CanResortPublishedItems(dataRefresh, filterChanged,
                sortChanged, complete, truncated, pending));

        [Theory]
        [InlineData(null, false)]
        [InlineData("+Name", false)]
        [InlineData("-Folder", false)]
        [InlineData("+FullName", false)]
        [InlineData("+C", false)]
        [InlineData("+Size", true)]
        [InlineData("-LastChangeTime", true)]
        public void MetadataOnlyChangesMoveRowsOnlyForMetadataSorts(string sort, bool expected)
            => Assert.Equal(expected, SearchModel.MetadataSortMayMove(sort));

        [Fact]
        public void WatcherChangeWindowCoalescesDataButNotAcrossStructuralEvents()
        {
            var firstA = new FsEvent(WatcherChangeTypes.Changed, @"C:\a.txt");
            var finalA = new FsEvent(WatcherChangeTypes.Changed, @"C:\A.txt");
            var changedB = new FsEvent(WatcherChangeTypes.Changed, @"C:\b.txt");
            var created = new FsEvent(WatcherChangeTypes.Created, @"C:\folder");
            var afterCreateA = new FsEvent(WatcherChangeTypes.Changed, @"C:\a.txt");
            var finalAfterCreateA = new FsEvent(WatcherChangeTypes.Changed, @"C:\A.txt");
            var deleted = new FsEvent(WatcherChangeTypes.Deleted, @"C:\old.txt");

            var result = FSChangeProcessor.CoalesceChangedEvents(new[]
            {
                firstA, finalA, changedB, created, afterCreateA, finalAfterCreateA, deleted
            });

            Assert.Equal(5, result.Length);
            Assert.Same(finalA, result[0]);
            Assert.Same(changedB, result[1]);
            Assert.Same(created, result[2]);
            Assert.Same(finalAfterCreateA, result[3]);
            Assert.Same(deleted, result[4]);
        }

        [Fact]
        public void LiveUpdateWindowsStayBelowOneSecondAndIgnoreLastAccessNoise()
        {
            Assert.InRange(FSChangeProcessor.NormalCoalesceWindowMs, 1, 999);
            Assert.InRange(FSChangeProcessor.StormCoalesceWindowMs, FSChangeProcessor.NormalCoalesceWindowMs, 999);
            Assert.True(FSChangeProcessor.WatcherNotifyFilter.HasFlag(NotifyFilters.LastWrite));
            Assert.False(FSChangeProcessor.WatcherNotifyFilter.HasFlag(NotifyFilters.LastAccess));
            Assert.False(FSChangeProcessor.WatcherNotifyFilter.HasFlag(NotifyFilters.CreationTime));
            Assert.Equal(0u, search.Core.UsnJournal.ReturnOnlyOnClose);
        }

        [Fact]
        public void BulkGridReplacementUsesOneResetOnTheSameCollection()
        {
            var rows = new RangeObservableCollection<int>();
            rows.AddRange(new[] { 1, 2, 3 });
            var resets = 0;
            rows.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset) resets++;
            };

            rows.ReplaceRange(new[] { 4, 5 });

            Assert.Equal(new[] { 4, 5 }, rows);
            Assert.Equal(1, resets);
        }

        [Fact]
        public void BulkGridMergePreservesSortAndPublishedLimit()
        {
            var merged = SearchModel.MergeSortedWindow(
                new[] { 1, 3, 5 }, new[] { 2, 4, 6 }, (a, b) => a.CompareTo(b), 5);

            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, merged);
        }

        /// <summary>Path-less node with a controllable sort key - SelectTop only compares</summary>
        sealed class KeyNode : INode
        {
            public KeyNode(ulong size) => Size = size;
            public override FileAttributes Attributes { get => 0; protected set { } }
            public override string Name => Size.ToString();
            public override ulong Size { get; protected set; }
            public override string FullName => @"C:\" + Name;
            public override DateTime LastChangeTime { get => default; protected set { } }
        }

        [Fact]
        public void FilteredSnapshotCacheAppliesSmallAddsAndRemovalsWithoutRebuildingBase()
        {
            var first = (INode)new KeyNode(1);
            var removed = (INode)new KeyNode(2);
            var added = (INode)new KeyNode(3);
            var baseNodes = new[] { first, removed };
            var cache = new SearchModel.SnapshotCache("", 10, baseNodes, new NodeFilter(""));

            //The repeated "first" models insertion becoming visible to the live snapshot
            //enumerator just before its version/delta publication.
            var patched = cache.Patch(11, new[] { first, added }, new[] { removed });
            var materialized = patched.Materialize(() => false);

            Assert.Same(baseNodes, patched.Nodes); //The million-row base is reused
            Assert.Equal(11, patched.Version);
            Assert.Equal(new[] { first, added }, materialized);

            var removedAgain = patched.Patch(12, Array.Empty<INode>(), new[] { first });
            Assert.Equal(new[] { added }, removedAgain.Materialize(() => false));
        }

        [Fact]
        public void BoundedSelectionMatchesAFullSortsWindowExactly()
        {
            var rnd = new Random(42);
            var nodes = Enumerable.Range(0, 10_000)
                .Select(_ => (INode)new KeyNode((ulong)rnd.Next(1_000_000_000))).ToArray();
            Comparison<INode> bySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

            //The published window: same membership, same order as sort-everything-then-take
            var window = SearchModel.SelectTop(nodes, bySizeDesc, 1000);
            Assert.Equal(nodes.OrderByDescending(n => n.Size).Take(1000).Select(n => n.Size),
                window.Select(n => n.Size));

            //A window larger than the source degenerates to the full sort
            var all = SearchModel.SelectTop(nodes, bySizeDesc, nodes.Length + 5);
            Assert.Equal(nodes.OrderByDescending(n => n.Size).Select(n => n.Size),
                all.Select(n => n.Size));
        }

        [Fact]
        public void ScalarKeySelectionMatchesTheComparerExactly()
        {
            var rnd = new Random(11);
            var nodes = Enumerable.Range(0, 10_000)
                .Select(_ => (INode)new KeyNode((ulong)rnd.Next(1_000_000_000))).ToArray();
            //'+Size' semantics: largest first, scalar key inverted accordingly
            Comparison<INode> bySizeDesc = (a, b) => b.Size.CompareTo(a.Size);
            var window = SearchModel.SelectTop(nodes, bySizeDesc, 1000, null, n => ulong.MaxValue - n.Size);
            Assert.Equal(nodes.OrderByDescending(n => n.Size).Take(1000).Select(n => n.Size),
                window.Select(n => n.Size));

            //'-Size' semantics: smallest first, key used directly
            Comparison<INode> bySizeAsc = (a, b) => a.Size.CompareTo(b.Size);
            var ascending = SearchModel.SelectTop(nodes, bySizeAsc, 1000, null, n => n.Size);
            Assert.Equal(nodes.OrderBy(n => n.Size).Take(1000).Select(n => n.Size),
                ascending.Select(n => n.Size));

            //Massive ties defeat the scalar quantile too - the comparer fallback must produce
            //the exact window
            var tied = Enumerable.Range(0, 50_000)
                .Select(_ => (INode)new KeyNode((ulong)rnd.Next(3))).ToArray();
            var tiedWindow = SearchModel.SelectTop(tied, bySizeDesc, 1000, null, n => ulong.MaxValue - n.Size);
            Assert.Equal(tied.OrderByDescending(n => n.Size).Take(1000).Select(n => n.Size),
                tiedWindow.Select(n => n.Size));
        }

        [Fact]
        public void BoundedSelectionSurvivesMassiveKeyTies()
        {
            //Low-cardinality keys (content-search rank, zero sizes) defeat quantile pruning -
            //the selection must detect that and still produce the exact window
            var rnd = new Random(7);
            var nodes = Enumerable.Range(0, 50_000)
                .Select(_ => (INode)new KeyNode((ulong)rnd.Next(3))).ToArray();
            Comparison<INode> bySizeDesc = (a, b) => b.Size.CompareTo(a.Size);

            var window = SearchModel.SelectTop(nodes, bySizeDesc, 1000);
            Assert.Equal(nodes.OrderByDescending(n => n.Size).Take(1000).Select(n => n.Size),
                window.Select(n => n.Size));
        }

        [Fact]
        public void BoundedSelectionHonorsCancellation()
        {
            var nodes = Enumerable.Range(0, 200_000).Select(i => (INode)new KeyNode((ulong)i));
            Assert.Null(SearchModel.SelectTop(nodes, (a, b) => a.Size.CompareTo(b.Size), 10, () => true));
        }

        [Theory]
        [InlineData("+Name", ListSortDirection.Ascending)]
        [InlineData("-Name", ListSortDirection.Descending)]
        [InlineData("+Size", ListSortDirection.Descending)]
        [InlineData("-Size", ListSortDirection.Ascending)]
        [InlineData("+LastChangeTime", ListSortDirection.Descending)]
        public void GridHeaderArrowMatchesTheDisplayedValueOrder(string sort, ListSortDirection expected)
            => Assert.Equal(expected, MainWindow.HeaderSortDirection(sort));

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
