using System;
using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class MftDriveReaderTests
    {
        static readonly DateTime Created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        static readonly DateTime Modified = new(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        static readonly DateTime Accessed = new(2022, 11, 12, 13, 14, 15, DateTimeKind.Utc);

        static FakeMft WithRoot(int bytesPerRecord) => new FakeMft(bytesPerRecord).AddEmpty(5).AddRoot();

        [Theory]
        [InlineData(1024, MftChunkReader.DefaultChunkBytes)] // standard size, single chunk
        [InlineData(1024, 2048)]  // two records per chunk
        [InlineData(1024, 100)]   // chunk smaller than one record
        [InlineData(1536, 1536)]  // sector multiple, no power of two
        [InlineData(3072, 6144)]
        [InlineData(4096, 4096)]  // 4Kn disks
        [InlineData(1000, 3000)]  // no power of two, not even sector-aligned
        public void ParsesTheHierarchyForAnyRecordSizeAndChunking(int bytesPerRecord, int chunkBytes)
        {
            var mft = WithRoot(bytesPerRecord)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
                .AddRecord(attributes: new[]                                                                   // 7
                {
                    FakeMft.StandardInfo(Created, Modified, Accessed),
                    FakeMft.FileName(6, "a.txt"),
                    FakeMft.ResidentData(123)
                });

            var nodes = mft.Parse(chunkBytes);

            Assert.Equal(3, nodes.Count);

            var file = nodes.Single(n => n.Name == "a.txt");
            Assert.False(file.IsDirectory);
            Assert.Equal(@"Q:\Docs\a.txt", file.FullName);
            Assert.Equal(123UL, file.Size);
            Assert.Equal(Created, file.CreationTime);   // $STANDARD_INFORMATION wins over $FILE_NAME
            Assert.Equal(Modified, file.LastChangeTime);
            Assert.Equal(Accessed, file.LastAccessTime);

            var docs = nodes.Single(n => n.Name == "Docs");
            Assert.True(docs.IsDirectory);
            Assert.Equal(@"Q:\Docs", docs.FullName);
            Assert.Equal(123UL, docs.Size); // file sizes roll up into folders

            var root = nodes.Single(n => n.Name == "Q:");
            Assert.Equal(FakeMft.Root, root.FullName);
            Assert.Equal(123UL, root.Size);
        }

        [Fact]
        public void ReadsNonResidentDataSizes()
        {
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "big.bin"), FakeMft.NonResidentData(5_000_000_000) })
                .Parse();

            Assert.Equal(5_000_000_000UL, nodes.Single(n => n.Name == "big.bin").Size);
        }

        [Theory]
        [InlineData(false)] // extension record after its base record
        [InlineData(true)]  // extension record before its base record
        public void ResolvesTheDataSizeFromAnExtensionRecord(bool extensionFirst)
        {
            var mft = WithRoot(1024);
            if (extensionFirst)
                mft.AddRecord(baseReference: 7, attributes: new[] { FakeMft.NonResidentData(777) })                                                    // 6
                   .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "frag.bin", size: 999), FakeMft.AttributeListWithData(6) });     // 7
            else
                mft.AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "frag.bin", size: 999), FakeMft.AttributeListWithData(7) })      // 6
                   .AddRecord(baseReference: 6, attributes: new[] { FakeMft.NonResidentData(777) });                                                   // 7

            // One record per chunk - the base and its extension never share a chunk
            var nodes = mft.Parse(chunkBytes: 1024);

            Assert.Equal(777UL, nodes.Single(n => n.Name == "frag.bin").Size);
        }

        [Fact]
        public void FallsBackToTheFileNameSizeWhenTheExtensionRecordIsMissing()
        {
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "orphan.bin", size: 999), FakeMft.AttributeListWithData(7) }) // 6
                .AddEmpty() // 7: the referenced record is invalid
                .Parse();

            Assert.Equal(999UL, nodes.Single(n => n.Name == "orphan.bin").Size);
        }

        [Fact]
        public void PrefersTheWin32NameOverTheDosName()
        {
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[]
                {
                    FakeMft.FileName(FakeMft.RootEntry, "AFILE~1.TXT", ns: 2), // DOS first on purpose
                    FakeMft.FileName(FakeMft.RootEntry, "a file.txt", ns: 1)
                })
                .Parse();

            Assert.Contains(nodes, n => n.Name == "a file.txt");
            Assert.DoesNotContain(nodes, n => n.Name == "AFILE~1.TXT");
        }

        [Fact]
        public void SkipsFreeCorruptAndExtensionRecords()
        {
            var nodes = WithRoot(1024)
                .AddRecord(inUse: false, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "deleted.txt") }) // 6: free
                .AddRaw(Enumerable.Repeat((byte)0xCC, 1024).ToArray())                                             // 7: garbage
                .AddRecord(baseReference: 42, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "ext") })    // 8: extension
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "kept.txt") })                  // 9
                .Parse();

            Assert.Equal(new[] { "Q:", "kept.txt" }, nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void ConsumesThePartialTailSoAPipeSenderNeverBlocks()
        {
            var mft = WithRoot(1024).AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "a.txt") });
            var stream = new MemoryStream(mft.Image(tailBytes: 300));

            var nodes = mft.Parse(tailBytes: 300, stream: stream);

            Assert.Equal(stream.Length, stream.Position);
            Assert.Equal(2, nodes.Count);
        }

        [Fact]
        public void AnMftShorterThanOneRecordYieldsNothingButIsStillConsumed()
        {
            var stream = new MemoryStream(new byte[100]);
            var nodes = MftDriveReader.GetNodes(stream, 1024, 100, FakeMft.Root);

            Assert.Empty(nodes);
            Assert.Equal(100, stream.Position);
        }

        [Fact]
        public void ParsesManyChunksInParallelWithCorrectFolderSizes()
        {
            const int files = 500;
            var mft = WithRoot(1024);
            for (var i = 0; i < files; i++)
                mft.AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, $"f{i}.dat"), FakeMft.ResidentData(i) });

            var nodes = mft.Parse(chunkBytes: 8 * 1024); // 8 records per chunk -> dozens of chunks

            Assert.Equal(files + 1, nodes.Count);
            Assert.Equal(42UL, nodes.Single(n => n.Name == "f42.dat").Size);
            Assert.Equal((ulong)(files * (files - 1) / 2), nodes.Single(n => n.Name == "Q:").Size);
        }
    }
}
