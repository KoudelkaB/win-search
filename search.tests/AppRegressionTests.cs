using SharpCompress.Archives;
using SharpCompress.Writers.Zip;
using System;
using System.IO;
using System.Linq;
using System.Text;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class AppRegressionTests
    {
        [Fact]
        public void ContentSearchFindsNeedlesLargerThanTheDefaultBuffer()
        {
            var path = Path.GetTempFileName();
            try
            {
                var needle = Enumerable.Repeat((byte)'x', 70_000).ToArray();
                File.WriteAllBytes(path, new byte[] { (byte)'a' }.Concat(needle).Concat(new byte[] { (byte)'z' }).ToArray());

                Assert.True(SearchModel.FindFileContents(path, needle) == true);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CaseInsensitiveContentSearchMatchesAcrossBufferBoundary()
        {
            var path = Path.GetTempFileName();
            try
            {
                var prefix = Enumerable.Repeat((byte)'x', (1 << 16) - 2);
                File.WriteAllBytes(path, prefix.Concat(Encoding.ASCII.GetBytes("AbCd")).ToArray());

                Assert.True(SearchModel.FindFileContents(path, Encoding.ASCII.GetBytes("abcd"), caseInsensitive: true) == true);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(@"..\outside.txt")]
        [InlineData(@"folder\..\..\outside.txt")]
        [InlineData(@"C:\outside.txt")]
        [InlineData(@"\server\share\outside.txt")]
        public void ArchiveExtractionRejectsPathsOutsideTheDestination(string entry)
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-extract");
            Assert.Null(ZipExtensions.SafeExtractionPath(root, entry));
            Assert.False(ZipExtensions.IsSafeArchivePath(entry));
        }

        [Fact]
        public void ArchiveExtractionAcceptsNestedRelativePaths()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-extract");
            var result = ZipExtensions.SafeExtractionPath(root, @"folder\child.txt");

            Assert.NotNull(result);
            Assert.StartsWith(Path.GetFullPath(root), result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyArchiveCanBeInspected()
        {
            using var archive = ArchiveFactory.CreateArchive<ZipWriterOptions>();
            Assert.Same(archive, archive.StructuredSubArchive());
        }

        [Fact]
        public void WindowsPathIdentityIgnoresCaseAndRelativeSpelling()
        {
            var path = Path.Combine(Path.GetTempPath(), "Win-Search", "File.txt");
            var alternate = Path.Combine(Path.GetDirectoryName(path), ".", "file.TXT");

            Assert.True(search.Models.Extensions.PathsReferToSameLocation(path, alternate));
        }
    }
}
