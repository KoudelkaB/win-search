using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class NodeFilterTests : IDisposable
    {
        readonly List<INode> nodes;

        public NodeFilterTests()
        {
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot();
            mft.AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
               .AddRecord(attributes: new[] { FakeMft.FileName(6, "a.txt") })                                 // 7
               .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(6, "Sub") })                  // 8
               .AddRecord(attributes: new[] { FakeMft.FileName(8, "b.txt") })                                 // 9
               .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "c.txt") });                // 10
            nodes = mft.Parse();

            // Stand-in for the SearchModel files index
            var index = new ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            foreach (var n in nodes) index[n] = n;
            NodeFilter.Resolve = path =>
                index.TryGetValue(path, out var n) ? n
                : path.Length > 0 && path[^1] != '\\' && index.TryGetValue(path + '\\', out n) ? n
                : null;
        }

        public void Dispose() => NodeFilter.Resolve = SearchModel.FindByPath;

        string[] Matching(string filter)
            => nodes.Where(new NodeFilter(filter).Matches).Select(n => n.Name)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        [Fact]
        public void NameFilterMatchesLeafNames()
        {
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Matching(".txt:"));
            Assert.Equal(new[] { "a.txt" }, Matching("a.txt"));
        }

        [Fact]
        public void ParentsFilterMatchesAnyPathComponentWithoutBuildingPaths()
        {
            // "docs\\" = the term must appear anywhere in the full path
            Assert.Equal(new[] { "a.txt", "b.txt", "Docs", "Sub" }, Matching(@"docs\\"));
            Assert.Equal(new[] { "b.txt", "Sub" }, Matching(@"sub\\"));
            Assert.Empty(Matching(@"nowhere\\"));
        }

        [Fact]
        public void AnchoredParentsPatternFallsBackToTheFullPath()
        {
            // ".txt:" anchored at the end => must match the materialized full path
            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, Matching(@".txt:\\"));
        }

        [Fact]
        public void DirFilterMatchesDirectChildrenOnly()
        {
            Assert.Equal(new[] { "a.txt", "Sub" }, Matching(@"Q:\Docs"));
            Assert.Equal(new[] { "b.txt" }, Matching(@"Q:\Docs\Sub"));
        }

        [Fact]
        public void RecursiveDirFilterMatchesTheWholeSubtree()
        {
            Assert.Equal(new[] { "a.txt", "b.txt", "Sub" }, Matching(@"Q:\Docs\\"));
        }

        [Fact]
        public void DirCombinedWithNameFilterSearchesTheSubtree()
        {
            Assert.Equal(new[] { "a.txt", "b.txt" }, Matching(@"Q:\Docs .txt:"));
            Assert.Equal(new[] { "b.txt" }, Matching(@"Q:\Docs b.txt"));
        }

        [Fact]
        public void UnknownDirMatchesNothingForIndexedNodes()
        {
            Assert.Empty(Matching(@"Q:\Missing"));
            Assert.Empty(Matching(@"Q:\Missing\\"));
        }

        [Fact]
        public void PathBackedNodesKeepTextualDirSemantics()
        {
            // A zip entry is not in the index - it must still match its textual location
            var entry = new FileNode(@"Q:\Docs\arch.zip\inner.txt");
            Assert.True(new NodeFilter(@"Q:\Docs\\").Matches(entry));
            Assert.True(new NodeFilter(@"Q:\Docs\arch.zip").Matches(entry));
            Assert.False(new NodeFilter(@"Q:\Docs").Matches(entry)); // not a direct child
            Assert.False(new NodeFilter(@"Q:\Other\\").Matches(entry));
        }

        [Fact]
        public void RootDirFilterMatchesRootChildren()
        {
            Assert.Equal(new[] { "c.txt", "Docs" }, Matching(@"Q:\"));
        }
    }
}
