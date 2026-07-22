using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace search.Models
{
    /// <summary>
    /// Pumps a raw $MFT byte stream through two fixed chunk buffers, so the whole
    /// table (possibly many GB) is never held in memory at once. Processing of one
    /// chunk overlaps the read of the next, and two consecutive chunks may be
    /// processed concurrently - the parse keeps up with the read instead of
    /// starting after it. Every chunk holds whole records only; any record size is
    /// supported, a chunk exceeding ChunkBytes only when a single record is larger.
    /// </summary>
    internal static class MftChunkReader
    {
        public const int DefaultChunkBytes = 1 << 23; // 8 MB (empiric minimal with best time)

        /// <summary>
        /// Read length bytes as records of bytesPerRecord bytes each, calling
        /// process(buffer, firstRecordIndex, recordCount) for every chunk.
        /// The partial-record tail beyond the last whole record is consumed too,
        /// and the stream is drained even when process throws - a pipe sender
        /// must never block on an unread remainder nor desync its channel.
        /// Returns the total number of whole records.
        /// </summary>
        public static int Read(Stream stream, int bytesPerRecord, long length,
            Action<byte[], int, int> process, int chunkBytes = DefaultChunkBytes,
            CancellationToken cancellationToken = default, bool drainOnCancellation = true)
        {
            if (bytesPerRecord <= 0)
                throw new InvalidDataException($"Unsupported MFT record size: {bytesPerRecord}.");

            var recordCount = checked((int)(length / bytesPerRecord));
            var recordsPerChunk = (int)Math.Min(Math.Max(1, chunkBytes / bytesPerRecord), Math.Max(1, recordCount));
            var buffers = new byte[2][];
            var pending = new[] { Task.CompletedTask, Task.CompletedTask };
            var consumed = 0L;
            try
            {
                var index = 0;
                var turn = 0;
                while (index < recordCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var records = Math.Min(recordsPerChunk, recordCount - index);

                    // The buffer was handed to process two chunks ago - wait before overwriting
                    pending[turn].GetAwaiter().GetResult();
                    var buffer = buffers[turn] ??= new byte[recordsPerChunk * bytesPerRecord];
                    stream.ReadExactly(buffer, 0, records * bytesPerRecord);
                    consumed += (long)records * bytesPerRecord;

                    var first = index;
                    pending[turn] = Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        process(buffer, first, records);
                    }, cancellationToken);
                    index += records;
                    turn ^= 1;
                }

                pending[0].GetAwaiter().GetResult();
                pending[1].GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                try { Task.WaitAll(pending); } catch { }
                //The broker reuses one framed pipe, so its unread payload must be consumed
                //before another command. Direct/service streams are disposable and skip it.
                if (drainOnCancellation)
                    try { Drain(stream, length - consumed); } catch { }
                throw;
            }
            catch
            {
                try { Task.WaitAll(pending); } catch { }
                try { Drain(stream, length - consumed); } catch { }
                throw;
            }

            Drain(stream, length - consumed);
            return recordCount;
        }

        static void Drain(Stream stream, long count)
        {
            if (count <= 0) return;
            var buffer = new byte[(int)Math.Min(count, 1 << 16)];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException("The $MFT stream ended prematurely.");
                count -= read;
            }
        }
    }
}
