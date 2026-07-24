using System;
using System.IO;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class INodeTests
    {
        [Fact]
        public void IndexKeepsOnlyTheTimestampDisplayedByTheGrid()
        {
            Assert.NotNull(typeof(INode).GetProperty(nameof(INode.LastChangeTime)));
            Assert.Null(typeof(INode).GetProperty("CreationTime"));
            Assert.Null(typeof(INode).GetProperty("LastAccessTime"));
        }

        [Fact]
        public void AddSizeDeltaAccumulates()
        {
            var n = new FileNode();
            n.AddSizeDelta(100);
            n.AddSizeDelta(23);
            Assert.Equal(123ul, n.Size);
            n.AddSizeDelta(-23);
            Assert.Equal(100ul, n.Size);
            n.AddSizeDelta(0);
            Assert.Equal(100ul, n.Size);
        }

        [Fact]
        public void AddSizeDeltaSaturatesAtZeroInsteadOfWrapping()
        {
            var n = new FileNode();
            n.AddSizeDelta(100);
            // A double-subtraction (e.g. a delete event replayed) must not wrap to exabytes
            n.AddSizeDelta(-101);
            Assert.Equal(0ul, n.Size);
            n.AddSizeDelta(long.MinValue + 1);
            Assert.Equal(0ul, n.Size);
        }

        [Fact]
        public void AddSizeDeltaHandlesLargeSizes()
        {
            var n = new FileNode();
            n.AddSizeDelta(long.MaxValue);
            Assert.Equal((ulong)long.MaxValue, n.Size);
            n.AddSizeDelta(-long.MaxValue);
            Assert.Equal(0ul, n.Size);
        }

        [Fact]
        public void PermanentlyDeletingGitRepositoryClearsReadOnlyObjects()
        {
            var root = Path.Combine(Path.GetTempPath(), $"win-search-delete-{Guid.NewGuid():N}");
            var repository = Path.Combine(root, "repository");
            var objectPath = Path.Combine(repository, ".git", "objects", "32",
                "1402640a66e5ac692638eebb387eeee74e57");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
                File.WriteAllText(objectPath, "loose git object");
                var gitDirectory = Path.Combine(repository, ".git");
                File.SetAttributes(gitDirectory,
                    File.GetAttributes(gitDirectory) | FileAttributes.Hidden);
                File.SetAttributes(objectPath,
                    File.GetAttributes(objectPath) | FileAttributes.ReadOnly);

                var node = new FileNode(repository);
                Assert.True(node.IsDirectory);

                node.Delete();

                Assert.False(Directory.Exists(repository));
            }
            finally
            {
                if (File.Exists(objectPath))
                    File.SetAttributes(objectPath,
                        File.GetAttributes(objectPath) & ~FileAttributes.ReadOnly);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PermanentlyDeletingOrdinaryReadOnlyFileClearsAttribute()
        {
            var path = Path.Combine(Path.GetTempPath(),
                $"win-search-read-only-{Guid.NewGuid():N}.dat");

            try
            {
                File.WriteAllText(path, "not a Git object");
                File.SetAttributes(path,
                    File.GetAttributes(path) | FileAttributes.ReadOnly);

                new FileNode(path).Delete();

                Assert.False(File.Exists(path));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path,
                        File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BestEffortDeleteRemovesEverythingExceptLockedItems(bool recycle)
        {
            var root = Path.Combine(Path.GetTempPath(), $"win-search-delete-{Guid.NewGuid():N}");
            var firstLockedPath = Path.Combine(root, "first-locked.bin");
            var secondLockedPath = Path.Combine(root, "blocked", "second-locked.bin");
            var deletablePath = Path.Combine(root, "deletable", "ordinary.bin");
            FileStream firstLock = null;
            FileStream secondLock = null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(secondLockedPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(deletablePath)!);
                File.WriteAllText(firstLockedPath, "locked");
                File.WriteAllText(secondLockedPath, "locked");
                File.WriteAllText(deletablePath, "delete me");
                firstLock = new FileStream(firstLockedPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                secondLock = new FileStream(secondLockedPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read);

                var error = Assert.ThrowsAny<IOException>(
                    () =>
                    {
                        var node = new FileNode(root);
                        if (recycle)
                            node.Recycle((path, isDirectory) =>
                            {
                                if (isDirectory) Directory.Delete(path, recursive: true);
                                else File.Delete(path);
                            });
                        else
                            node.Delete();
                    });

                Assert.Contains("2 undeletable item(s)", error.Message);
                Assert.True(File.Exists(firstLockedPath));
                Assert.True(File.Exists(secondLockedPath));
                Assert.False(File.Exists(deletablePath));
                Assert.False(Directory.Exists(Path.GetDirectoryName(deletablePath)));
            }
            finally
            {
                firstLock?.Dispose();
                secondLock?.Dispose();
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}
