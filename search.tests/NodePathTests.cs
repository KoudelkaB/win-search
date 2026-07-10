using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class NodePathTests
    {
        static List<INode> Sample()
        {
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot();
            mft.AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
               .AddRecord(attributes: new[] { FakeMft.FileName(6, "a.txt"), FakeMft.ResidentData(1) })        // 7
               .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(6, "Sub") })                  // 8
               .AddRecord(attributes: new[] { FakeMft.FileName(8, "b.txt"), FakeMft.ResidentData(2) });       // 9
            return mft.Parse();
        }

        static INode ByName(List<INode> nodes, string name) => nodes.Single(n => n.Name == name);

        [Fact]
        public void NodesEqualTheirFullPathStrings()
        {
            foreach (var n in Sample())
            {
                Assert.True(NodePath.KeyComparer.Equals(n, n.FullName));
                Assert.True(NodePath.KeyComparer.Equals(n.FullName, n)); // symmetric
                Assert.True(NodePath.KeyComparer.Equals(n, n.FullName.ToUpperInvariant()));
                Assert.Equal(NodePath.KeyComparer.GetHashCode(n.FullName), NodePath.KeyComparer.GetHashCode(n));
                Assert.Equal(NodePath.KeyComparer.GetHashCode(n.FullName.ToLowerInvariant()), NodePath.KeyComparer.GetHashCode(n));
            }
        }

        [Fact]
        public void DictionaryKeyedByNodesIsQueriedByPathStrings()
        {
            var nodes = Sample();
            var dict = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            foreach (var n in nodes) dict[n] = n;

            Assert.True(dict.TryGetValue(@"Q:\Docs\Sub\b.txt", out var b));
            Assert.Equal("b.txt", b.Name);
            Assert.True(dict.TryGetValue(@"q:\DOCS\sub\B.TXT", out _)); // case-insensitive
            Assert.True(dict.TryGetValue(@"Q:\", out var root));
            Assert.Equal("Q:", root.Name);
            Assert.False(dict.TryGetValue(@"Q:\Docs\missing.txt", out _));
            Assert.False(dict.TryGetValue(@"Q:\Docs\Sub\b.txt.bak", out _));
        }

        [Fact]
        public void PathBackedAndChainedNodesWithTheSamePathAreEqual()
        {
            var chained = ByName(Sample(), "a.txt");
            var pathBacked = new FileNode(@"Q:\Docs\a.txt");

            Assert.True(NodePath.KeyComparer.Equals(chained, pathBacked));
            Assert.True(NodePath.KeyComparer.Equals(pathBacked, chained));
            Assert.Equal(NodePath.KeyComparer.GetHashCode(pathBacked), NodePath.KeyComparer.GetHashCode(chained));
            Assert.Equal(0, NodePath.ByPath.Compare(chained, pathBacked));
            Assert.Equal(0, NodePath.ByPath.Compare(pathBacked, chained));
        }

        [Fact]
        public void OrdersAsAComponentTree()
        {
            // Tree order: a folder groups with its content ("a\z" right after "a",
            // before "a!" and "ab" - a plain string sort would interleave them)
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot();
            mft.AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "a") }) // 6
               .AddRecord(attributes: new[] { FakeMft.FileName(6, "z") })                                  // 7
               .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "a!") })                 // 8
               .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "ab") });                // 9

            var sorted = mft.Parse().OrderBy(x => x, NodePath.ByPath).Select(n => n.FullName).ToArray();

            Assert.Equal(new[] { @"Q:\", @"Q:\a", @"Q:\a\z", @"Q:\a!", @"Q:\ab" }, sorted);
        }

        [Fact]
        public void MixedRepresentationsSortConsistently()
        {
            var nodes = Sample();
            var mixed = nodes.Concat(new INode[]
            {
                new FileNode(@"Q:\Docs\Sub\b.txt"), // duplicate path, different type
                new FileNode(@"Q:\Docs\m.txt"),
                new FileNode(@"Q:\zzz.txt")
            }).ToList();

            var sorted = mixed.OrderBy(x => x, NodePath.ByPath).Select(n => n.FullName).ToArray();

            Assert.Equal(new[]
            {
                @"Q:\", @"Q:\Docs", @"Q:\Docs\a.txt", @"Q:\Docs\m.txt",
                @"Q:\Docs\Sub", @"Q:\Docs\Sub\b.txt", @"Q:\Docs\Sub\b.txt", @"Q:\zzz.txt"
            }, sorted);
        }

        [Fact]
        public void FolderThenNameGroupsSiblings()
        {
            var nodes = Sample();
            var sorted = nodes.OrderBy(x => x, NodePath.ByFolderThenName).Select(n => n.FullName).ToArray();

            // Empty folder (the root) first, then Q:\ content, then Q:\Docs content, then Q:\Docs\Sub
            Assert.Equal(new[] { @"Q:\", @"Q:\Docs", @"Q:\Docs\a.txt", @"Q:\Docs\Sub", @"Q:\Docs\Sub\b.txt" }, sorted);
        }

        [Fact]
        public void IsUnderMatchesSubtrees()
        {
            var nodes = Sample();
            var docs = ByName(nodes, "Docs");
            var b = ByName(nodes, "b.txt");
            var root = nodes.Single(n => n.Name == "Q:");

            Assert.True(NodePath.IsUnder(b, docs, @"Q:\Docs\"));
            Assert.True(NodePath.IsUnder(ByName(nodes, "Sub"), docs, @"Q:\Docs\"));
            Assert.False(NodePath.IsUnder(docs, docs, @"Q:\Docs\")); // strictly under
            Assert.True(NodePath.IsUnder(b, root, @"Q:\"));

            // Path-backed nodes (zip entries, walked drives) match textually
            Assert.True(NodePath.IsUnder(new FileNode(@"Q:\Docs\arch.zip\inner.txt"), docs, @"Q:\Docs\"));
            Assert.False(NodePath.IsUnder(new FileNode(@"Q:\Other\x.txt"), docs, @"Q:\Docs\"));
        }

        [Fact]
        public void HasParentMatchesTheImmediateParentOnly()
        {
            var nodes = Sample();
            var docs = ByName(nodes, "Docs");

            Assert.True(NodePath.HasParent(ByName(nodes, "a.txt"), docs));
            Assert.True(NodePath.HasParent(ByName(nodes, "Sub"), docs));
            Assert.False(NodePath.HasParent(ByName(nodes, "b.txt"), docs)); // grandchild
            Assert.False(NodePath.HasParent(docs, docs));
        }

        [Fact]
        public void LeafAndComponentHelpersNeedNoFullPath()
        {
            var nodes = Sample();
            var a = ByName(nodes, "a.txt");

            Assert.True(NodePath.LeafEquals(a, "A.TXT"));
            Assert.False(NodePath.LeafEquals(a, "b.txt"));
            Assert.True(NodePath.LeafEndsWith(a, ".TXT"));
            Assert.False(NodePath.LeafEndsWith(a, ".exe"));

            Assert.True(NodePath.HasPathComponent(ByName(nodes, "b.txt"), "docs"));
            Assert.True(NodePath.HasPathComponent(ByName(nodes, "b.txt"), "SUB"));
            Assert.False(NodePath.HasPathComponent(a, "sub"));
            Assert.False(NodePath.HasPathComponent(a, "a.txt")); // the leaf itself is not a folder component

            var exe = new FileNode(@"C:\src\Debug\tool.exe");
            Assert.True(NodePath.LeafEquals(exe, "TOOL.EXE"));
            Assert.False(NodePath.LeafEquals(exe, "ol.exe")); // suffix but not a whole component
            Assert.True(NodePath.HasPathComponent(exe, "debug"));
            Assert.False(NodePath.HasPathComponent(exe, "release"));
        }

        [Fact]
        public void MaterializeEqualsFullName()
        {
            foreach (var n in Sample())
                Assert.Equal(n.FullName, NodePath.Materialize(n));
        }
    }
}
