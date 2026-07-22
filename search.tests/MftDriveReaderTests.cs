using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

        [Fact]
        public void RuntimeMftNodeFitsIn64BytesExcludingItsName()
        {
            var nodeType = WithRoot(1024).Parse().Single().GetType();
            _ = RuntimeHelpers.GetUninitializedObject(nodeType); //Warm the runtime helper.
            const int count = 10_000;
            var nodes = new object[count];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < nodes.Length; i++)
                nodes[i] = RuntimeHelpers.GetUninitializedObject(nodeType);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(64, allocated / count);
            GC.KeepAlive(nodes);
        }

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
            // Local times - the grid mixes MFT rows with watcher/walk FileNodes, whose
            // FileSystemInfo times are local
            Assert.Equal(Modified.ToLocalTime(), file.LastChangeTime);

            var docs = nodes.Single(n => n.Name == "Docs");
            Assert.True(docs.IsDirectory);
            Assert.Equal(@"Q:\Docs", docs.FullName);
            Assert.Equal(123UL, docs.Size); // file sizes roll up into folders

            var root = nodes.Single(n => n.Name == "Q:");
            Assert.Equal(FakeMft.Root, root.FullName);
            Assert.Equal(123UL, root.Size);
        }

        [Fact]
        public void WholeTreeDeletionSubtractsItsAggregateOnceFromSurvivingParents()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[]
                {
                    FakeMft.FileName(FakeMft.RootEntry, "Docs")
                })
                .AddRecord(attributes: new[]
                {
                    FakeMft.FileName(6, "large.bin"), FakeMft.ResidentData(123)
                })
                .Parse();
            var root = nodes.Single(n => n.Name == "Q:");
            var docs = nodes.Single(n => n.Name == "Docs");

            var changed = SearchModel.ApplySizeDeltaToParentChain(docs, -(long)docs.Size, null);

            Assert.Equal(1, changed);
            Assert.Equal(0UL, root.Size);
            Assert.Equal(123UL, docs.Size); //Removed node itself is discarded, never decremented.
        }

        [Fact]
        public void DeepHierarchyAggregatesOnceAndKeepsCanonicalPathHashes()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(5, "A") })
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(6, "B") })
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(7, "C") })
                .AddRecord(attributes: new[]
                {
                    FakeMft.FileName(8, "deep.bin"), FakeMft.ResidentData(123)
                })
                .Parse();

            Assert.Equal(123UL, nodes.Single(n => n.Name == "Q:").Size);
            Assert.All(nodes.Where(n => n.IsDirectory), n => Assert.Equal(123UL, n.Size));
            var map = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            foreach (var node in nodes) map[node] = node;
            Assert.True(map.TryGetValue(@"Q:\A\B\C\deep.bin", out var deep));
            Assert.Equal("deep.bin", deep.Name);
        }

        [Fact]
        public void IndexedMftFileCanBeRemovedByPathAndDecrementsTheRootAggregate()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[]
                {
                    FakeMft.FileName(FakeMft.RootEntry, "Docs")
                })
                .AddRecord(attributes: new[]
                {
                    FakeMft.FileName(6, "single.bin"), FakeMft.ResidentData(123)
                })
                .Parse();
            var map = new NonBlocking.ConcurrentDictionary<object, INode>(NodePath.KeyComparer);
            foreach (var node in nodes) map[node] = node;
            var index = new DriveNodeIndex();
            index.ReplaceDrive(FakeMft.Root, map, nodes);
            var root = nodes.Single(n => n.Name == "Q:");

            Assert.True(index.TryRemove(@"Q:\Docs\single.bin", out var removed));
            Assert.Equal(123UL, removed.Size);
            Assert.Equal(2, SearchModel.ApplySizeDeltaToParentChain(
                removed, -(long)removed.Size, null));
            Assert.Equal(0UL, root.Size);
        }

        [Fact]
        public void FileDeleteBeforeItsParentTreeDeleteIsNotSubtractedTwice()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[]
                {
                    FakeMft.FileName(FakeMft.RootEntry, "Docs")
                })
                .AddRecord(attributes: new[]
                {
                    FakeMft.FileName(6, "single.bin"), FakeMft.ResidentData(123)
                })
                .Parse();
            var root = nodes.Single(n => n.Name == "Q:");
            var docs = nodes.Single(n => n.Name == "Docs");
            var file = nodes.Single(n => n.Name == "single.bin");

            //One drive queue is FIFO. If the child record is delivered first it reduces
            //the directory aggregate; the later tree delete therefore has no remainder.
            SearchModel.ApplySizeDeltaToParentChain(file, -(long)file.Size, null);
            Assert.Equal(0UL, docs.Size);
            Assert.Equal(0UL, root.Size);

            if (docs.Size > 0)
                SearchModel.ApplySizeDeltaToParentChain(docs, -(long)docs.Size, null);
            Assert.Equal(0UL, root.Size);
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
        public void ResolvesTheDataSizeEvenWithoutAResidentAttributeList()
        {
            // A non-resident $ATTRIBUTE_LIST leaves no list value in the base record at all;
            // the extension record's own base reference must be enough to resolve the size
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "frag.bin", size: 999) }) // 6
                .AddRecord(baseReference: 6, attributes: new[] { FakeMft.NonResidentData(777) })             // 7
                .Parse();

            Assert.Equal(777UL, nodes.Single(n => n.Name == "frag.bin").Size);
        }

        [Fact]
        public void AFreedExtensionRecordDoesNotPoisonAReusedBaseIndex()
        {
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "new.bin", size: 999) })    // 6: reused base
                .AddRecord(inUse: false, baseReference: 6, attributes: new[] { FakeMft.NonResidentData(777) }) // 7: stale, freed extension
                .Parse();

            Assert.Equal(999UL, nodes.Single(n => n.Name == "new.bin").Size);
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
        public void RecoversTheWin32NameFromAnExtensionRecord()
        {
            // A crowded base record kept only the DOS 8.3 name; the Win32 name overflowed
            // into an extension record. The file must show its full Windows name - and
            // count once in folder sizes (the DOS name shadows the extension's Win32 pair).
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[]                                                                       // 6
                {
                    FakeMft.FileName(FakeMft.RootEntry, "DOTNET~4.EXE", ns: 2),
                    FakeMft.ResidentData(100)
                })
                .AddRecord(baseReference: 6, attributes: new[]                                                     // 7
                {
                    FakeMft.FileName(FakeMft.RootEntry, "dotnet-sdk-10.0.100-preview.7.25380.108-win-x64.exe", ns: 1)
                })
                .Parse(chunkBytes: 1024); // base and extension never share a chunk

            var file = nodes.Single(n => n.Name == "dotnet-sdk-10.0.100-preview.7.25380.108-win-x64.exe");
            Assert.Equal(@"Q:\dotnet-sdk-10.0.100-preview.7.25380.108-win-x64.exe", file.FullName);
            Assert.DoesNotContain(nodes, n => n.Name == "DOTNET~4.EXE");
            Assert.Equal(100UL, nodes.Single(n => n.Name == "Q:").Size); // one link, counted once
        }

        [Fact]
        public void AFileWhoseNamesAllOverflowedIsStillIndexed()
        {
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.StandardInfo(Created, Modified, Accessed), FakeMft.ResidentData(123) }) // 6: no $FILE_NAME at all
                .AddRecord(baseReference: 6, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "overflowed.txt", ns: 1) }) // 7
                .Parse(chunkBytes: 1024);

            var file = nodes.Single(n => n.Name == "overflowed.txt");
            Assert.Equal(@"Q:\overflowed.txt", file.FullName);
            Assert.Equal(123UL, file.Size);
        }

        [Fact]
        public void AnEquallyRankedExtensionNameDoesNotReplaceTheBaseName()
        {
            // Both names are Win32 hard links - the base record's own name stays
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Other") })   // 6
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "kept.bin", ns: 1), FakeMft.ResidentData(50) }) // 7
                .AddRecord(baseReference: 7, attributes: new[] { FakeMft.FileName(6, "overflow-link.bin", ns: 1) })                // 8
                .Parse(chunkBytes: 1024);

            Assert.Contains(nodes, n => n.Name == "kept.bin");
            Assert.DoesNotContain(nodes, n => n.Name == "overflow-link.bin");
            Assert.Equal(100UL, nodes.Single(n => n.Name == "Q:").Size); // both links still counted
        }

        [Fact]
        public void AStaleExtensionNameDoesNotPoisonAReusedBaseIndex()
        {
            var nodes = WithRoot(1024)
                .AddRecord(sequence: 5, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "NEWFIL~1.TXT", ns: 2) }) // 6: reused base
                .AddRecord(baseReference: 6 | (2UL << 48), attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "old name.txt", ns: 1) }) // 7: stale extension
                .Parse(chunkBytes: 1024);

            Assert.Contains(nodes, n => n.Name == "NEWFIL~1.TXT");
            Assert.DoesNotContain(nodes, n => n.Name == "old name.txt");
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
        public void OrphanedFilesAreDropped()
        {
            // Parent entry 99 does not exist (deleted mid-scan): the file's real path is
            // unknowable and must not surface at a made-up "Q:\" path
            var nodes = WithRoot(1024)
                .AddRecord(attributes: new[] { FakeMft.FileName(99, "ghost.txt"), FakeMft.ResidentData(100) }) // 6
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "kept.txt") })              // 7
                .Parse();

            Assert.Equal(new[] { "Q:", "kept.txt" }, nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(0UL, nodes.Single(n => n.Name == "Q:").Size); // the orphan's size never lands anywhere
        }

        [Fact]
        public void AnOrphanedDirectoryTakesItsSubtreeWithIt()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(99, "lost") })          // 6: orphan dir
                .AddRecord(attributes: new[] { FakeMft.FileName(6, "child.txt"), FakeMft.ResidentData(50) }) // 7
                .Parse();

            Assert.Equal(new[] { "Q:" }, nodes.Select(n => n.Name));
        }

        [Fact]
        public void ParentCyclesAreDropped()
        {
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(7, "a") })          // 6 -> 7
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(6, "b") })          // 7 -> 6
                .AddRecord(attributes: new[] { FakeMft.FileName(6, "in-cycle.txt") })                // 8
                .AddRecord(attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "kept.txt") })    // 9
                .Parse();

            Assert.Equal(new[] { "Q:", "kept.txt" }, nodes.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void AStaleParentSequenceOrphansTheFile()
        {
            // Entry 6 currently holds "Docs" with sequence 5; the stale file still references
            // sequence 2 - its true parent was deleted and the entry reused mid-scan
            var nodes = WithRoot(1024)
                .AddRecord(directory: true, sequence: 5, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
                .AddRecord(attributes: new[] { FakeMft.FileName(6 | (2UL << 48), "stale.txt") })                            // 7
                .AddRecord(attributes: new[] { FakeMft.FileName(6 | (5UL << 48), "fresh.txt") })                            // 8
                .Parse();

            Assert.DoesNotContain(nodes, n => n.Name == "stale.txt");
            Assert.Equal(@"Q:\Docs\fresh.txt", nodes.Single(n => n.Name == "fresh.txt").FullName);
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
        public void HardLinksCountOncePerLinkInFolderSizes()
        {
            // A file hard-linked into Docs and Other is one directory entry in each -
            // both folders count it, and the root counts both entries (walk/Explorer semantics)
            var mft = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") })  // 6
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Other") }) // 7
                .AddRecord(attributes: new[]                                                                    // 8
                {
                    FakeMft.FileName(6, "linked.bin", ns: 1),
                    FakeMft.FileName(7, "linked2.bin", ns: 1),
                    FakeMft.ResidentData(100)
                });

            var nodes = mft.Parse();

            Assert.Equal(100UL, nodes.Single(n => n.Name == "Docs").Size);
            Assert.Equal(100UL, nodes.Single(n => n.Name == "Other").Size);
            Assert.Equal(200UL, nodes.Single(n => n.Name == "Q:").Size);
        }

        [Fact]
        public void MftSourceRetainsOnlyTheSparseMultiLinkMarkerNeededByUsnUpdates()
        {
            var mft = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") })  // 6
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Other") }) // 7
                .AddRecord(sequence: 4, attributes: new[]                                                        // 8
                {
                    FakeMft.FileName(6, "linked.bin"),
                    FakeMft.FileName(7, "linked-again.bin")
                })
                .AddRecord(sequence: 5, attributes: new[] { FakeMft.FileName(6, "single.bin") });               // 9
            using var stream = new MemoryStream(mft.Image());
            var source = Assert.IsAssignableFrom<IFrnNodeSource>(MftDriveReader.GetNodes(stream,
                mft.BytesPerRecord, (long)mft.Count * mft.BytesPerRecord, FakeMft.Root));

            Assert.True(source.HasMultipleLinks(((ulong)4 << 48) | 8));
            Assert.False(source.HasMultipleLinks(((ulong)5 << 48) | 9));
            Assert.False(source.HasMultipleLinks(((ulong)6 << 48) | 8)); //stale sequence
        }

        [Fact]
        public void HardLinkNamesOverflowedToExtensionRecordsCountInFolderSizes()
        {
            // The file's second hard-link name lives in an extension record (its base
            // record ran out of space) - the extension's own base reference ties it back
            var mft = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") })  // 6
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Other") }) // 7
                .AddRecord(attributes: new[] { FakeMft.FileName(6, "linked.bin"), FakeMft.ResidentData(100) })  // 8
                .AddRecord(baseReference: 8, attributes: new[] { FakeMft.FileName(7, "linked2.bin") });         // 9

            var nodes = mft.Parse();

            Assert.Equal(100UL, nodes.Single(n => n.Name == "Docs").Size);
            Assert.Equal(100UL, nodes.Single(n => n.Name == "Other").Size);
            Assert.Equal(200UL, nodes.Single(n => n.Name == "Q:").Size);
        }

        [Fact]
        public void ADosShadowNameDoesNotDoubleCountFolderSizes()
        {
            var mft = WithRoot(1024)
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
                .AddRecord(attributes: new[]                                                                   // 7
                {
                    FakeMft.FileName(6, "AFILE~1.TXT", ns: 2), // the DOS pair of the Win32 name below
                    FakeMft.FileName(6, "a file.txt", ns: 1),
                    FakeMft.ResidentData(100)
                });

            var nodes = mft.Parse();

            Assert.Equal(100UL, nodes.Single(n => n.Name == "Docs").Size);
            Assert.Equal(100UL, nodes.Single(n => n.Name == "Q:").Size);
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
