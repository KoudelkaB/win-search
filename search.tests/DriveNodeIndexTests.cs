using System;
using System.Collections.Generic;
using System.IO;
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
    }
}
