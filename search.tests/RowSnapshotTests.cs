using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The row-based query paths must produce exactly what the node-based paths produced:
    /// same membership after live overlay changes, same filter hits, same sort windows -
    /// while creating handles only for the rows they return.
    /// </summary>
    public class RowSnapshotTests
    {
        static MftTable Table(int files = 40)
        {
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") })   // 6
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(6, "Sub") })                    // 7
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Other") });  // 8
            var rnd = new Random(7);
            for (var i = 0; i < files; i++)
            {
                var parent = (ulong)(6 + i % 3);
                var modified = new DateTime(2020, 1, 1).AddMinutes(rnd.Next(0, 500_000));
                mft.AddRecord(attributes: new[]
                {
                    FakeMft.FileName(parent, $"file{i:D3}.{(i % 2 == 0 ? "txt" : "log")}"),
                    FakeMft.StandardInfo(modified, modified, modified),
                    FakeMft.NonResidentData((ulong)rnd.Next(1, 900_000))
                });
            }
            using var stream = new MemoryStream(mft.Image());
            return Assert.IsType<MftTable>(MftDriveReader.GetNodes(stream, mft.BytesPerRecord,
                (long)mft.Count * mft.BytesPerRecord, FakeMft.Root));
        }

        static DriveNodeIndex IndexOf(MftTable table)
        {
            var index = new DriveNodeIndex();
            index.ReplaceDrive(FakeMft.Root, DriveNodeIndex.PrepareDrive(table));
            return index;
        }

        [Fact]
        public void PreparingATableCreatesNoHandles()
        {
            var table = Table();
            var prepared = DriveNodeIndex.PrepareDrive(table);

            Assert.Same(table, prepared.Table);
            Assert.Equal(table.Count, prepared.Count);
            Assert.Equal(0, table.MaterializedHandles);
            Assert.True(prepared.Base.TryGetValue(@"Q:\Docs\Sub\file001.log", out var hit));
            Assert.Equal("file001.log", hit.Name);
            Assert.Equal(1, table.MaterializedHandles);
        }

        [Fact]
        public void SnapshotHidesOverlayShadowedRowsAndAppendsAdditions()
        {
            var table = Table();
            var index = IndexOf(table);
            var removedPath = @"Q:\Docs\file000.txt";
            var replacedPath = @"Q:\Other\file002.txt";
            Assert.True(index.TryRemove(removedPath, out var removed));
            var replacement = new FileNode(replacedPath, new NodeMetadataSnapshot(false, 5, DateTime.Now));
            index[replacedPath] = replacement;
            var added = new FileNode(@"Q:\Docs\new.txt", new NodeMetadataSnapshot(false, 1, DateTime.Now));
            index[added.FullName] = added;

            var snapshot = index.BuildSnapshot();

            Assert.Equal(table.Count, snapshot.Count); //-1 removed -1 replaced +1 replacement +1 added
            var all = snapshot.ToList();
            Assert.DoesNotContain(removed, all);
            Assert.DoesNotContain(all, n => n.FullName.Equals(replacedPath, StringComparison.OrdinalIgnoreCase) && n is MftNode);
            Assert.Contains(replacement, all);
            Assert.Contains(added, all);
            Assert.Equal(all.Count, all.Distinct().Count());
            //Positional access agrees with enumeration and skips hidden rows in O(log hidden)
            for (var i = 0; i < snapshot.Count; i++) Assert.Same(all[i], snapshot[i]);
            //The same membership the classic materialized merge produces
            var expected = index.Select(pair => pair.Value).OrderBy(n => n.FullName).ToArray();
            Assert.Equal(expected, all.OrderBy(n => n.FullName).ToArray());
        }

        [Fact]
        public void SegmentIndexMappingSkipsHiddenIndexes()
        {
            var segment = new DriveNodeIndex.Snapshot.Segment(null,
                Enumerable.Range(0, 10).Select(i => (INode)new FileNode($@"Q:\{i}")).ToArray(), new[] { 0, 3, 4, 9 });
            Assert.Equal(6, segment.Count);
            Assert.Equal(new[] { 1, 2, 5, 6, 7, 8 }, Enumerable.Range(0, 6).Select(segment.IndexAt).ToArray());
        }

        [Fact]
        public void FilterSweepsRowsAndMaterializesOnlyMatches()
        {
            var table = Table(60);
            var index = IndexOf(table);
            var live = new FileNode(@"Q:\Docs\live.txt", new NodeMetadataSnapshot(false, 1, DateTime.Now));
            index[live.FullName] = live;
            Assert.True(index.TryRemove(@"Q:\Docs\file003.log", out _));
            var before = table.MaterializedHandles;
            var filter = new NodeFilter(".txt:");

            var hits = index.FilterSnapshot(filter.Matches, filter.Matches, null);

            //Only the matching rows got a handle (the full enumeration below creates the rest)
            Assert.Equal(before + hits.Count(n => n is MftNode), table.MaterializedHandles);
            var expected = index.Select(pair => pair.Value).Where(filter.Matches).OrderBy(n => n.FullName).ToArray();
            Assert.Equal(expected, hits.OrderBy(n => n.FullName).ToArray());
            Assert.Contains(live, hits);

            var subtree = new NodeFilter(@"Q:\Docs\\ .log:");
            Assert.Equal(index.Select(pair => pair.Value).Where(subtree.Matches).OrderBy(n => n.FullName).ToArray(),
                index.FilterSnapshot(subtree.Matches, subtree.Matches, null).OrderBy(n => n.FullName).ToArray());
            var direct = new NodeFilter(@"Q:\Docs");
            Assert.Equal(index.Select(pair => pair.Value).Where(direct.Matches).OrderBy(n => n.FullName).ToArray(),
                index.FilterSnapshot(direct.Matches, direct.Matches, null).OrderBy(n => n.FullName).ToArray());
        }

        [Theory]
        [InlineData("+" + nameof(INode.LastChangeTime))]
        [InlineData("-" + nameof(INode.LastChangeTime))]
        [InlineData("+" + nameof(INode.Size))]
        [InlineData("-" + nameof(INode.Size))]
        [InlineData("+" + nameof(INode.Count))]
        [InlineData("-" + nameof(INode.Count))]
        [InlineData("+" + nameof(INode.Name))]
        [InlineData("-" + nameof(INode.Name))]
        [InlineData("+" + nameof(INode.FullName))]
        [InlineData("-" + nameof(INode.Folder))]
        public void RowSortsProduceTheSameWindowAsNodeSorts(string sort)
        {
            var table = Table(300);
            var index = IndexOf(table);
            var live = new FileNode(@"Q:\Other\zzz-live.txt", new NodeMetadataSnapshot(false, 123, new DateTime(2021, 5, 5)));
            index[live.FullName] = live;
            var snapshot = index.BuildSnapshot();
            var model = SearchModelSortAccess.Comparison(sort);
            var key = SearchModelSortAccess.ScalarKey(sort);
            const int limit = 20;

            var window = SearchModel.SelectTop(snapshot, model, limit, null, key, SearchModel.ParseSort(sort));
            var naive = snapshot.ToList().OrderBy(x => x, Comparer<INode>.Create(model)).Take(limit).ToList();

            Assert.Equal(naive.Count, window.Count);
            //Equal keys may order differently; compare the key sequence, not identities
            for (var i = 0; i < naive.Count; i++)
                Assert.Equal(0, model(naive[i], window[i]));
        }

        [Fact]
        public void RetiredTableDetachesItsHandlesAndBecomesCollectable()
        {
            var (weak, file, root) = RetireScenario();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(weak.TryGetTarget(out _)); //Nothing but the handles survived, and they let go
            Assert.Equal(@"Q:\Docs\Sub\file001.log", file.FullName);
            GC.KeepAlive(file);
            GC.KeepAlive(root);
        }

        //Separate frame: a Debug JIT keeps locals alive to the end of their method
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static (WeakReference<MftTable> Weak, MftNode File, MftNode Root) RetireScenario()
        {
            var table = Table(20);
            var weak = new WeakReference<MftTable>(table);
            var file = (MftNode)table.DenseNodes.Single(n => n.Name == "file001.log"); // Q:\Docs\Sub\file001.log
            var root = (MftNode)table.DenseNodes.Single(n => n.Name == "Q:");
            var parentBefore = file.PathParent;
            var expectedSize = file.Size;
            var expectedTime = file.LastChangeTime;
            var expectedFrn = file.Frn;
            var expectedHash = NodePath.KeyComparer.GetHashCode(file);

            table.Retire();

            Assert.False(file.IsAttached);
            Assert.Null(file.Table);
            Assert.True(table.IsRetired);
            //A handle handed out after retirement never pins the table
            Assert.False(table.Handle(3).IsAttached);
            //Everything the handle knew is still there, including its chain and identity
            Assert.Equal("file001.log", file.Name);
            Assert.Equal(@"Q:\Docs\Sub\file001.log", file.FullName);
            Assert.Same(parentBefore, file.PathParent);
            Assert.Equal(@"Q:\Docs\Sub", file.PathParent.FullName);
            Assert.Equal("Sub", file.ParentName);
            Assert.Equal(expectedSize, file.Size);
            Assert.Equal(expectedTime, file.LastChangeTime);
            Assert.Equal(expectedFrn, file.Frn);
            Assert.Equal(expectedHash, NodePath.KeyComparer.GetHashCode(file));
            Assert.True(NodePath.KeyComparer.Equals(file, @"Q:\Docs\Sub\file001.log"));
            Assert.True(NodePath.IsUnder(file, root, @"Q:\"));
            Assert.Equal(@"Q:\", root.FullName);
            Assert.True(new NodeFilter(@"Q:\Docs\\ .log:").Matches(file));
            //Mutations through a detached handle keep working
            file.AddSizeDelta(10);
            Assert.Equal(expectedSize + 10, file.Size);
            return (weak, file, root);
        }

        [Fact]
        public void PublishingANewScanRetiresTheOldTableButKeepsPreservedHandlesUsable()
        {
            var old = Table(20);
            var index = new DriveNodeIndex();
            index.ReplaceDrive(FakeMft.Root, DriveNodeIndex.PrepareDrive(old));
            var watermark = index.BeginSnapshot(FakeMft.Root);
            //A live change after the watermark on an old row: it survives the publication
            Assert.True(index.TryGetValue(@"Q:\Docs\Sub\file001.log", out var touched));
            touched.AddSizeDelta(5);
            index.Touch(touched, touched);
            var fresh = Table(20);

            var superseded = index.ReplaceDrive(FakeMft.Root, DriveNodeIndex.PrepareDrive(fresh), watermark, _ => true);

            Assert.Same(old, superseded);
            superseded.Retire();
            Assert.True(index.TryGetValue(@"Q:\Docs\Sub\file001.log", out var current));
            Assert.Same(touched, current); //The preserved delta still wins ...
            Assert.False(((MftNode)current).IsAttached); //... without pinning the old table
            Assert.Equal(@"Q:\Docs\Sub\file001.log", current.FullName);
            //And the snapshot lists it once, in place of the fresh row
            var snapshot = index.BuildSnapshot();
            Assert.Equal(fresh.Count, snapshot.Count);
            Assert.Equal(1, snapshot.Count(n => n.FullName == @"Q:\Docs\Sub\file001.log"));
            Assert.Contains(touched, snapshot);
            Assert.Null(index.ReplaceDrive(FakeMft.Root, DriveNodeIndex.PrepareDrive(fresh))); //Same table => nothing superseded
        }

        [Fact]
        public void PatchKeepsRowsHiddenAndAdditionsUnique()
        {
            var table = Table(30);
            var index = IndexOf(table);
            var snapshot = index.BuildSnapshot();
            var gone = snapshot.First(n => n.Name == "file005.log");
            var extra = new FileNode(@"Q:\extra.txt", new NodeMetadataSnapshot(false, 1, DateTime.Now));
            var alreadyThere = snapshot.First(n => n.Name == "file006.txt");

            var patched = snapshot.Patch(new HashSet<INode>(new[] { gone }, ReferenceEqualityComparer.Instance),
                new[] { extra, alreadyThere, extra });

            Assert.Equal(snapshot.Count, patched.Count); //-1 +1
            Assert.DoesNotContain(gone, patched);
            Assert.Contains(extra, patched);
            Assert.Equal(1, patched.Count(n => ReferenceEquals(n, alreadyThere)));
            Assert.Equal(1, patched.Count(n => ReferenceEquals(n, extra)));
        }
    }

    /// <summary>Reaches SearchModel's private sort helpers the same way GetItems builds them.</summary>
    static class SearchModelSortAccess
    {
        public static Comparison<INode> Comparison(string sort)
        {
            var up = sort[0] == '+';
            Comparison<INode> key;
            bool ascending;
            switch (sort.Substring(1))
            {
                case nameof(INode.Name): key = SearchModel.CompareNames; ascending = up; break;
                case nameof(INode.Size): key = (a, b) => a.Size.CompareTo(b.Size); ascending = !up; break;
                case nameof(INode.Count): key = (a, b) => a.Count.CompareTo(b.Count); ascending = !up; break;
                case nameof(INode.LastChangeTime): key = (a, b) => a.LastChangeTime.CompareTo(b.LastChangeTime); ascending = !up; break;
                case nameof(INode.FullName): key = NodePath.ByPath.Compare; ascending = up; break;
                case nameof(INode.Folder): key = NodePath.ByFolderThenName.Compare; ascending = up; break;
                default: throw new ArgumentException(sort);
            }
            return ascending ? key : (a, b) => key(b, a);
        }

        public static Func<INode, ulong> ScalarKey(string sort)
        {
            Func<INode, ulong> key = sort.Substring(1) switch
            {
                nameof(INode.Size) => n => n.Size,
                nameof(INode.Count) => n => n.Count,
                nameof(INode.LastChangeTime) => n => (ulong)Math.Max(0, n.LastChangeTime.Ticks),
                _ => null
            };
            if (key == null) return null;
            return sort[0] == '+' ? n => ulong.MaxValue - key(n) : key;
        }
    }
}
