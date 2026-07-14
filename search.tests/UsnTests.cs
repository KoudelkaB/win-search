using System;
using System.IO;
using System.Linq;
using System.Text;
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
