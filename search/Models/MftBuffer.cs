using System;
using System.IO;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// The whole $MFT held in 16 MB segments (a multiple of any record size, no single huge allocation)
    /// </summary>
    internal sealed class MftBuffer
    {
        const int SegmentShift = 24;
        const int SegmentSize = 1 << SegmentShift;

        readonly byte[][] segments;
        readonly int bytesPerRecord;

        public MftBuffer(int bytesPerRecord, long length)
        {
            if (bytesPerRecord <= 0 || SegmentSize % bytesPerRecord != 0)
                throw new InvalidDataException($"Unsupported MFT record size: {bytesPerRecord}.");

            this.bytesPerRecord = bytesPerRecord;
            Length = length / bytesPerRecord * bytesPerRecord;
            RecordCount = checked((int)(Length / bytesPerRecord));
            segments = new byte[(int)((Length + SegmentSize - 1) >> SegmentShift)][];
            for (var i = 0; i < segments.Length; i++)
                segments[i] = new byte[SegmentSize];
        }

        public long Length { get; }
        public int RecordCount { get; }

        public Span<byte> Record(int index)
        {
            var (segment, offset) = Locate((long)index * bytesPerRecord);
            return segment.AsSpan(offset, bytesPerRecord);
        }

        public (byte[] Segment, int Offset) Locate(long position)
            => (segments[position >> SegmentShift], (int)(position & (SegmentSize - 1)));

        /// <summary>
        /// Fill from a directly opened volume (the process itself has the rights to read it)
        /// </summary>
        public static MftBuffer From(RawMft raw)
        {
            var buffer = new MftBuffer(raw.BytesPerMftRecord, raw.Length);
            var position = 0L;
            raw.CopyTo((chunk, count) => position = buffer.Write(position, chunk, count));
            if (position < buffer.Length)
                throw new InvalidDataException("The $MFT stream ended prematurely.");
            return buffer;
        }

        /// <summary>
        /// Fill from a pipe stream carrying exactly length raw $MFT bytes (service or broker)
        /// </summary>
        public static MftBuffer From(Stream stream, int bytesPerRecord, long length)
        {
            var buffer = new MftBuffer(bytesPerRecord, length);
            var position = 0L;
            while (position < buffer.Length)
            {
                var (segment, offset) = buffer.Locate(position);
                var count = (int)Math.Min(segment.Length - offset, buffer.Length - position);
                stream.ReadExactly(segment, offset, count);
                position += count;
            }

            // Consume the partial-record tail (Length is truncated to whole records)
            // so the sender never blocks on an unread remainder
            var tail = length - buffer.Length;
            if (tail > 0)
                stream.ReadExactly(new byte[tail]);

            return buffer;
        }

        long Write(long position, byte[] src, int count)
        {
            var copied = 0;
            while (copied < count && position < Length)
            {
                var (segment, offset) = Locate(position);
                var chunk = (int)Math.Min(Math.Min((long)(segment.Length - offset), count - copied), Length - position);
                Buffer.BlockCopy(src, copied, segment, offset, chunk);
                copied += chunk;
                position += chunk;
            }
            return position;
        }
    }
}
