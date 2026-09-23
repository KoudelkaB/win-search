using System;
using System.IO;

namespace search.Core
{
    /// <summary>
    /// A $MFT byte stream that knows where free (all-zero) records lie ahead. The
    /// parser skips such spans instead of reading, zeroing and rejecting every record.
    /// </summary>
    public interface IMftZeroSpans
    {
        /// <summary>
        /// Bytes from the current position known to be zero (free records); 0 when
        /// data follows. May block to learn it (a pipe reads the next frame header).
        /// </summary>
        long ZeroBytesAhead { get; }

        /// <summary>Consume count bytes of the zero span ahead without copying them</summary>
        void SkipZeros(long count);
    }

    /// <summary>
    /// Decodes the framed $MFT payload of ServicePipe.MftFramesProtocolVersion (and of
    /// the broker): a sequence of little-endian int64 frame headers summing up to the
    /// $MFT length, each either n > 0 followed by n data bytes, or n &lt; 0 standing
    /// for -n zero bytes that are not transmitted. Free $MFT records - gigabytes on a
    /// volume that once held many more files - so never cross the pipe.
    /// Read expands zero frames, so the stream is a drop-in for the raw payload.
    /// </summary>
    public sealed class MftFrameStream : Stream, IMftZeroSpans
    {
        readonly Stream inner;
        long remaining; // Of the whole payload
        long frame;     // Bytes left in the current frame
        bool zeroFrame;

        public MftFrameStream(Stream inner, long length)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            remaining = length;
        }

        /// <summary>Encode a data frame</summary>
        public static void WriteData(Stream s, byte[] buffer, int count)
        {
            if (count <= 0) return;
            ServicePipe.WriteInt64(s, count);
            s.Write(buffer, 0, count);
        }

        /// <summary>Encode a zero frame</summary>
        public static void WriteZeros(Stream s, long count)
        {
            if (count > 0) ServicePipe.WriteInt64(s, -count);
        }

        void NextFrame()
        {
            if (frame != 0 || remaining == 0) return;
            var n = ServicePipe.ReadInt64(inner);
            var size = n == long.MinValue ? long.MaxValue : Math.Abs(n);
            if (n == 0 || size > remaining)
                throw new InvalidDataException($"Invalid $MFT frame {n} with {remaining} bytes left.");
            zeroFrame = n < 0;
            frame = size;
        }

        public long ZeroBytesAhead
        {
            get
            {
                NextFrame();
                return remaining > 0 && zeroFrame ? frame : 0;
            }
        }

        public void SkipZeros(long count)
        {
            while (count > 0)
            {
                NextFrame();
                if (remaining == 0 || !zeroFrame)
                    throw new InvalidOperationException("No zero span ahead to skip.");
                var step = Math.Min(count, frame);
                frame -= step;
                remaining -= step;
                count -= step;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, remaining);
            if (count <= 0) return 0;
            NextFrame();
            var n = (int)Math.Min(count, frame);
            if (zeroFrame)
                Array.Clear(buffer, offset, n);
            else
            {
                n = inner.Read(buffer, offset, n);
                if (n <= 0) throw new EndOfStreamException("The $MFT stream ended prematurely.");
            }
            frame -= n;
            remaining -= n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
