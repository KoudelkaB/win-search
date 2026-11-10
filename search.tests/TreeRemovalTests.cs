using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A deleted folder leaves the index with everything below it. The pass that finds what
    /// is below it, and the batching of the deletes that share one pass.
    /// </summary>
    public class TreeRemovalTests
    {
        static readonly string Root = OperatingSystem.IsWindows() ? @"Q:\" : "/";
        static readonly NodeMetadataSnapshot FileState = new(false, 10, new DateTime(2026, 11, 9));
        static readonly NodeMetadataSnapshot FolderState = new(true, 0, new DateTime(2026, 11, 9));

        static string P(params string[] parts) => Root + string.Join(Path.DirectorySeparatorChar, parts);

        /// <summary>A node named by its parent chain, as a scan of the master file table makes them</summary>
        sealed class ChainNode : INode
        {
            readonly INode parent;
            readonly string name;

            public ChainNode(INode parent, string name, bool directory = false)
            {
                this.parent = parent;
                this.name = name;
                Attributes = directory ? FileAttributes.Directory : 0;
            }

            public override INode PathParent => parent;
            public override string Name => name;
            public override string FullName => NodePath.Materialize(this);
            public override FileAttributes Attributes { get; protected set; }
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
        }

        static FsEvent Deleted(string path) => new(WatcherChangeTypes.Deleted, path);
        static FsEvent State(string path, FolderContent content = FolderContent.Unchanged)
            => FsEvent.ServiceState(path, FolderState, content);

        [Fact]
        public void ADirectoryCoversItselfAndWhatIsBelowIt()
        {
            var set = new PathTreeSet(new[] { P("data", "alpha"), P("other", "deep", "tree") });

            Assert.True(set.Covers(P("data", "alpha")));
            Assert.True(set.Covers(P("data", "alpha", "one.txt")));
            Assert.True(set.Covers(P("data", "alpha", "sub", "two.txt")));
            Assert.True(set.Covers(P("other", "deep", "tree", "x")));

            Assert.False(set.Covers(P("data")));                       //Above it
            Assert.False(set.Covers(P("data", "alphabet")));           //A name that only starts the same
            Assert.False(set.Covers(P("data", "alphabet", "x")));
            Assert.False(set.Covers(P("data", "alph")));
            Assert.False(set.Covers(P("data", "beta", "alpha")));      //The same name elsewhere
            Assert.False(set.Covers(P("other", "deep")));
            Assert.False(set.Covers(P("other", "deep", "trees", "x")));
            Assert.False(set.Covers(Root));
            Assert.False(set.Covers(null));
        }

        [Fact]
        public void ThousandsOfDirectoriesAnswerLikeEachOnItsOwn()
        {
            var directories = Enumerable.Range(0, 3000).Select(i => P("pkg", $"dir{i % 40}", $"sub{i}")).ToArray();
            var set = new PathTreeSet(directories);

            Assert.Equal(3000, set.Count);
            foreach (var directory in directories.Where((_, i) => i % 97 == 0))
            {
                Assert.True(set.Covers(directory));
                Assert.True(set.Covers(directory + Path.DirectorySeparatorChar + "file.bin"));
                Assert.False(set.Covers(directory + "x"));
                Assert.False(set.Covers(Path.GetDirectoryName(directory)));
            }
        }

        [Fact]
        public void ABareRootCoversTheWholeDrive()
        {
            var set = new PathTreeSet(new[] { Root });

            Assert.True(set.Covers(Root));
            Assert.True(set.Covers(P("a")));
            Assert.True(set.Covers(P("a", "b")));
        }

        [Fact]
        public void AnEmptiedSetCoversNothingAndStartsAnew()
        {
            var set = new PathTreeSet();
            Assert.False(set.Covers(P("a")));

            set.Add(P("a", "long", "path", "below"));
            set.Clear();
            Assert.False(set.Covers(P("a", "long", "path", "below")));

            set.Add(P("b"));
            Assert.True(set.Covers(P("b", "c")));
            Assert.False(set.Covers(P("a", "long", "path", "below")));
        }

        [Fact]
        public void NamesDifferingOnlyInCaseAreTheSameFolderOnlyWhereThePlatformSaysSo()
        {
            var set = new PathTreeSet(new[] { P("data", "Foo") });

            Assert.True(set.Covers(P("data", "Foo", "a")));
            Assert.Equal(PathText.IgnoreCase, set.Covers(P("data", "foo")));
            Assert.Equal(PathText.IgnoreCase, set.Covers(P("data", "foo", "a")));
            Assert.Equal(PathText.IgnoreCase, set.Covers(P("DATA", "Foo", "a")));

            //The deletes of one batch: a file in "foo" has nothing to do with the deleted "Foo"
            Assert.Equal(PathText.IgnoreCase, SearchModel.PathsOverlap(P("data", "Foo"), P("data", "foo", "a")));
            var trees = PathText.NewSet();
            trees.Add(P("data", "Foo"));
            Assert.Equal(PathText.IgnoreCase, SearchModel.IsBelowPendingTree(P("data", "foo", "a"), trees));
            Assert.True(SearchModel.IsBelowPendingTree(P("data", "Foo", "a"), trees));
        }

        /// <summary>What the pass asked before: the node is a root, or lies below one</summary>
        static bool Before(INode n, IReadOnlyList<(string Path, INode Node)> trees)
        {
            var roots = new HashSet<object>(trees.Select(t => t.Node), NodePath.KeyComparer);
            var prefixes = trees.Select(t => t.Path + Path.DirectorySeparatorChar).ToArray();
            return roots.Contains(n) || NodePath.IsUnderAny(n, roots, prefixes);
        }

        static bool Now(INode n, IReadOnlyList<(string Path, INode Node)> trees)
            => NodePath.IsInAnyTree(n, new HashSet<object>(trees.Select(t => t.Node), NodePath.KeyComparer),
                new PathTreeSet(trees.Select(t => t.Path)));

        [Fact]
        public void ChainedAndPathBackedNodesAreFoundInTheirTrees()
        {
            //A path-backed folder with chained nodes below it, and path-backed nodes around
            var data = FileNode.Create(P("data"), FolderState);
            var alpha = new ChainNode(data, "alpha", directory: true);
            var one = new ChainNode(alpha, "one.txt");
            var sub = new ChainNode(alpha, "sub", directory: true);
            var two = new ChainNode(sub, "two.txt");
            var alphabet = new ChainNode(data, "alphabet", directory: true);
            var inAlphabet = new ChainNode(alphabet, "x.txt");
            var added = FileNode.Create(P("data", "alpha", "added.txt"), FileState);       //Added by a watcher
            var addedDeep = FileNode.Create(P("data", "alpha", "sub", "new", "deep.txt"), FileState);
            var other = FileNode.Create(P("other"), FolderState);
            var otherAlpha = FileNode.Create(P("other", "alpha"), FolderState);
            var otherFile = FileNode.Create(P("other", "alpha", "y.txt"), FileState);
            var all = new INode[] { data, alpha, one, sub, two, alphabet, inAlphabet, added, addedDeep, other, otherAlpha, otherFile };

            var chainedRoot = new[] { (P("data", "alpha"), (INode)alpha) };
            Assert.Equal(new INode[] { alpha, one, sub, two, added, addedDeep }, all.Where(n => Now(n, chainedRoot)));

            var pathBackedRoot = new[] { (P("other"), (INode)other) };
            Assert.Equal(new INode[] { other, otherAlpha, otherFile }, all.Where(n => Now(n, pathBackedRoot)));

            var several = new[] { (P("data", "alpha", "sub"), (INode)sub), (P("other", "alpha"), (INode)otherAlpha), (P("data", "alphabet"), (INode)alphabet) };
            Assert.Equal(new INode[] { sub, two, alphabet, inAlphabet, addedDeep, otherAlpha, otherFile }, all.Where(n => Now(n, several)));

            foreach (var trees in new[] { chainedRoot, pathBackedRoot, several, new[] { (P("data"), (INode)data) } })
                foreach (var n in all)
                    Assert.Equal(Before(n, trees), Now(n, trees));
        }

        [Fact]
        public void TheIndexPassFindsTheTreesAcrossTheScanAndTheLiveChanges()
        {
            var nodes = new List<INode> { FileNode.Create(Root, FolderState) };
            foreach (var folder in new[] { "keep", "gone", "gone-too", "goner" })
            {
                nodes.Add(FileNode.Create(P("fsm-tree-test", folder), FolderState));
                for (var i = 0; i < 40; i++)
                {
                    nodes.Add(FileNode.Create(P("fsm-tree-test", folder, $"sub{i}"), FolderState));
                    for (var j = 0; j < 5; j++)
                        nodes.Add(FileNode.Create(P("fsm-tree-test", folder, $"sub{i}", $"file{j}.txt"), FileState));
                }
            }
            var index = new DriveNodeIndex();
            index.ReplaceDrive(Root, DriveNodeIndex.PrepareDrive(nodes, nodes));

            //The live overlay: removed, replaced and added entries, inside the trees and outside
            Assert.True(index.TryRemove(P("fsm-tree-test", "gone", "sub3", "file1.txt"), out _));
            Assert.True(index.TryRemove(P("fsm-tree-test", "keep", "sub3", "file1.txt"), out _));
            var replacement = FileNode.Create(P("fsm-tree-test", "gone", "sub4", "file0.txt"), FileState);
            index[replacement.FullName] = replacement;
            var addedInside = FileNode.Create(P("fsm-tree-test", "gone-too", "new", "inside.txt"), FileState);
            index[addedInside.FullName] = addedInside;
            var addedOutside = FileNode.Create(P("fsm-tree-test", "keep", "outside.txt"), FileState);
            index[addedOutside.FullName] = addedOutside;

            var trees = new[] { P("fsm-tree-test", "gone"), P("fsm-tree-test", "gone-too"), P("fsm-tree-test", "keep", "sub7") }
                .Select(path => (path, index[path])).ToArray();
            var roots = new HashSet<object>(trees.Select(t => t.Item2), NodePath.KeyComparer);
            var found = index.FilterSnapshot(n => NodePath.IsInAnyTree(n, roots, new PathTreeSet(trees.Select(t => t.path))), null);

            var expected = index.Values.Where(n => Before(n, trees)).ToArray();
            Assert.Equal(expected.OrderBy(n => n.FullName, StringComparer.Ordinal), found.OrderBy(n => n.FullName, StringComparer.Ordinal));
            //2 whole folders of 1 + 40 * 6 nodes less the removed file, the added one, and 1 + 5 of the third
            Assert.Equal(2 * 241 - 1 + 1 + 6, found.Count);
            Assert.Contains(replacement, found);
            Assert.Contains(addedInside, found);
            Assert.DoesNotContain(addedOutside, found);
            Assert.DoesNotContain(found, n => n.FullName.StartsWith(P("fsm-tree-test", "goner"), StringComparison.Ordinal));
        }

        [Fact]
        public void TheFolderADeleteHappenedInLetsTheDeleteWait()
        {
            var pending = new PendingDeletes();
            Assert.False(pending.MustPrecede(State(P("t", "a"))));

            pending.Add(Deleted(P("t", "a", "f1")));
            //Its folder follows every delete with its new time - and commutes with it
            Assert.False(pending.MustPrecede(State(P("t", "a"))));
            Assert.False(pending.MustPrecede(State(P("t"))));
            Assert.False(pending.MustPrecede(new FsEvent(WatcherChangeTypes.Changed, P("t", "a"))));
            Assert.False(pending.MustPrecede(FsEvent.MetadataResult(P("t", "a"), null, FolderState, 0)));
            //Nothing to do with it
            Assert.False(pending.MustPrecede(State(P("t", "a", "f2"))));
            Assert.False(pending.MustPrecede(State(P("t", "b", "f1"))));
            Assert.False(pending.MustPrecede(State(P("t", "a", "f10"))));

            //The deleted item is back
            Assert.True(pending.MustPrecede(State(P("t", "a", "f1"))));
            Assert.True(pending.MustPrecede(new FsEvent(WatcherChangeTypes.Changed, P("t", "a", "f1"))));

            pending.Add(Deleted(P("t", "a")));
            Assert.False(pending.MustPrecede(State(P("t"))));
            //The folder, or something inside it, is there again
            Assert.True(pending.MustPrecede(State(P("t", "a"))));
            Assert.True(pending.MustPrecede(State(P("t", "a", "new", "deep.txt"))));
            Assert.False(pending.MustPrecede(State(P("t", "ab"))));

            pending.Clear();
            Assert.Equal(0, pending.Count);
            Assert.Null(pending.First);
            Assert.False(pending.MustPrecede(State(P("t", "a"))));
        }

        [Fact]
        public void WhatReadsTheIndexBelowAFolderSeesEveryDeleteBelowItApplied()
        {
            var pending = new PendingDeletes();
            pending.Add(Deleted(P("t", "a", "f1")));

            //The folder's content state asks how many items are indexed below it
            Assert.True(pending.MustPrecede(State(P("t", "a"), FolderContent.Listable)));
            Assert.True(pending.MustPrecede(State(P("t"), FolderContent.Unlistable)));
            Assert.False(pending.MustPrecede(State(P("t", "b"), FolderContent.Listable)));
            //A create or a rename: the same path, above it, below it
            Assert.True(pending.Overlap(P("t", "a")));
            Assert.True(pending.Overlap(P("t", "a", "f1")));
            Assert.True(pending.Overlap(P("x"), P("t")));
            Assert.False(pending.Overlap(P("t", "b"), P("t", "a", "f2")));
        }

        [Fact]
        public void ARemovedTreeIsAppliedWithOnePassNotOnePerFolder()
        {
            //What the Linux service reports while a package is removed folder by folder:
            //each file's delete and the new state of its folder, then the emptied folder's
            //own delete and the new state of the folder above it
            var events = new List<FsEvent>();
            for (var folder = 0; folder < 300; folder++)
            {
                for (var file = 0; file < 5; file++)
                {
                    events.Add(Deleted(P("usr", "share", "app", $"dir{folder}", $"file{file}")));
                    if (file == 0) events.Add(State(P("usr", "share", "app", $"dir{folder}")));
                }
                events.Add(Deleted(P("usr", "share", "app", $"dir{folder}")));
                events.Add(State(P("usr", "share", "app")));
            }
            events.Add(Deleted(P("usr", "share", "app")));
            events.Add(State(P("usr", "share")));

            var pending = new PendingDeletes();
            var passes = 0;
            foreach (var e in events)
            {
                if (e.ChangeType == WatcherChangeTypes.Deleted) pending.Add(e);
                else if (pending.MustPrecede(e))
                {
                    passes++;
                    pending.Clear();
                }
            }

            //All of them wait for the end of the batch - one pass over the index
            Assert.Equal(0, passes);
            Assert.Equal(300 * 6 + 1, pending.Count);
            Assert.Same(events[0], pending.First);
        }
    }
}
