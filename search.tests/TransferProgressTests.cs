using System;
using System.Globalization;
using System.IO;
using System.Threading;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class TransferProgressTests
    {
        [Theory]
        [InlineData(0UL, 1L)]
        [InlineData(579UL, 579L)]
        [InlineData(ulong.MaxValue, long.MaxValue)]
        public void CachedNtfsSizeBecomesBoundedProgressWork(ulong size, long expected)
            => Assert.Equal(expected, MainWindow.SizeAsWork(size));

        [Theory]
        [InlineData(100UL, 100UL, 0L)]
        [InlineData(90UL, 100UL, 0L)]
        [InlineData(579UL, 123UL, 456L)]
        public void WatcherSizeGrowthNeverReportsNegativeProgress(
            ulong current,
            ulong initial,
            long expected)
            => Assert.Equal(expected, MainWindow.PositiveSizeGrowth(current, initial));

        [Theory]
        [InlineData(100UL, 100UL, 0L)]
        [InlineData(100UL, 110UL, 0L)]
        [InlineData(579UL, 123UL, 456L)]
        public void WatcherSizeDecreaseNeverReportsNegativeProgress(
            ulong initial,
            ulong current,
            long expected)
            => Assert.Equal(expected, MainWindow.PositiveSizeDecrease(initial, current));

        [Theory]
        [InlineData(65, "1:05")]
        [InlineData(3661, "1:01:01")]
        [InlineData(90061, "1.01:01:01")]
        public void ElapsedTimeUsesACompactReadableFormat(int seconds, string expected)
            => Assert.Equal(expected, TransferProgressWindow.FormatDuration(
                TimeSpan.FromSeconds(seconds)));

        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(1536L, "1.5 KB")]
        [InlineData(1073741824L, "1 GB")]
        public void ByteCountsUseCompactBinaryUnits(long bytes, string expected)
            => Assert.Equal(expected, TransferProgressWindow.FormatBytes(bytes));

        [Fact]
        public void ByteCountsDoNotDependOnTheMachineDecimalSeparator()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs-CZ");

                Assert.Equal("1.5 KB", TransferProgressWindow.FormatBytes(1536));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Theory]
        [InlineData(100L, 100L, 99L)]
        [InlineData(120L, 100L, 99L)]
        [InlineData(1L, 1L, 0L)]
        [InlineData(42L, 100L, 42L)]
        public void PassiveObservationCannotClaimCompletion(
            long observed,
            long total,
            long expected)
            => Assert.Equal(expected,
                TransferProgressWindow.CapIncompleteProgress(observed, total));

        [Fact]
        public void SingleItemDescriptionUsesItsFullPath()
        {
            const string path = @"C:\source\project";

            Assert.Equal($"\"{path}\"", FileOperationText.DescribeItems(new[] { path }));
        }

        [Fact]
        public void MultipleItemDescriptionKeepsTheCount()
            => Assert.Equal(
                "2 item(s)",
                FileOperationText.DescribeItems(new[] { @"C:\one", @"C:\two" }));

        [Fact]
        public void NativeCancellableCopyPreservesFileContents()
        {
            var root = CreateRoot();
            try
            {
                var source = Path.Combine(root, "source.bin");
                var destination = Path.Combine(root, "destination.bin");
                var content = new byte[2 * 1024 * 1024 + 137];
                new Random(42).NextBytes(content);
                File.WriteAllBytes(source, content);
                using var cancellation = new CancellationTokenSource();

                var errors = source.UniversalCopyOrMove(
                    destination,
                    overwrite: false,
                    cancellationToken: cancellation.Token);

                Assert.Empty(errors);
                Assert.Equal(content, File.ReadAllBytes(destination));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CancelledCopyStopsBeforeCreatingTheDestination()
        {
            var root = CreateRoot();
            try
            {
                var source = Path.Combine(root, "source.bin");
                var destination = Path.Combine(root, "destination.bin");
                File.WriteAllBytes(source, new byte[1024]);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                Assert.Throws<OperationCanceledException>(() =>
                    source.UniversalCopyOrMove(
                        destination,
                        overwrite: false,
                        cancellationToken: cancellation.Token));
                Assert.False(File.Exists(destination));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CompletedVisibleProgressWindowCloses()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new TransferProgressWindow("Test operation");
                    window.Begin(100);
                    window.Show();
                    Assert.True(window.IsVisible);

                    window.Complete();

                    Assert.False(window.IsVisible);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
                throw failure;
        }

        [Fact]
        public void DirectoryCopyPreservesJunctionInsteadOfTraversingItsTarget()
        {
            var root = CreateRoot();
            var sourceLink = Path.Combine(root, "source", "linked");
            var copiedLink = Path.Combine(root, "copy", "linked");
            try
            {
                var target = Directory.CreateDirectory(Path.Combine(root, "target"));
                File.WriteAllText(Path.Combine(target.FullName, "payload.txt"), "payload");
                var source = Directory.CreateDirectory(Path.Combine(root, "source"));
                Assert.Empty(target.FullName.Hardlink(sourceLink));

                var errors = source.FullName.UniversalCopyOrMove(
                    Path.Combine(root, "copy"),
                    overwrite: false,
                    cancellationToken: new CancellationTokenSource().Token);

                Assert.Empty(errors);
                Assert.True(File.GetAttributes(copiedLink).HasFlag(FileAttributes.ReparsePoint));
                Assert.Equal(
                    target.FullName,
                    new DirectoryInfo(copiedLink).ResolveLinkTarget(returnFinalTarget: false)?.FullName,
                    ignoreCase: true);
            }
            finally
            {
                if (Directory.Exists(copiedLink))
                    Directory.Delete(copiedLink);
                if (Directory.Exists(sourceLink))
                    Directory.Delete(sourceLink);
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DirectoryCopyStillCopiesNestedFilesWithBatchedChildNotifications()
        {
            var root = CreateRoot();
            try
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "source"));
                var nested = source.CreateSubdirectory(Path.Combine("one", "two"));
                File.WriteAllText(Path.Combine(source.FullName, "root.txt"), "root");
                File.WriteAllText(Path.Combine(nested.FullName, "nested.txt"), "nested");
                var destination = Path.Combine(root, "copy");

                var errors = source.FullName.UniversalCopyOrMove(
                    destination,
                    overwrite: false,
                    cancellationToken: CancellationToken.None);

                Assert.Empty(errors);
                Assert.Equal("root", File.ReadAllText(
                    Path.Combine(destination, "root.txt")));
                Assert.Equal("nested", File.ReadAllText(
                    Path.Combine(destination, "one", "two", "nested.txt")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DeletingJunctionRemovesOnlyTheJunction()
        {
            var root = CreateRoot();
            var junction = Path.Combine(root, "junction");
            try
            {
                var target = Directory.CreateDirectory(Path.Combine(root, "target"));
                var payload = Path.Combine(target.FullName, "payload.txt");
                File.WriteAllText(payload, "payload");
                Assert.Empty(target.FullName.Hardlink(junction));

                new FileNode(junction).Delete();

                Assert.False(Directory.Exists(junction));
                Assert.True(Directory.Exists(target.FullName));
                Assert.Equal("payload", File.ReadAllText(payload));
            }
            finally
            {
                if (Directory.Exists(junction))
                    Directory.Delete(junction);
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DeletingOneHardLinkPreservesTheOtherLinkAndData()
        {
            var root = CreateRoot();
            try
            {
                var original = Path.Combine(root, "original.txt");
                var link = Path.Combine(root, "link.txt");
                File.WriteAllText(original, "shared payload");
                Assert.Empty(original.Hardlink(link));

                new FileNode(link).Delete();

                Assert.False(File.Exists(link));
                Assert.True(File.Exists(original));
                Assert.Equal("shared payload", File.ReadAllText(original));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static string CreateRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "win-search-transfer-progress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
