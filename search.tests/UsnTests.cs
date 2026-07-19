using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using search.Core;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class UsnTests
    {
        /// <summary>Build one USN_RECORD_V2 (name empty = the unprivileged read's blanking)</summary>
        static byte[] RecordV2(ulong frn, ulong parentFrn, uint reason, uint attributes, string name = "")
        {
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var length = (60 + nameBytes.Length + 7) & ~7; //8-aligned like the real journal
            var r = new byte[length];
            BitConverter.GetBytes(length).CopyTo(r, 0);
            BitConverter.GetBytes((ushort)2).CopyTo(r, 4);       //MajorVersion
            BitConverter.GetBytes(frn).CopyTo(r, 8);
            BitConverter.GetBytes(parentFrn).CopyTo(r, 16);
            BitConverter.GetBytes(reason).CopyTo(r, 40);
            BitConverter.GetBytes(attributes).CopyTo(r, 52);
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(r, 56);
            BitConverter.GetBytes((ushort)60).CopyTo(r, 58);
            nameBytes.CopyTo(r, 60);
            return r;
        }

        static byte[] Batch(params byte[][] records)
        {
            var data = new byte[8 + records.Sum(r => r.Length)];
            BitConverter.GetBytes(12345L).CopyTo(data, 0); //Next USN
            var offset = 8;
            foreach (var r in records) { r.CopyTo(data, offset); offset += r.Length; }
            return data;
        }

        [Fact]
        public void ParsesRecordsWithAndWithoutNames()
        {
            var data = Batch(
                RecordV2(0x0005_000000000042, 0x0002_000000000007, UsnJournal.ReasonFileCreate, 0x20),
                RecordV2(0x0001_0000000000AA, 0x0002_000000000007, UsnJournal.ReasonFileDelete | 0x80000000 /*close*/, 0x10, "gone.txt"));

            var records = UsnJournal.Parse(data, data.Length);

            Assert.Equal(2, records.Count);
            Assert.Equal(0x0005_000000000042UL, records[0].Frn);
            Assert.Equal(0x0002_000000000007UL, records[0].ParentFrn);
            Assert.Equal(UsnJournal.ReasonFileCreate, records[0].Reason & UsnJournal.ReasonFileCreate);
            Assert.Equal("", records[0].Name); //The unprivileged read blanks names
            Assert.Equal("gone.txt", records[1].Name);
            Assert.True((records[1].Attributes & 0x10) != 0); //FILE_ATTRIBUTE_DIRECTORY carried through
        }

        [Fact]
        public void ParseStopsAtTruncatedAndUnknownVersionRecords()
        {
            var v3 = RecordV2(1, 2, UsnJournal.ReasonFileCreate, 0);
            BitConverter.GetBytes((ushort)3).CopyTo(v3, 4); //An unrequested V3 must be skipped, not misparsed
            var good = RecordV2(3, 4, UsnJournal.ReasonFileDelete, 0);
            var data = Batch(v3, good);

            var records = UsnJournal.Parse(data, data.Length);
            Assert.Equal(3UL, Assert.Single(records).Frn);

            //A batch cut mid-record must not throw or fabricate a record
            Assert.Empty(UsnJournal.Parse(data, 12));
        }

        [Fact]
        public void MftNodesCarryTheirFileReferenceNumbers()
        {
            var nodes = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(directory: true, sequence: 7, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // entry 6
                .Parse();

            var docs = nodes.Single(n => n.Name == "Docs");
            Assert.Equal(((ulong)7 << 48) | 6UL, docs.Frn); //USN records key files by sequence<<48|entry

            //Nodes outside the MFT have no reference - the USN watcher must fall back for them
            Assert.Equal(0UL, new FileNode(Path.GetTempPath().TrimEnd('\\')).Frn);
        }

        /// <summary>Node carrying only an FRN - what the map stores and verifies elsewhere</summary>
        sealed class FrnNode : INode
        {
            readonly ulong frn;
            public FrnNode(ulong frn) => this.frn = frn;
            public override ulong Frn => frn;
            public override string FullName => @"C:\" + frn;
            public override string Name => frn.ToString();
            public override FileAttributes Attributes { get => 0; protected set { } }
            public override ulong Size { get => 0; protected set { } }
            public override DateTime LastChangeTime { get => default; protected set { } }
        }

        [Fact]
        public void FrnMapResolvesExactReferencesOnly()
        {
            var map = new UsnDriveWatcher.FrnMap();
            var frn = ((ulong)7 << 48) | 42;
            map.Populate(new INode[] { new FrnNode(frn), new FrnNode(0) }); //Frn 0 = walked node, never mapped

            Assert.True(map.TryGetValue(frn, out var node));
            Assert.Equal(frn, node.Frn);
            Assert.False(map.TryGetValue(((ulong)8 << 48) | 42, out _)); //Reused record: same entry, newer sequence
            Assert.False(map.TryGetValue(41, out _));
            Assert.False(map.TryGetValue(0, out _));
        }

        [Fact]
        public void FrnMapReusesTheMftRecordTableAndOverlaysWatcherChanges()
        {
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(sequence: 7, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "old.txt") });
            using var stream = new MemoryStream(mft.Image());
            var source = MftDriveReader.GetNodes(stream, mft.BytesPerRecord,
                (long)mft.Count * mft.BytesPerRecord, FakeMft.Root);
            var frnSource = Assert.IsAssignableFrom<IFrnNodeSource>(source);
            var scanned = ((ulong)7 << 48) | 6;
            var map = new UsnDriveWatcher.FrnMap();

            map.Populate(source);

            Assert.True(map.TryGetValue(scanned, out var old));
            Assert.Same(frnSource.DenseNodes[1], old);

            var reused = ((ulong)8 << 48) | 6;
            var replacement = new FrnNode(reused);
            map.Set(reused, replacement);
            Assert.False(map.TryGetValue(scanned, out _));
            Assert.True(map.TryGetValue(reused, out var current));
            Assert.Same(replacement, current);

            map.Remove(scanned); //A stale delete must not remove the new sequence.
            Assert.True(map.TryGetValue(reused, out _));
            map.Remove(reused);
            Assert.False(map.TryGetValue(reused, out _));
        }

        [Fact]
        public void FrnMapSetRemoveAndOverflowBehaveLikeTheDictionary()
        {
            var map = new UsnDriveWatcher.FrnMap();
            var scanned = ((ulong)1 << 48) | 5;
            map.Populate(new INode[] { new FrnNode(scanned) });

            var beyond = ((ulong)3 << 48) | 1000; //The MFT grew past the scanned records
            map.Set(beyond, new FrnNode(beyond));
            Assert.True(map.TryGetValue(beyond, out _));

            var reused = ((ulong)2 << 48) | 5; //The slot's entry was freed and reused
            map.Set(reused, new FrnNode(reused));
            Assert.False(map.TryGetValue(scanned, out _));
            Assert.True(map.TryGetValue(reused, out _));

            map.Remove(((ulong)9 << 48) | 5); //A stale reference must not evict the new owner
            Assert.True(map.TryGetValue(reused, out _));
            map.Remove(reused);
            Assert.False(map.TryGetValue(reused, out _));

            map.Clear();
            map.Set(beyond, new FrnNode(beyond)); //Cleared map still accepts entries (overflow)
            Assert.True(map.TryGetValue(beyond, out _));
            map.Populate(Array.Empty<INode>()); //Repopulation resets the overflow too
            Assert.False(map.TryGetValue(beyond, out _));
        }

        [Fact]
        public void FrnMapHandlesSparseHighRecordNumbersWithoutRangeSizedAllocation()
        {
            var map = new UsnDriveWatcher.FrnMap();
            var sparse = ((ulong)4 << 48) | (1UL << 40) | 17;

            map.Populate(new INode[] { new FrnNode(sparse) });

            Assert.True(map.TryGetValue(sparse, out var node));
            Assert.Equal(sparse, node.Frn);
        }

        [Fact]
        public void HardLinkAndMultiLinkedSizeChangesRequireAnExactMftRebuild()
        {
            Assert.True(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonHardLinkChange, false));
            Assert.True(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonDataExtend, true));
            Assert.True(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonFileDelete, true));
            Assert.True(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonRenameNewName, true));

            Assert.False(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonDataExtend, false));
            Assert.False(UsnDriveWatcher.RequiresExactMftRescan(UsnJournal.ReasonBasicInfoChange, true));
            Assert.InRange(UsnDriveWatcher.ExactRescanQuietMs, 1, 1999);
        }

        [Fact]
        public async Task FrnMapKeepsWatcherUpdatesArrivingDuringPopulation()
        {
            var map = new UsnDriveWatcher.FrnMap();
            var scanned = ((ulong)1 << 48) | 5;
            var changed = ((ulong)2 << 48) | 6;
            using var populationEntered = new ManualResetEventSlim();
            using var releasePopulation = new ManualResetEventSlim();
            using var updateStarted = new ManualResetEventSlim();

            IEnumerable<INode> BlockingScan()
            {
                populationEntered.Set();
                releasePopulation.Wait();
                yield return new FrnNode(scanned);
            }

            var populate = Task.Run(() => map.Populate(BlockingScan()));
            Task update = null;
            try
            {
                Assert.True(populationEntered.Wait(TimeSpan.FromSeconds(5)));
                update = Task.Run(() =>
                {
                    updateStarted.Set();
                    map.Set(changed, new FrnNode(changed));
                });
                Assert.True(updateStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(update.IsCompleted); //Set is waiting for the population swap
            }
            finally
            {
                releasePopulation.Set();
            }
            await Task.WhenAll(populate, update);

            Assert.True(map.TryGetValue(scanned, out _));
            Assert.True(map.TryGetValue(changed, out _));
        }

        [Fact]
        public void FsEventCarriesRenamesAcrossDirectories()
        {
            var moved = new FsEvent(WatcherChangeTypes.Renamed, @"C:\b\file.txt", @"C:\a\file.txt");
            Assert.Equal(@"C:\a\file.txt", moved.OldFullPath); //RenamedEventArgs cannot express this

            var fromWatcher = FsEvent.From(new RenamedEventArgs(WatcherChangeTypes.Renamed, @"C:\a", "new.txt", "old.txt"));
            Assert.Equal(@"C:\a\new.txt", fromWatcher.FullPath);
            Assert.Equal(@"C:\a\old.txt", fromWatcher.OldFullPath);
            Assert.Null(FsEvent.From(new FileSystemEventArgs(WatcherChangeTypes.Deleted, @"C:\a", "x.txt")).OldFullPath);
        }
    }
}
