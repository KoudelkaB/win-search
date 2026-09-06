using System;
using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class HardLinkSearchTests
    {
        [Fact]
        public void EdgeNamesInBaseAndExtensionsAreAllSearchable()
        {
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(5, "EdgeCore") })
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(5, "EdgeWebView") })
                .AddRecord(attributes: new[] {
                    FakeMft.FileName(6, "msedge.dll"),
                    FakeMft.FileName(6, "another-name.dll"),
                    FakeMft.ResidentData(100) })
                .AddRecord(baseReference: 8, attributes: new[] {
                    FakeMft.FileName(7, "msedge.dll"),
                    FakeMft.FileName(7, "MSEDGE~1.DLL", ns: 2) });
            using var stream = new MemoryStream(mft.Image());
            var source = Assert.IsAssignableFrom<IFrnNodeSource>(MftDriveReader.GetNodes(stream,
                1024, mft.Count * 1024L, FakeMft.Root, chunkBytes: 1024));
            var index = new DriveNodeIndex.CompactPathIndex(source.DenseNodes);
            Assert.Equal(new[] { @"Q:\EdgeCore\msedge.dll", @"Q:\EdgeWebView\msedge.dll" },
                index.Where(new NodeFilter("msedge.dll").Matches).Select(n => n.FullName).OrderBy(p => p));
            Assert.True(index.TryGetValue(@"Q:\EdgeCore\another-name.dll", out var alias));
            Assert.Equal(100UL, alias.Size);
            Assert.Equal(3, source.GetFileLinks(alias.Frn).Count);
            Assert.Equal(300UL, source.Single(n => n.Name == "Q:").Size);
            Assert.Equal(5U, source.Single(n => n.Name == "Q:").Count);
            var map = new UsnDriveWatcher.FrnMap();
            map.Populate(source);
            Assert.Equal(3, map.GetLinkPaths(alias.Frn).Length);
            map.RemapDirectory(source.Single(n => n.Name == "EdgeWebView").Frn,
                @"Q:\EdgeWebView", @"Q:\Moved");
            Assert.Contains(@"Q:\Moved\msedge.dll", map.GetLinkPaths(alias.Frn));
            map.Remove(alias.Frn);
            Assert.Empty(map.GetLinkPaths(alias.Frn));
            Assert.False(map.TryGetLinkState(alias.Frn, out _, out _));
            var withoutNames = new UsnDriveWatcher.FrnMap();
            withoutNames.Populate(source);
            withoutNames.SetLinkState(alias.Frn, new ulong[] { 6 }, 100);
            Assert.Empty(withoutNames.GetLinkPaths(alias.Frn));
        }

        [Fact]
        public void SurvivingLinkRescuesAFileWithAStaleCanonicalParent()
        {
            var nodes = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(directory: true, sequence: 3, attributes: new[] { FakeMft.FileName(5, "Live") })
                .AddRecord(attributes: new[] {
                    FakeMft.FileName(6 | (2UL << 48), "stale.bin"),
                    FakeMft.FileName(6 | (3UL << 48), "live.bin"), FakeMft.ResidentData(30) })
                .Parse();
            Assert.Equal(@"Q:\Live\live.bin", Assert.Single(nodes, n => !n.IsDirectory).FullName);
            Assert.Equal(30UL, nodes.Single(n => n.Name == "Q:").Size);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RejectedRenameRepairsNamesAndAggregatesFromCurrentRows(bool pathOnlyRows)
        {
            var index = new DriveNodeIndex();
            var snapshot = new NodeMetadataSnapshot(false, 100, DateTime.Now);
            var original = FileNode.Create(@"Q:\old.dll", snapshot, 42);
            var current = FileNode.Create(original.FullName, snapshot, pathOnlyRows ? 0UL : 42UL);
            index.GetOrAdd(current.FullName, current); //A scan or reconcile replaced the instance.
            var freshPath = @"Q:\new.dll";
            var change = FsEvent.HardLinkUpdate(original.FullName, 42, original,
                snapshot, new[] { new HardLinkParentDelta(@"Q:\", 999, 999) },
                new[] { original.FullName }, new[] { freshPath });
            Assert.False(SearchModel.CanApplyHardLinkDelta(index, change));
            long bytes = 100, count = 1;
            SearchModel.ReconcileHardLinkRows(index, change,
                path => (true, path == freshPath ? snapshot with { Size = 200 } : null, 42UL),
                (_, size, entries) => { bytes += size; count += entries; }, (_, _) => { });
            Assert.False(index.ContainsKey(original.FullName));
            Assert.Equal(200UL, Assert.Single(index).Value.Size);
            Assert.True(index.ContainsKey(freshPath));
            Assert.Equal(200, bytes);
            Assert.Equal(1, count);
            var repaired = Assert.Single(index).Value;
            Assert.Equal(42UL, repaired.Frn);
            var next = FsEvent.HardLinkUpdate(freshPath, 42, repaired, snapshot,
                Array.Empty<HardLinkParentDelta>(), new[] { freshPath }, new[] { freshPath });
            Assert.True(SearchModel.CanApplyHardLinkDelta(index, next));
        }

        [Fact]
        public void UnknownAliasRequiresReconciliationAndInaccessibleRowsSurviveIt()
        {
            var index = new DriveNodeIndex();
            var snapshot = new NodeMetadataSnapshot(false, 100, DateTime.Now);
            var canonical = FileNode.Create(@"Q:\canonical.dll", snapshot, 42);
            var alias = FileNode.Create(@"Q:\alias.dll", snapshot);
            index.GetOrAdd(canonical.FullName, canonical);
            index.GetOrAdd(alias.FullName, alias);
            var paths = new[] { canonical.FullName, alias.FullName };
            var change = FsEvent.HardLinkUpdate(canonical.FullName, 42, canonical,
                snapshot, Array.Empty<HardLinkParentDelta>(), paths, paths);
            Assert.False(SearchModel.CanApplyHardLinkDelta(index, change));
            Assert.Equal("affected path FRN unknown", SearchModel.HardLinkDeltaRejection(index, change));
            SearchModel.ReconcileHardLinkRows(index, change,
                path => path == alias.FullName ? (true, snapshot with { Size = 300 }, 42UL) : (false, null, 0UL),
                (_, _, _) => { }, (_, _) => { });
            Assert.Same(canonical, index.Single(pair => pair.Value.FullName == canonical.FullName).Value);
            Assert.Equal(300UL, index.Single(pair => pair.Value.FullName == alias.FullName).Value.Size);
            Assert.True(SearchModel.CanApplyHardLinkDelta(index, change));
        }

        [Fact]
        public void ReconcileReadsReplacementIdentityInsteadOfReusingQueuedFrn()
        {
            var root = Path.Combine(Path.GetTempPath(), $"win-search-frn-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "file.txt");
                File.WriteAllText(path, "original");
                var original = SearchModel.ReadHardLinkPath(path);
                Assert.True(original.Known);
                Assert.NotEqual(0UL, original.Frn);
                var node = FileNode.Create(path, original.Snapshot.Value, original.Frn);
                var index = new DriveNodeIndex();
                index.GetOrAdd(path, node);
                var change = FsEvent.HardLinkUpdate(path, original.Frn, node, original.Snapshot.Value,
                    Array.Empty<HardLinkParentDelta>(), new[] { path }, new[] { path });
                File.Move(path, Path.Combine(root, "old.txt"));
                File.WriteAllText(path, "replacement contents");
                SearchModel.ReconcileHardLinkRows(index, change, SearchModel.ReadHardLinkPath,
                    (_, _, _) => { }, (_, _) => { });
                var repaired = Assert.Single(index).Value;
                Assert.NotEqual(original.Frn, repaired.Frn);
                Assert.NotEqual(0UL, repaired.Frn);
                Assert.Equal((ulong)new FileInfo(path).Length, repaired.Size);
                Assert.Equal("canonical identity replaced", SearchModel.HardLinkDeltaRejection(index, change));
                index.TryRemove(path, out _);
                Assert.Equal("canonical path missing", SearchModel.HardLinkDeltaRejection(index, change));
                index.GetOrAdd(path, FileNode.Create(path, original.Snapshot.Value));
                Assert.Equal("canonical FRN unknown", SearchModel.HardLinkDeltaRejection(index, change));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void LiveTopologyUpdatesAddRefreshAndRemoveEverySearchRow()
        {
            var index = new DriveNodeIndex();
            var snapshot = new NodeMetadataSnapshot(false, 100, DateTime.Now);
            var canonical = FileNode.Create(@"Q:\first.dll", snapshot, 42);
            index.GetOrAdd(canonical.FullName, canonical);
            void Apply(string[] oldPaths, string[] currentPaths, ulong size)
            {
                var change = FsEvent.HardLinkUpdate(canonical.FullName, 42, canonical,
                    snapshot with { Size = size }, Array.Empty<HardLinkParentDelta>(), oldPaths, currentPaths);
                SearchModel.ApplyHardLinkRows(index, change, (_, _) => { }, _ => { });
            }
            var both = new[] { canonical.FullName, @"Q:\second.dll" };
            Apply(new[] { canonical.FullName }, both, 100);
            Assert.Equal(2, index.Count());
            Apply(both, both, 200);
            Assert.All(index, row => Assert.Equal(200UL, row.Value.Size));
            Apply(both, new[] { both[1] }, 200);
            Assert.False(index.ContainsKey(both[0]));
            Assert.True(index.ContainsKey(both[1]));
            // A stale notification cannot remove a replacement with a different FRN.
            index.TryRemove(both[1], out _);
            var replacement = FileNode.Create(both[1], snapshot, 99);
            index.GetOrAdd(both[1], replacement);
            Apply(new[] { both[1] }, Array.Empty<string>(), 0);
            Assert.Same(replacement, Assert.Single(index).Value);
        }
    }
}
