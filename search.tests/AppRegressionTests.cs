using SharpCompress.Archives;
using SharpCompress.Writers.Zip;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        [InlineData(0, true)]
        [InlineData(8, true)]
        [InlineData(14, true)] //The batch that caused the recorded 1.3 s UI stall
        [InlineData(64, true)]
        [InlineData(65, false)]
        public void MediumWatcherBatchesAvoidAFullGridReset(int changes, bool incremental)
            => Assert.Equal(incremental, SearchModel.UsesIncrementalBatch(changes));

        [Theory]
        [InlineData(true, 14, false)]
        [InlineData(true, 64, false)]
        [InlineData(true, 65, true)]
        [InlineData(false, 1000, false)]
        public void DriveLoadingDefersOnlyLargeWatcherStorms(bool loading, int changes, bool deferred)
            => Assert.Equal(deferred, SearchModel.DefersLoadingBatch(loading, changes));

        [Fact]
        public void DriveScanRequestsCoalesceReasonsWithoutLosingRequestsDuringAScan()
        {
            var requests = new DriveScanRequestAccumulator();
            Assert.True(requests.Add(DriveScanReason.Startup));
            Assert.False(requests.Add(DriveScanReason.UsnHardLinkChange));
            Assert.False(requests.Add(DriveScanReason.UsnHardLinkChange));

            var first = requests.Snapshot();
            Assert.Equal(3, first.Total);
            Assert.Equal(1, first.Reasons[DriveScanReason.Startup]);
            Assert.Equal(2, first.Reasons[DriveScanReason.UsnHardLinkChange]);

            //A request arriving after the snapshot is not consumed by that scan.
            Assert.False(requests.Add(DriveScanReason.UsnHistoryLost));
            Assert.False(requests.Complete(first));

            var rerun = requests.Snapshot();
            Assert.Equal(1, rerun.Total);
            Assert.Equal(1, rerun.Reasons[DriveScanReason.UsnHistoryLost]);
            Assert.True(requests.Complete(rerun));

            //The next independent request must start a fresh per-drive worker.
            Assert.True(requests.Add(DriveScanReason.Retry));
        }

        [Fact]
        public void OlderSizeBatchCannotConsumeANewerChangeOfTheSameDirectory()
        {
            var directory = (INode)new KeyNode(1);
            var pending = new NonBlocking.ConcurrentDictionary<INode, long>();
            pending[directory] = 1;
            var olderBatch = pending.Single();

            pending[directory] = 2;

            Assert.False(SearchModel.ConsumePendingSizeChange(pending, olderBatch));
            Assert.Equal(2, pending[directory]);
            Assert.True(SearchModel.ConsumePendingSizeChange(pending,
                new KeyValuePair<INode, long>(directory, 2)));
            Assert.Empty(pending);
        }

        [Theory]
        [InlineData(@"Q:\Docs\single.bin", true)]
        [InlineData(@"Q:\Docs\Sub\nested.bin", true)]
        [InlineData(@"Q:\Docs2\sibling.bin", false)]
        [InlineData(@"Q:\other.bin", false)]
        public void FolderDeleteCoversOnlyItsRealDescendants(string path, bool expected)
        {
            IReadOnlySet<string> trees = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                @"Q:\Docs"
            };
            Assert.Equal(expected, SearchModel.IsBelowPendingTree(path, trees));
        }

        [Theory]
        [InlineData(@"C:\Sdk\es", @"C:\Sdk\fr\file.dll", false)]
        [InlineData(@"C:\Sdk\es", @"C:\Sdk\es", true)]
        [InlineData(@"C:\Sdk\es", @"C:\Sdk\es\file.dll", true)]
        [InlineData(@"C:\Sdk\es\file.dll", @"C:\Sdk\es", true)]
        [InlineData(@"C:\Sdk\es", @"C:\Sdk\esperanto\file.dll", false)]
        [InlineData(@"c:\sdk\ES\", @"C:\Sdk\es\file.dll", true)]
        public void PendingDeletesFlushOnlyForOverlappingPaths(string deleted, string current, bool expected)
            => Assert.Equal(expected, SearchModel.PathsOverlap(deleted, current));

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
        [InlineData(true, 100_000, 339, true)]   //Recorded Documents subtree -> direct children incident
        [InlineData(true, 100_000, 25_000, false)] //Exactly fourfold is not a substantial shrink
        [InlineData(true, 4_095, 100, false)]    //Small old views are cheap to reset in place
        [InlineData(false, 100_000, 339, false)] //Automatic refresh keeps the stable source
        [InlineData(true, 339, 100_000, false)]  //Expansion has no large old view to retire
        public void LargeFilterShrinkRetiresTheOldWpfCollectionView(bool filterChanged,
            int currentRows, int newRows, bool expected)
            => Assert.Equal(expected,
                SearchModel.PreferFreshItemsSource(filterChanged, currentRows, newRows));

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
        public void WatcherChangeWindowCollapsesAdjacentDuplicateCreatesOnly()
        {
            var firstCreate = new FsEvent(WatcherChangeTypes.Created, @"C:\asset.png");
            var duplicateCreate = new FsEvent(WatcherChangeTypes.Created, @"c:\ASSET.png");
            var changed = new FsEvent(WatcherChangeTypes.Changed, @"C:\asset.png");
            var laterCreate = new FsEvent(WatcherChangeTypes.Created, @"C:\asset.png");

            var result = FSChangeProcessor.CoalesceChangedEvents(new[]
            {
                firstCreate, duplicateCreate, changed, laterCreate
            });

            Assert.Equal(new[] { firstCreate, changed, laterCreate }, result);
        }

        [Fact]
        public void ExactUsnEchoReplacesAPathOnlyDuplicate()
        {
            var appEcho = new FsEvent(WatcherChangeTypes.Created, @"C:\asset.png");
            var usn = new FsEvent(WatcherChangeTypes.Created, @"c:\ASSET.png",
                frn: 0x0007_00000000002A, ntfsAttributes: 0x20);

            var result = FSChangeProcessor.CoalesceChangedEvents(new[] { appEcho, usn });

            Assert.Same(usn, Assert.Single(result));
            Assert.Equal(0x0007_00000000002AUL, result[0].Frn);
            Assert.Equal(0x20u, result[0].NtfsAttributes);
        }

        [Fact]
        public void FilesystemEventsDefaultToConservativeDirectoryDeletion()
        {
            var watcherDelete = new FsEvent(WatcherChangeTypes.Deleted, @"C:\tree");
            var usnDelete = new FsEvent(WatcherChangeTypes.Deleted, @"C:\tree", null,
                descendantDeletesReported: true);

            Assert.False(watcherDelete.DescendantDeletesReported);
            Assert.True(usnDelete.DescendantDeletesReported);
        }

        [Fact]
        public void DeferredMetadataResultCarriesSnapshotAndExpectedIdentity()
        {
            var node = (INode)new FileNode(@"C:\deferred-metadata.test",
                new NodeMetadataSnapshot(false, 10, DateTime.MinValue));
            var changed = new DateTime(2026, 7, 23, 10, 0, 0);
            var snapshot = new NodeMetadataSnapshot(false, 25, changed);

            var result = FsEvent.MetadataResult(node.FullName, node, snapshot, 1234);
            node.ApplyMetadata(result.MetadataSnapshot.Value);

            Assert.True(result.IsMetadataResult);
            Assert.Same(node, result.MetadataNode);
            Assert.Equal(1234, result.MetadataReadMs);
            Assert.Equal((ulong)25, node.Size);
            Assert.Equal(changed, node.LastChangeTime);
        }

        [Fact]
        public void DeferredMetadataResultsCoalesceToTheNewestSnapshot()
        {
            var node = (INode)new FileNode(@"C:\deferred-metadata.test",
                new NodeMetadataSnapshot(false, 10, DateTime.MinValue));
            var first = FsEvent.MetadataResult(node.FullName, node,
                new NodeMetadataSnapshot(false, 20, DateTime.MinValue), 5);
            var newest = FsEvent.MetadataResult(node.FullName, node,
                new NodeMetadataSnapshot(false, 30, DateTime.MinValue), 7);

            var result = FSChangeProcessor.CoalesceChangedEvents(new[] { first, newest });

            Assert.Single(result);
            Assert.Same(newest, result[0]);
            Assert.Equal((ulong)30, result[0].MetadataSnapshot.Value.Size);
        }

        [Fact]
        public void RawChangesDoNotCoalesceAcrossDeferredMetadataResults()
        {
            var node = (INode)new FileNode(@"C:\metadata-race.test",
                new NodeMetadataSnapshot(false, 10, DateTime.MinValue));
            var rawBefore = new FsEvent(WatcherChangeTypes.Changed, node.FullName,
                frn: 0x0007_00000000002A);
            var snapshot = FsEvent.MetadataResult(node.FullName, node,
                new NodeMetadataSnapshot(false, 20, DateTime.MinValue), 2);
            var rawAfter = new FsEvent(WatcherChangeTypes.Changed, node.FullName,
                frn: 0x0007_00000000002A);

            var result = FSChangeProcessor.CoalesceChangedEvents(
                new[] { rawBefore, snapshot, rawAfter });

            Assert.Equal(new[] { rawBefore, snapshot, rawAfter }, result);
        }

        [Fact]
        public void TargetedHardLinkSnapshotAbsorbsAnEarlierRawChangeForTheSameFile()
        {
            var node = (INode)new FileNode(@"C:\hard-link.test",
                new NodeMetadataSnapshot(false, 10, DateTime.MinValue));
            var rawBefore = new FsEvent(WatcherChangeTypes.Changed, node.FullName,
                frn: 0x0007_00000000002A);
            var unrelated = new FsEvent(WatcherChangeTypes.Changed, @"C:\other.test");
            var targeted = FsEvent.HardLinkUpdate(node.FullName,
                0x0007_00000000002A, node,
                new NodeMetadataSnapshot(false, 20, DateTime.MinValue),
                new[] { new HardLinkParentDelta(@"C:\", 10, 0) });
            var rawAfter = new FsEvent(WatcherChangeTypes.Changed, node.FullName,
                frn: 0x0007_00000000002A);

            var result = FSChangeProcessor.CoalesceChangedEvents(
                new[] { rawBefore, unrelated, targeted, rawAfter });

            Assert.Equal(new[] { unrelated, targeted, rawAfter }, result);
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

        [Fact]
        public void PureRemovalDiffTouchesOnlyDeletedRowsAndPromotesReserveTail()
        {
            var nodes = Enumerable.Range(1, 8)
                .Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var visible = new RangeObservableCollection<INode>();
            foreach (var node in nodes.Take(5)) visible.Add(node);
            var next = new[] { nodes[0], nodes[2], nodes[4], nodes[5], nodes[6] };
            ISet<INode> removed = new HashSet<INode>(new[] { nodes[1], nodes[3] },
                ReferenceEqualityComparer.Instance);
            var actions = new List<NotifyCollectionChangedAction>();
            ((INotifyCollectionChanged)visible).CollectionChanged += (_, e) => actions.Add(e.Action);

            var count = LiveResultWindow<INode>.PureRemovalDiffCount(visible, next, removed);
            LiveResultWindow<INode>.ApplyPureRemovalDiff(visible, next, removed);

            Assert.Equal(2, count);
            Assert.Equal(next, visible);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
            Assert.Equal(2, actions.Count(x => x == NotifyCollectionChangedAction.Remove));
            Assert.Equal(2, actions.Count(x => x == NotifyCollectionChangedAction.Add));
        }

        [Fact]
        public void PureRemovalDiffRejectsAReorderedSurvivor()
        {
            var nodes = Enumerable.Range(1, 4)
                .Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            IList<INode> visible = nodes.ToList();
            var next = new[] { nodes[1], nodes[0], nodes[2] };
            ISet<INode> removed = new HashSet<INode>(new[] { nodes[3] },
                ReferenceEqualityComparer.Instance);

            Assert.Equal(-1,
                LiveResultWindow<INode>.PureRemovalDiffCount(visible, next, removed));
        }

        [Fact]
        public void MixedTargetedDiffMovesChangedRowsWithoutResettingTheGrid()
        {
            var nodes = Enumerable.Range(0, 7)
                .Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var visible = new RangeObservableCollection<INode>();
            foreach (var node in nodes.Skip(1).Take(5)) visible.Add(node); //1,2,3,4,5
            var next = new[] { nodes[0], nodes[1], nodes[3], nodes[2], nodes[6] };
            ISet<INode> changed = new HashSet<INode>(new[] { nodes[0], nodes[2] },
                ReferenceEqualityComparer.Instance);
            var actions = new List<NotifyCollectionChangedAction>();
            visible.CollectionChanged += (_, e) => actions.Add(e.Action);

            var plan = LiveResultWindow<INode>.PlanTargetedDiff(visible, next, changed);
            LiveResultWindow<INode>.ApplyTargetedDiff(visible, next, plan);

            Assert.NotNull(plan);
            Assert.Equal(6, plan.OperationCount); //remove 2/4/5, insert 0/2/6
            Assert.Equal(next, visible);
            Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        }

        /// <summary>Path-less node with a controllable sort key - SelectTop only compares</summary>
        sealed class KeyNode : INode
        {
            public KeyNode(ulong size) => Size = size;
            public void SetSize(ulong size) => Size = size;
            public override FileAttributes Attributes { get => 0; protected set { } }
            public override string Name => Size.ToString();
            public override ulong Size { get; protected set; }
            public override string FullName => @"C:\" + Name;
            public override DateTime LastChangeTime { get => default; protected set { } }
        }

        sealed class PathKeyNode : INode
        {
            readonly string path;
            public PathKeyNode(string path) => this.path = path;
            public override FileAttributes Attributes { get => 0; protected set { } }
            public override string Name => Path.GetFileName(path);
            public override ulong Size { get; protected set; }
            public override string FullName => path;
            public override DateTime LastChangeTime { get => default; protected set { } }
        }

        [Fact]
        public void ResultWindowPromotesReserveRowsWithoutAFullSourceQuery()
        {
            var nodes = Enumerable.Range(1, 8).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(visibleLimit: 3, reserveLimit: 3,
                reserveLowWatermark: 3);
            window.Reset(nodes.Take(6).ToList(), unknownTail: true);
            var visible = nodes.Take(3).ToList();
            var operations = new List<LiveResultWindow<INode>.Operation>();

            window.Apply(nodes[1], include: false, (a, b) => a.Size.CompareTo(b.Size), operations);
            LiveResultWindow<INode>.ApplyOperations(visible, operations);

            Assert.Equal(new ulong[] { 1, 3, 4 }, visible.Select(n => n.Size));
            Assert.Equal(2, window.ReserveCount);
            Assert.True(window.NeedsRefill);

            //A previously unknown-tail node improves enough to enter the visible prefix.
            ((KeyNode)nodes[7]).SetSize(0);
            operations.Clear();
            window.Apply(nodes[7], include: true, (a, b) => a.Size.CompareTo(b.Size), operations);
            LiveResultWindow<INode>.ApplyOperations(visible, operations);

            Assert.Equal(new ulong[] { 0, 1, 3 }, visible.Select(n => n.Size));
            Assert.Equal(3, window.ReserveCount);
            Assert.False(window.NeedsRefill);
        }

        [Fact]
        public void MutableSortKeyIsRemovedBeforeItIsReinserted()
        {
            var nodes = Enumerable.Range(1, 6).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(3, 3, 1);
            window.Reset(nodes.ToList(), unknownTail: true);
            var visible = nodes.Take(3).ToList();
            var operations = new List<LiveResultWindow<INode>.Operation>();

            ((KeyNode)nodes[1]).SetSize(10);
            window.Apply(nodes[1], include: true, (a, b) => a.Size.CompareTo(b.Size), operations);
            LiveResultWindow<INode>.ApplyOperations(visible, operations);

            Assert.Equal(new ulong[] { 1, 3, 4 }, visible.Select(n => n.Size));
            Assert.Equal(new ulong[] { 1, 3, 4 }, window.VisibleSnapshot().Select(n => n.Size));
        }

        [Fact]
        public void AdjacentMutableKeysAreRemovedBeforeSmallBatchBinaryInsertion()
        {
            var nodes = Enumerable.Range(1, 6).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(3, 3, 1);
            window.Reset(nodes.ToList(), unknownTail: false);
            var visible = nodes.Take(3).ToList();
            var operations = new List<LiveResultWindow<INode>.Operation>();

            ((KeyNode)nodes[1]).SetSize(9);
            ((KeyNode)nodes[2]).SetSize(8);
            window.ApplySmallBatch(new[] { nodes[1], nodes[2] }, _ => true,
                (a, b) => a.Size.CompareTo(b.Size), operations);
            LiveResultWindow<INode>.ApplyOperations(visible, operations);

            Assert.Equal(new ulong[] { 1, 4, 5 }, visible.Select(n => n.Size));
            Assert.Equal(window.VisibleSnapshot(), visible);
        }

        [Fact]
        public void LargeDeltaMergesOnlyTheMaterializedWindow()
        {
            var nodes = Enumerable.Range(1, 6).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var zero = (INode)new KeyNode(0);
            var between = (INode)new KeyNode(45);
            var included = new HashSet<INode>(new[] { zero, between }, ReferenceEqualityComparer.Instance);
            var changed = new[] { nodes[1], nodes[2], zero, between };
            var window = new LiveResultWindow<INode>(3, 3, 1);
            window.Reset(nodes.ToList(), unknownTail: true);

            var visible = window.ApplyBatch(changed, included.Contains,
                (a, b) => a.Size.CompareTo(b.Size));

            Assert.Equal(new ulong[] { 0, 1, 4 }, visible.Select(n => n.Size));
            //The new size-45 node is worse than the last known row. It cannot safely fill
            //the private tail because an unmaterialized size-7 node may precede it.
            Assert.Equal(2, window.ReserveCount);
            Assert.True(window.HasUnknownTail);
        }

        [Fact]
        public void ExactShortTailDoesNotRequestARefill()
        {
            var nodes = Enumerable.Range(1, 4).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(3, 3, 1);
            window.Reset(nodes.ToList(), unknownTail: false);
            var visible = nodes.Take(3).ToList();
            var operations = new List<LiveResultWindow<INode>.Operation>();

            window.Apply(nodes[0], include: false, (a, b) => a.Size.CompareTo(b.Size), operations);
            LiveResultWindow<INode>.ApplyOperations(visible, operations);

            Assert.Equal(new ulong[] { 2, 3, 4 }, visible.Select(n => n.Size));
            Assert.False(window.IsTruncated);
            Assert.False(window.NeedsRefill);
        }

        [Fact]
        public void OneLargeDeleteCanMarkTheVisibleWindowIncomplete()
        {
            var nodes = Enumerable.Range(1, 8).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(visibleLimit: 3, reserveLimit: 2,
                reserveLowWatermark: 1);
            window.Reset(nodes.Take(5).ToList(), unknownTail: true);

            var visible = window.ApplyBatch(nodes.Take(4).ToArray(), _ => false,
                (a, b) => a.Size.CompareTo(b.Size));

            Assert.Single(visible);
            Assert.True(window.NeedsRefill);
            Assert.True(window.IsVisibleIncomplete);
        }

        [Fact]
        public void UnknownOffscreenNodeCannotPretendToRefillAReserveHole()
        {
            var nodes = Enumerable.Range(1, 7).Select(i => (INode)new KeyNode((ulong)i)).ToArray();
            var window = new LiveResultWindow<INode>(visibleLimit: 3, reserveLimit: 2,
                reserveLowWatermark: 2);
            window.Reset(nodes.Take(5).ToList(), unknownTail: true);

            //The old tail row becomes much worse. Nodes 6 and 7 are known to exist beyond
            //the materialized prefix, so row 5 cannot be kept as the apparent next reserve.
            ((KeyNode)nodes[4]).SetSize(100);
            window.ApplyBatch(new[] { nodes[4] }, _ => true,
                (a, b) => a.Size.CompareTo(b.Size));

            Assert.Equal(new ulong[] { 1, 2, 3 }, window.VisibleSnapshot().Select(n => n.Size));
            Assert.Equal(1, window.ReserveCount);
            Assert.True(window.NeedsRefill);
        }

        [Fact]
        public void RandomSmallBatchesKeepTheExternalGridEqualToTheWindow()
        {
            var rnd = new Random(731);
            var nodes = Enumerable.Range(0, 25)
                .Select(i => (INode)new KeyNode((ulong)(i * 1000))).ToArray();
            var included = new HashSet<INode>(nodes, ReferenceEqualityComparer.Instance);
            Comparison<INode> compare = (a, b) => a.Size.CompareTo(b.Size);
            var window = new LiveResultWindow<INode>(visibleLimit: 20, reserveLimit: 10,
                reserveLowWatermark: 3);
            window.Reset(nodes.OrderBy(n => n.Size).ToList(), unknownTail: false);
            var visible = window.VisibleSnapshot().ToList();

            for (var batch = 0; batch < 250; batch++)
            {
                var changed = nodes.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 6)).ToArray();
                foreach (var node in changed)
                {
                    ((KeyNode)node).SetSize((ulong)(batch * 100 + rnd.Next(100)));
                    if (rnd.Next(5) == 0)
                    {
                        if (!included.Remove(node)) included.Add(node);
                    }
                }

                var operations = new List<LiveResultWindow<INode>.Operation>();
                window.ApplySmallBatch(changed, included.Contains, compare, operations);
                LiveResultWindow<INode>.ApplyOperations(visible, operations);

                var expected = included.OrderBy(n => n.Size).Take(20).ToArray();
                Assert.Equal(expected, window.VisibleSnapshot());
                Assert.Equal(expected, visible);
            }
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

        [Fact]
        public void PublishedLimitIsSelectedFromAllIndexedDrivesBeforePathSortIsTruncated()
        {
            var nodes = Enumerable.Range(0, 100_100)
                .Select(i => (INode)new PathKeyNode($@"C:\bulk\{i:D6}.dat"))
                .Concat(Enumerable.Range(0, 30)
                    .Select(i => (INode)new PathKeyNode($@"P:\share\{i:D3}.dat")))
                .Concat(Enumerable.Range(0, 30)
                    .Select(i => (INode)new PathKeyNode($@"S:\share\{i:D3}.dat")))
                .ToArray();
            Comparison<INode> folderDescending =
                (a, b) => NodePath.ByFolderThenName.Compare(b, a);

            var published = SearchModel.SelectTop(nodes, folderDescending, 100_000);

            Assert.Equal(100_000, published.Count);
            Assert.Equal(30, published.Count(n => n.FullName.StartsWith(@"P:\")));
            Assert.Equal(30, published.Count(n => n.FullName.StartsWith(@"S:\")));
            Assert.StartsWith(@"S:\", published[0].FullName);
        }

        [Fact]
        public void DriveReadinessProbeIsBoundedAndReportsSuccessfulReconnect()
        {
            Assert.True(DriveAvailability.ProbeWithTimeout(() => true, TimeSpan.FromMilliseconds(100)));
            Assert.False(DriveAvailability.ProbeWithTimeout(() => throw new IOException(),
                TimeSpan.FromMilliseconds(100)));
            Assert.False(DriveAvailability.ProbeWithTimeout(() =>
            {
                Thread.Sleep(100);
                return true;
            }, TimeSpan.FromMilliseconds(10)));
        }

        [Theory]
        [InlineData(DriveType.Fixed, "NTFS", true)]
        [InlineData(DriveType.Network, "NTFS", false)]
        [InlineData(DriveType.Fixed, "ReFS", false)]
        public void RawMftIsNeverAttemptedForMappedNetworkDrives(
            DriveType type, string format, bool expected)
            => Assert.Equal(expected, SearchModel.CanUseMftSource(type, format));

        [Theory]
        [InlineData(DriveType.Fixed, "NTFS", true)]
        [InlineData(DriveType.Removable, "NTFS", true)]
        [InlineData(DriveType.Network, "NTFS", false)]
        [InlineData(DriveType.Fixed, "ReFS", false)]
        public void OnlyNonNetworkNtfsDrivesAreSelectedByDefault(
            DriveType type, string format, bool expected)
            => Assert.Equal(expected, DriveSelectionStore.DefaultEnabled(type, format));

        [Fact]
        public void DriveDialogRefreshesOnlyRootsWhoseSelectionChanged()
        {
            var selection = new DriveSelection
            {
                Drives = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["C:"] = true,
                    ["P:"] = false,
                    ["S:"] = false
                }
            };

            var changed = DriveSelectionStore.ApplyChoices(selection, new[]
            {
                (Key: "C:", Root: @"C:\", Enabled: true),
                (Key: "P:", Root: @"P:\", Enabled: false),
                (Key: "S:", Root: @"S:\", Enabled: true)
            });

            Assert.Equal(new[] { @"S:\" }, changed);
            Assert.True(selection.Drives["C:"]);
            Assert.False(selection.Drives["P:"]);
            Assert.True(selection.Drives["S:"]);
        }

        [Theory]
        [InlineData("+Name", ListSortDirection.Ascending)]
        [InlineData("-Name", ListSortDirection.Descending)]
        [InlineData("+Size", ListSortDirection.Descending)]
        [InlineData("-Size", ListSortDirection.Ascending)]
        [InlineData("+Count", ListSortDirection.Descending)]
        [InlineData("-Count", ListSortDirection.Ascending)]
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
        public void MissingKeyboardFocusAfterEndIsBenign()
            => Assert.Null(MainWindow.FocusedItemFromElement(null, null));

        [Fact]
        public void ItemExchangeAlwaysRunsSelectionCleanup()
        {
            var calls = new List<string>();
            Assert.Throws<InvalidOperationException>(() => SearchModel.ExchangeItems(
                () => calls.Add("before"),
                () =>
                {
                    calls.Add("mutation");
                    throw new InvalidOperationException();
                },
                () => calls.Add("after")));
            Assert.Equal(new[] { "before", "mutation", "after" }, calls);

            calls.Clear();
            Assert.Throws<InvalidOperationException>(() => SearchModel.ExchangeItems(
                () =>
                {
                    calls.Add("before");
                    throw new InvalidOperationException();
                },
                () => calls.Add("mutation"),
                () => calls.Add("after")));
            Assert.Equal(new[] { "before", "after" }, calls);
        }

        [Theory]
        [InlineData(0, -1, -1, false, -1)]
        [InlineData(3, 1, -1, false, 1)] //First jump reveals the active selected row
        [InlineData(3, 1, -1, true, 1)]
        [InlineData(3, -1, -1, false, 0)] //Without an active row use the visual boundary
        [InlineData(3, -1, -1, true, 2)]
        [InlineData(3, -1, 1, false, 2)]
        [InlineData(3, -1, 2, false, 0)]  //Forward wrap
        [InlineData(3, -1, 0, true, 2)]   //Backward wrap
        public void SelectedItemJumpCyclesInCurrentVisualOrder(
            int selectedCount, int activeIndex, int lastJumpIndex, bool reverse, int expected)
            => Assert.Equal(expected, MainWindow.SelectionCycleTargetIndex(
                selectedCount, activeIndex, lastJumpIndex, reverse));

        [Fact]
        public void SelectedItemCycleRecoversItsCursorAfterGridPublication()
        {
            var first = new object();
            var lastVisited = new object();
            var third = new object();

            //The cached index 1 now contains a different row. Identity lookup must recover
            //the last visited row at its new index so the next jump continues from there.
            Assert.Equal(2, MainWindow.SelectionCycleItemIndex(
                new[] { third, first, lastVisited }, lastVisited, cachedIndex: 1));
            Assert.Equal(-1, MainWindow.SelectionCycleItemIndex(
                new[] { third, first }, lastVisited, cachedIndex: 1));
        }

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
                Assert.Equal(3U, logs.Count); // app.txt + sub + sub/b.txt
                Assert.NotNull(sub);
                Assert.Equal(10ul, sub.Size);
                Assert.Equal(1U, sub.Count);
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
