using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using search.Core;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class MftFrameStreamTests
    {
        /// <summary>
        /// Encode an image the way the service does: zero frames for the given records,
        /// data frames of at most dataChunk bytes for the rest (the partial tail included)
        /// </summary>
        static byte[] Encode(byte[] image, int bytesPerRecord, Func<long, bool> zeroRecord, int dataChunk = 3000)
        {
            var output = new MemoryStream();
            var records = image.Length / bytesPerRecord;
            var position = 0L;
            while (position < image.Length)
            {
                var record = position / bytesPerRecord;
                if (record < records && zeroRecord(record))
                {
                    var end = record;
                    while (end < records && zeroRecord(end)) end++;
                    MftFrameStream.WriteZeros(output, (end - record) * bytesPerRecord);
                    position = end * bytesPerRecord;
                    continue;
                }
                var stop = record;
                while (stop < records && !zeroRecord(stop)) stop++;
                var limit = stop < records ? stop * bytesPerRecord : image.Length;
                var count = (int)Math.Min(dataChunk, limit - position);
                MftFrameStream.WriteData(output, image.AsSpan((int)position, count).ToArray(), count);
                position += count;
            }
            output.Position = 0;
            return output.ToArray();
        }

        static byte[] Image(int records, int bytesPerRecord, Func<long, bool> zeroRecord, int tail = 0)
        {
            var image = new byte[records * bytesPerRecord + tail];
            new Random(1).NextBytes(image);
            for (var r = 0; r < records; r++)
                if (zeroRecord(r)) Array.Clear(image, r * bytesPerRecord, bytesPerRecord);
            return image;
        }

        [Fact]
        public void ReadExpandsZeroFramesToTheOriginalBytes()
        {
            Func<long, bool> zero = r => r is >= 3 and < 11 || r == 15;
            var image = Image(20, 100, zero, tail: 37);
            var stream = new MftFrameStream(new MemoryStream(Encode(image, 100, zero)), image.Length);

            var decoded = new MemoryStream();
            var buffer = new byte[77]; // Odd size - reads cross frame boundaries
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                decoded.Write(buffer, 0, read);

            Assert.Equal(image, decoded.ToArray());
        }

        [Fact]
        public void ZeroSpansAreReportedAndSkippedWithoutData()
        {
            Func<long, bool> zero = r => r is >= 2 and < 6;
            var image = Image(8, 100, zero);
            var stream = new MftFrameStream(new MemoryStream(Encode(image, 100, zero)), image.Length);

            Assert.Equal(0, stream.ZeroBytesAhead);
            var head = new byte[200];
            stream.ReadExactly(head);
            Assert.Equal(image.AsSpan(0, 200).ToArray(), head);

            Assert.Equal(400, stream.ZeroBytesAhead);
            stream.SkipZeros(400);
            Assert.Equal(0, stream.ZeroBytesAhead);
            var rest = new byte[200];
            stream.ReadExactly(rest);
            Assert.Equal(image.AsSpan(600).ToArray(), rest);
            Assert.Equal(0, stream.Read(rest, 0, rest.Length));
        }

        [Fact]
        public void FrameLongerThanThePayloadIsRejected()
        {
            var output = new MemoryStream();
            MftFrameStream.WriteZeros(output, 500);
            output.Position = 0;
            var stream = new MftFrameStream(output, 400);

            Assert.Throws<InvalidDataException>(() => stream.ZeroBytesAhead);
        }

        [Fact]
        public void ChunkReaderNeverHandsSkippedRecordsToProcess()
        {
            Func<long, bool> zero = r => r is >= 5 and < 15;
            var image = Image(20, 100, zero);
            var stream = new MftFrameStream(new MemoryStream(Encode(image, 100, zero)), image.Length);
            var seen = new NonBlocking.ConcurrentDictionary<int, byte[]>();

            var total = MftChunkReader.Read(stream, 100, image.Length, (buffer, first, count) =>
            {
                for (var i = 0; i < count; i++)
                    Assert.True(seen.TryAdd(first + i, buffer.AsSpan(i * 100, 100).ToArray()));
            }, chunkBytes: 300);

            Assert.Equal(20, total);
            foreach (var record in Enumerable.Range(0, 20).Where(r => !zero(r)))
                Assert.Equal(image.AsSpan(record * 100, 100).ToArray(), seen[record]);
            // A chunk that started on data may run into the zero span (expanded zeros);
            // the rest of the span is skipped
            var zeroed = seen.Keys.Where(r => zero(r)).ToArray();
            Assert.InRange(zeroed.Length, 0, 2);
            Assert.All(zeroed, r => Assert.All(seen[r], b => Assert.Equal(0, b)));
        }

        [Fact]
        public void FramedPayloadParsesToTheSameTreeAsTheRawOne()
        {
            // A large free block between the directory and its file, like a $MFT that
            // once held many more files
            var mft = new FakeMft(1024).AddEmpty(5).AddRoot()
                .AddRecord(directory: true, attributes: new[] { FakeMft.FileName(FakeMft.RootEntry, "Docs") }) // 6
                .AddEmpty(600)
                .AddRecord(attributes: new[] { FakeMft.FileName(6, "a.txt"), FakeMft.ResidentData(123) });   // 607
            var image = mft.Image();
            Func<long, bool> zero = r => r < 5 || r is >= 7 and < 607;

            var raw = mft.Parse(chunkBytes: 64 * 1024);
            var framed = mft.Parse(chunkBytes: 64 * 1024, stream:
                new MftFrameStream(new MemoryStream(Encode(image, 1024, zero, dataChunk: 5000)), image.Length));

            static string[] Shape(IEnumerable<INode> nodes) => nodes
                .Select(n => $"{n.FullName}|{n.Size}|{n.Count}|{n.IsDirectory}").OrderBy(x => x).ToArray();
            Assert.Equal(3, framed.Count);
            Assert.Equal(Shape(raw), Shape(framed));
            Assert.Contains(framed, n => n.FullName == @"Q:\Docs\a.txt" && n.Size == 123);
        }
    }
}
