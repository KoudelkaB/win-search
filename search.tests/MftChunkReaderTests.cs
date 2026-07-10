using System;
using System.Collections.Concurrent;
using System.IO;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class MftChunkReaderTests
    {
        [Theory]
        [InlineData(100, 256)]   // several records per chunk
        [InlineData(100, 100)]   // exactly one record per chunk
        [InlineData(1024, 64)]   // a single record larger than the chunk size
        [InlineData(1000, 4096)] // record size that is no power of two
        public void DeliversEveryRecordExactlyOnce(int bytesPerRecord, int chunkBytes)
        {
            const int records = 10;
            var data = new byte[records * bytesPerRecord + 50]; // + partial tail
            for (var r = 0; r < records; r++)
                Array.Fill(data, (byte)(r + 1), r * bytesPerRecord, bytesPerRecord);

            var stream = new MemoryStream(data);
            var seen = new NonBlocking.ConcurrentDictionary<int, byte[]>();
            var total = MftChunkReader.Read(stream, bytesPerRecord, data.Length, (buffer, first, count) =>
            {
                for (var i = 0; i < count; i++)
                    Assert.True(seen.TryAdd(first + i, buffer.AsSpan(i * bytesPerRecord, bytesPerRecord).ToArray()));
            }, chunkBytes);

            Assert.Equal(records, total);
            Assert.Equal(records, seen.Count);
            for (var r = 0; r < records; r++)
                Assert.All(seen[r], b => Assert.Equal((byte)(r + 1), b));
            Assert.Equal(stream.Length, stream.Position); // the tail was drained too
        }

        [Fact]
        public void ZeroRecordsStillDrainTheTail()
        {
            var stream = new MemoryStream(new byte[300]);
            var total = MftChunkReader.Read(stream, 1024, 300, (_, _, _) => Assert.Fail("no chunk expected"));
            Assert.Equal(0, total);
            Assert.Equal(300, stream.Position);
        }

        [Fact]
        public void RejectsInvalidRecordSizes()
        {
            Assert.Throws<InvalidDataException>(() => MftChunkReader.Read(new MemoryStream(), 0, 100, (_, _, _) => { }));
            Assert.Throws<InvalidDataException>(() => MftChunkReader.Read(new MemoryStream(), -5, 100, (_, _, _) => { }));
        }

        [Fact]
        public void ThrowsWhenTheStreamEndsPrematurely()
        {
            Assert.Throws<EndOfStreamException>(
                () => MftChunkReader.Read(new MemoryStream(new byte[500]), 100, 1000, (_, _, _) => { }));
        }

        [Fact]
        public void DrainsTheStreamEvenWhenProcessingFails()
        {
            // A pipe sender writes the full payload - a parse failure must still consume
            // the remainder, otherwise the sender blocks and the channel desyncs
            var stream = new MemoryStream(new byte[1000]);
            Assert.Throws<InvalidOperationException>(
                () => MftChunkReader.Read(stream, 100, 1000, (_, _, _) => throw new InvalidOperationException(), chunkBytes: 200));
            Assert.Equal(1000, stream.Position);
        }
    }
}
