using System;
using System.Collections.Generic;
using System.Linq;
using search.Core;
using Xunit;

namespace search.Tests
{
    public class MftRecordBitmapTests
    {
        const int Record = 1024;

        static byte[] Bits(int records, Func<int, bool> used)
        {
            var bits = new byte[(records + 7) / 8];
            for (var r = 0; r < records; r++)
                if (used(r)) bits[r >> 3] |= (byte)(1 << (r & 7));
            return bits;
        }

        /// <summary>
        /// Walk the whole $MFT the way RawMft's stream does - capped by chunk and run
        /// boundaries - and return every span with its free flag
        /// </summary>
        static List<(long Start, int Length, bool Free)> Walk(MftRecordBitmap bitmap,
            long length, int chunk, long runBytes)
        {
            var spans = new List<(long, int, bool)>();
            var position = 0L;
            while (position < length)
            {
                var max = (int)Math.Min(Math.Min(chunk, length - position),
                    runBytes - position % runBytes);
                var span = bitmap.NextSpan(position, max, out var free);
                Assert.InRange(span, 1, max);
                spans.Add((position, span, free));
                position += span;
            }
            return spans;
        }

        [Fact]
        public void LargeFreeBlockIsSkippedAndEveryUsedRecordIsRead()
        {
            const int records = 20000;
            // Used head, one huge free block, used tail with scattered holes
            Func<int, bool> used = r => r < 3000 || (r >= 17000 && r % 5 != 0);
            var bitmap = new MftRecordBitmap(Bits(records, used), records, Record);

            var spans = Walk(bitmap, (long)records * Record, 1 << 20, 3000L * Record);

            var skipped = new HashSet<long>();
            foreach (var (start, length, free) in spans)
            {
                if (!free) continue;
                Assert.Equal(0, start % Record);
                Assert.Equal(0, length % Record);
                for (var r = start / Record; r < (start + length) / Record; r++)
                {
                    Assert.False(used((int)r), $"used record {r} was skipped");
                    skipped.Add(r);
                }
            }
            for (var r = 3000; r < 17000; r++)
                Assert.Contains((long)r, skipped);
            // Single-record holes are read with their neighbours, except where a span
            // capped by a run boundary happens to consist of nothing else
            Assert.InRange(skipped.Count, 14000, 14010);
            Assert.True(spans.Count < 40, $"too many spans: {spans.Count}");
        }

        [Fact]
        public void ShortFreeRunsAreReadWithTheirNeighbours()
        {
            const int records = 4096;
            var bitmap = new MftRecordBitmap(
                Bits(records, r => r % 100 != 0), records, Record);

            var span = bitmap.NextSpan(Record, 1 << 20, out var free);

            Assert.False(free);
            Assert.Equal(1 << 20, span);
        }

        [Fact]
        public void ShortFreeRunAtTheEndOfTheAllowedSpanIsSkipped()
        {
            var bitmap = new MftRecordBitmap(Bits(64, r => r < 32), 64, Record);

            var span = bitmap.NextSpan(32L * Record, 16 * Record, out var free);

            Assert.True(free);
            Assert.Equal(16 * Record, span);
        }

        [Fact]
        public void AttributeListNamesTheExtensionRecordsOfTheBitmap()
        {
            // $MFT base record 0 of a 7 GB $MFT: $BITMAP moved to extension record 11,
            // its second VCN range to record 12; a named $BITMAP must be ignored
            static byte[] Entry(uint type, byte nameLength, ulong vcn, ulong reference)
            {
                var entry = new byte[nameLength == 0 ? 32 : 40];
                BitConverter.GetBytes(type).CopyTo(entry, 0);
                BitConverter.GetBytes((ushort)entry.Length).CopyTo(entry, 4);
                entry[6] = nameLength;
                entry[7] = 26;
                BitConverter.GetBytes(vcn).CopyTo(entry, 8);
                BitConverter.GetBytes(reference).CopyTo(entry, 16);
                return entry;
            }
            const ulong Sequence = 1UL << 48;
            var list = new List<byte>();
            list.AddRange(Entry(0x10, 0, 0, Sequence | 0));
            list.AddRange(Entry(0x80, 0, 0, Sequence | 0));
            list.AddRange(Entry(0xb0, 0, 0, Sequence | 11));
            list.AddRange(Entry(0xb0, 0, 20, Sequence | 12));
            list.AddRange(Entry(0xb0, 0, 40, Sequence | 12));
            list.AddRange(Entry(0xb0, 4, 0, Sequence | 13));

            Assert.Equal(new long[] { 11, 12 },
                RawMft.AttributeListRecords(list.ToArray(), 0xb0));
        }

        /// <summary>A non-resident $BITMAP extent of one cluster run at lcn</summary>
        static byte[] BitmapExtent(ulong startVcn, ulong lastVcn, short lcn, byte clusters, ulong validLength)
        {
            var attribute = new byte[72];
            BitConverter.GetBytes(0xb0u).CopyTo(attribute, 0);
            BitConverter.GetBytes(72u).CopyTo(attribute, 4);
            attribute[8] = 1; // non-resident
            BitConverter.GetBytes(startVcn).CopyTo(attribute, 16);
            BitConverter.GetBytes(lastVcn).CopyTo(attribute, 24);
            BitConverter.GetBytes((ushort)64).CopyTo(attribute, 32);
            if (startVcn == 0)
            {
                BitConverter.GetBytes(validLength).CopyTo(attribute, 40);
                BitConverter.GetBytes(validLength).CopyTo(attribute, 48);
                BitConverter.GetBytes(validLength).CopyTo(attribute, 56);
            }
            attribute[64] = 0x21; // 1-byte length, 2-byte LCN
            attribute[65] = clusters;
            BitConverter.GetBytes(lcn).CopyTo(attribute, 66);
            return attribute;
        }

        static byte[] ListEntry(uint type, ulong vcn, ulong record)
        {
            var entry = new byte[32];
            BitConverter.GetBytes(type).CopyTo(entry, 0);
            BitConverter.GetBytes((ushort)32).CopyTo(entry, 4);
            entry[7] = 26;
            BitConverter.GetBytes(vcn).CopyTo(entry, 8);
            BitConverter.GetBytes((1UL << 48) | record).CopyTo(entry, 16);
            return entry;
        }

        static byte[] FixedUp(byte[] record)
        {
            Assert.True(MftFixup.Apply(record));
            return record;
        }

        [Fact]
        public void BitmapStartingInTheBaseRecordContinuesInListedExtensionRecords()
        {
            const long Cluster = 16;
            var list = ListEntry(0x80, 0, 0).Concat(ListEntry(0xb0, 0, 0)).Concat(ListEntry(0xb0, 1, 11)).ToArray();
            var baseRecord = FixedUp(FakeMft.Record(1024, attributes: new[]
            {
                FakeMft.Resident(0x20, list),
                BitmapExtent(0, 0, 100, 1, 2 * Cluster)
            }));
            var extension = FixedUp(FakeMft.Record(1024, baseReference: 1UL << 48, attributes: new[]
            {
                BitmapExtent(1, 1, 200, 1, 0)
            }));
            var disk = new Dictionary<long, byte>
            {
                [100 * Cluster] = 0xaa,
                [200 * Cluster] = 0xbb
            };

            var bits = RawMft.ReadBitmapBytes(baseRecord, index => index == 11 ? extension : null,
                (position, buffer, offset, count) => Array.Fill(buffer, disk[position], offset, count),
                Cluster);

            Assert.Equal(Enumerable.Repeat((byte)0xaa, 16).Concat(Enumerable.Repeat((byte)0xbb, 16)), bits);
        }

        /// <summary>A non-resident $DATA extent of one cluster run at lcn</summary>
        static byte[] DataExtent(ulong startVcn, ulong lastVcn, short lcn, byte clusters, ulong size)
        {
            var attribute = BitmapExtent(startVcn, lastVcn, lcn, clusters, size);
            BitConverter.GetBytes(0x80u).CopyTo(attribute, 0);
            return attribute;
        }

        [Fact]
        public void MftDataRunsContinueInListedExtensionRecords()
        {
            const long Cluster = 16;
            var list = ListEntry(0x80, 0, 0).Concat(ListEntry(0x80, 2, 11)).ToArray();
            var baseRecord = FixedUp(FakeMft.Record(1024, attributes: new[]
            {
                FakeMft.Resident(0x20, list),
                DataExtent(0, 1, 100, 2, 4 * Cluster)
            }));
            var extension = FixedUp(FakeMft.Record(1024, baseReference: 1UL << 48, attributes: new[]
            {
                DataExtent(2, 3, 300, 2, 0)
            }));

            var (runs, size) = RawMft.MftDataRuns(baseRecord,
                (index, baseRuns) =>
                {
                    // The extension record is located through the base record's own runs
                    Assert.Single(baseRuns);
                    return index == 11 ? extension : null;
                },
                (position, buffer, offset, count) => throw new InvalidOperationException(), Cluster);

            Assert.Equal(4UL * Cluster, size);
            Assert.Equal(new[] { (100L, 2UL), (300L, 2UL) }, runs.Select(r => (r.Lcn, r.Clusters)));
        }

        [Fact]
        public void MftDataRunsWithAnUnreadableExtensionRecordFail()
        {
            var list = ListEntry(0x80, 0, 0).Concat(ListEntry(0x80, 2, 11)).ToArray();
            var baseRecord = FixedUp(FakeMft.Record(1024, attributes: new[]
            {
                FakeMft.Resident(0x20, list),
                DataExtent(0, 1, 100, 2, 64)
            }));

            Assert.Throws<System.IO.InvalidDataException>(() => RawMft.MftDataRuns(baseRecord, (_, _) => null,
                (position, buffer, offset, count) => throw new InvalidOperationException(), 16));
        }

        [Fact]
        public void BitmapWithAMissingListedExtentIsNotUsed()
        {
            const long Cluster = 16;
            var list = ListEntry(0xb0, 0, 0).Concat(ListEntry(0xb0, 1, 11)).ToArray();
            var baseRecord = FixedUp(FakeMft.Record(1024, attributes: new[]
            {
                FakeMft.Resident(0x20, list),
                BitmapExtent(0, 0, 100, 1, 2 * Cluster)
            }));

            Assert.Null(RawMft.ReadBitmapBytes(baseRecord, _ => null,
                (position, buffer, offset, count) => Array.Fill(buffer, (byte)0xaa, offset, count),
                Cluster));
        }

        [Fact]
        public void BitmapInABaseRecordWithoutAttributeListIsReadDirectly()
        {
            var baseRecord = FixedUp(FakeMft.Record(1024, attributes: new[]
            {
                BitmapExtent(0, 1, 100, 2, 32)
            }));

            var bits = RawMft.ReadBitmapBytes(baseRecord, _ => throw new InvalidOperationException(),
                (position, buffer, offset, count) => Array.Fill(buffer, (byte)0xcc, offset, count), 16);

            Assert.Equal(Enumerable.Repeat((byte)0xcc, 32), bits);
        }

        [Fact]
        public void RecordsBeyondTheBitmapAndPartialRecordsAreRead()
        {
            // The bitmap covers 1000 records, all free; the $MFT is longer
            var bitmap = new MftRecordBitmap(new byte[125], 1000, Record);

            Assert.Equal(1000 * Record, bitmap.NextSpan(0, 1 << 20, out var free));
            Assert.True(free);

            Assert.Equal(1 << 20, bitmap.NextSpan(1000L * Record, 1 << 20, out free));
            Assert.False(free);

            Assert.Equal(Record - 100, bitmap.NextSpan(100, 1 << 20, out free));
            Assert.False(free);

            Assert.Equal(300, bitmap.NextSpan(0, 300, out free));
            Assert.False(free);
        }
    }
}
