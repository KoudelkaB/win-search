using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class DriveNodeIndexTests
    {
        sealed class TestNode : INode
        {
            readonly string path;

            public TestNode(string path) => this.path = path;
            public override string FullName => path;
            public override string Name => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            public override FileAttributes Attributes { get; protected set; }
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }

        static NonBlocking.ConcurrentDictionary<object, INode> Map(params INode[] nodes)
        {
            var result = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            foreach (var node in nodes) result[node] = node;
            return result;
        }

        [Fact]
        public void FreshSingleDriveSnapshotReusesTheMftDenseArray()
        {
            var root = new TestNode(@"C:\");
            var file = new TestNode(@"C:\one.txt");
            IReadOnlyList<INode> dense = new INode[] { root, file };
            var index = new DriveNodeIndex();

            index.ReplaceDrive(@"C:\", Map(root, file), dense);

            Assert.Equal(2, index.Count);
            Assert.True(index.TryGetValue(@"C:\one.txt", out var found));
            Assert.Same(file, found);
            Assert.True(index.TryGetDenseSnapshot(out var snapshot));
            Assert.Same(dense, snapshot);
        }

        [Fact]
        public void MembershipChangeInvalidatesDenseSnapshot()
        {
            var root = new TestNode(@"C:\");
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", Map(root), new INode[] { root });

            var added = new TestNode(@"C:\new.txt");
            Assert.Same(added, index.GetOrAdd(added, added));

            Assert.False(index.TryGetDenseSnapshot(out _));
            Assert.True(index.TryGetValue(@"C:\new.txt", out var found));
            Assert.Same(added, found);
        }

        [Fact]
        public void CompactBaseAndDeltaExposeOneLogicalEntryPerPath()
        {
            var original = new TestNode(@"C:\one.txt");
            IReadOnlyList<INode> dense = new INode[] { original };
            var prepared = DriveNodeIndex.PrepareDrive(dense, dense);
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", prepared);

            var replacement = new TestNode(@"C:\one.txt");
            index[replacement.FullName] = replacement;

            Assert.Equal(1, index.Count);
            Assert.True(index.TryGetValue(original, out var found));
            Assert.Same(replacement, found);
            Assert.Single(index);
            Assert.Same(replacement, Assert.Single(index).Value);
        }

        [Fact]
        public void RemovingTransientAdditionRestoresZeroCopyDenseSnapshot()
        {
            var original = new TestNode(@"C:\one.txt");
            IReadOnlyList<INode> dense = new INode[] { original };
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", DriveNodeIndex.PrepareDrive(dense, dense));
            var added = new TestNode(@"C:\two.txt");
            index.GetOrAdd(added, added);

            Assert.True(index.TryRemove(added.FullName, out var removed));
            Assert.Same(added, removed);
            Assert.True(index.TryGetDenseSnapshot(out var restored));
            Assert.Same(dense, restored);
            Assert.Equal(1, index.Count);
        }

        [Fact]
        public void CopySnapshotMergesBaseReplacementsRemovalsAndAdditions()
        {
            var keep = new TestNode(@"C:\keep.txt");
            var remove = new TestNode(@"C:\remove.txt");
            var replace = new TestNode(@"C:\replace.txt");
            IReadOnlyList<INode> dense = new INode[] { keep, remove, replace };
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", DriveNodeIndex.PrepareDrive(dense, dense));
            var replacement = new TestNode(replace.FullName);
            var added = new TestNode(@"C:\added.txt");

            Assert.True(index.TryRemove(remove.FullName, out _));
            index[replace.FullName] = replacement;
            index[added.FullName] = added;
            var snapshot = index.CopySnapshot(() => false);

            Assert.Equal(3, snapshot.Count);
            Assert.Contains(keep, snapshot);
            Assert.Contains(replacement, snapshot);
            Assert.Contains(added, snapshot);
            Assert.DoesNotContain(remove, snapshot);
            Assert.DoesNotContain(replace, snapshot);
        }

        [Fact]
        public void CompactBaseResolvesProbeChainsAndMisses()
        {
            var nodes = Enumerable.Range(0, 5000)
                .Select(i => (INode)new TestNode($@"C:\group{i % 37}\file{i}.bin"))
                .ToArray();
            var prepared = DriveNodeIndex.PrepareDrive(nodes, nodes);

            Assert.Equal(nodes.Length, prepared.Count);
            foreach (var node in nodes)
            {
                Assert.True(prepared.Base.TryGetValue(node.FullName, out var found));
                Assert.Same(node, found);
            }
            for (var i = 0; i < 1000; i++)
                Assert.False(prepared.Base.TryGetValue($@"C:\missing\file{i}.bin", out _));
        }

        [Fact]
        public void CompactBaseKeepsOneEntryAndLatestValueForDuplicatePath()
        {
            var first = new TestNode(@"C:\same.txt");
            var latest = new TestNode(@"C:\same.txt");
            var prepared = DriveNodeIndex.PrepareDrive(new INode[] { first, latest });

            Assert.Equal(1, prepared.Count);
            Assert.True(prepared.Base.TryGetValue(first.FullName, out var found));
            Assert.Same(latest, found);
            Assert.Null(prepared.DenseNodes);
        }

        [Fact]
        public void CompactBaseBuildIsCancellable()
        {
            var nodes = new INode[] { new TestNode(@"C:\one.txt") };
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                DriveNodeIndex.PrepareDrive(nodes, nodes, canceled.Token));
        }

        [Fact]
        public void ReplacingOneDriveKeepsTheOtherDriveAndDropsOldEntries()
        {
            var oldC = new TestNode(@"C:\old.txt");
            var newC = new TestNode(@"C:\new.txt");
            var d = new TestNode(@"D:\kept.txt");
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", Map(oldC));
            index.ReplaceDrive(@"D:\", Map(d));

            index.ReplaceDrive(@"C:\", Map(newC));

            Assert.False(index.TryGetValue(oldC, out _));
            Assert.True(index.TryGetValue(newC, out _));
            Assert.True(index.TryGetValue(d, out _));

            index.ReplaceDrive(@"C:\", Map());
            Assert.False(index.TryGetValue(newC, out _));
            Assert.True(index.TryGetValue(d, out _));
            Assert.Equal(1, index.Count);
        }

        [Fact]
        public void RemovalHookRunsWhileTheEntryAndItsShardAreStillPublished()
        {
            var root = new TestNode(@"C:\");
            var parent = new TestNode(@"C:\Users");
            var file = new TestNode(@"C:\Users\probe.bin");
            var index = new DriveNodeIndex();
            index.ReplaceDrive(@"C:\", Map(root, parent, file));
            var observedFile = false;
            var observedParent = false;

            Assert.True(index.TryRemove(file.FullName, current =>
            {
                observedFile = index.TryGetValue(file.FullName, out var indexedFile)
                    && ReferenceEquals(indexedFile, current);
                observedParent = index.TryGetValue(parent.FullName, out var indexedParent)
                    && ReferenceEquals(indexedParent, parent);
            }, out var removed));

            Assert.True(observedFile);
            Assert.True(observedParent);
            Assert.Same(file, removed);
            Assert.False(index.TryGetValue(file.FullName, out _));
            Assert.True(index.TryGetValue(parent.FullName, out _));
        }
    }
}
