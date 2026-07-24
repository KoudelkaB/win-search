using System;
using System.Threading;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class DirectoryWalkerTests
    {
        [Fact]
        public void FallbackWalkAggregatesEveryDescendantWithoutCountingTheFolderItself()
        {
            var directory = new NodeMetadataSnapshot(true, 0, DateTime.MinValue);
            var root = FileNode.Create(@"Q:\", directory);
            var docs = FileNode.Create(@"Q:\Docs", directory);
            var sub = FileNode.Create(@"Q:\Docs\Sub", directory);
            var topFile = FileNode.Create(@"Q:\Docs\a.txt",
                new NodeMetadataSnapshot(false, 5, DateTime.MinValue));
            var deepFile = FileNode.Create(@"Q:\Docs\Sub\b.txt",
                new NodeMetadataSnapshot(false, 10, DateTime.MinValue));

            DirectoryWalker.AggregateFolderSizes(
                new INode[] { deepFile, root, sub, topFile, docs },
                CancellationToken.None);

            Assert.Equal(4U, root.Count); //Docs + a.txt + Sub + b.txt
            Assert.Equal(3U, docs.Count); //a.txt + Sub + b.txt
            Assert.Equal(1U, sub.Count);
            Assert.Equal(1U, topFile.Count);
            Assert.Equal(15UL, root.Size);
            Assert.Equal(15UL, docs.Size);
            Assert.Equal(10UL, sub.Size);
        }
    }
}
