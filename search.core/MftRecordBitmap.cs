using System;

namespace search.Core
{
    /// <summary>
    /// The $MFT:$BITMAP - one bit per FILE record, set while the record is in use.
    /// The $MFT never shrinks: a volume that once held millions more files keeps
    /// gigabytes of free records (seen: 7.5M records, 5.2M of them in one free block),
    /// and reading them from the disk only to reject them was most of the load time
    /// (that 7.3 GB $MFT: 10 s read directly, 2.9 s with the free spans skipped).
    /// Free spans are therefore zero-filled instead of read - a zeroed record carries
    /// no FILE signature and is skipped by the parser exactly like a free record.
    /// A record allocated after the bitmap was read is reported by the USN journal,
    /// which the app starts watching before the scan, just like a record changed after
    /// the stream passed it.
    /// </summary>
    public sealed class MftRecordBitmap
    {
        /// <summary>
        /// Shorter free runs are read with their neighbours - splitting one large disk
        /// read into many small ones would cost more than the few free records saved
        /// </summary>
        public const int MinSkipRecords = 256;

        readonly byte[] bits;
        readonly long bitCount;
        readonly int bytesPerRecord;

        public MftRecordBitmap(byte[] bits, long bitCount, int bytesPerRecord)
        {
            if (bytesPerRecord <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerRecord));
            this.bits = bits ?? throw new ArgumentNullException(nameof(bits));
            this.bitCount = Math.Clamp(bitCount, 0, (long)bits.Length * 8);
            this.bytesPerRecord = bytesPerRecord;
        }

        /// <summary>
        /// Records beyond the bitmap count as used - only a known free record is skipped
        /// </summary>
        bool IsFree(long record)
            => record < bitCount && (bits[record >> 3] & (1 << (int)(record & 7))) == 0;

        /// <summary>
        /// Length of the span starting at the logical $MFT byte position and at most
        /// maxBytes long that is either all free (free = true, zero-fill it) or has to
        /// be read (free = false). A used span stops where a free run of at least
        /// MinSkipRecords starts, a free span where the next used record starts.
        /// A partial record is always read.
        /// </summary>
        public int NextSpan(long position, int maxBytes, out bool free)
        {
            free = false;
            if (maxBytes <= 0) return 0;
            var within = (int)(position % bytesPerRecord);
            if (within != 0) return Math.Min(maxBytes, bytesPerRecord - within);

            var first = position / bytesPerRecord;
            var records = maxBytes / bytesPerRecord;
            if (records == 0) return maxBytes;

            var freeRun = FreeRecordsAt(first, records);
            if (IsSkippable(freeRun, records))
            {
                free = true;
                return checked((int)(freeRun * bytesPerRecord));
            }

            //Read up to the start of the next long free run
            var run = 0L;
            for (var r = freeRun; r < records; r++)
            {
                if (!IsFree(first + r))
                {
                    run = 0;
                    continue;
                }
                if (++run == MinSkipRecords)
                    return checked((int)((r - run + 1) * bytesPerRecord));
            }
            //The used span may end in a short free run - read it together with the rest.
            //A partial-record tail beyond the whole records is the next call's span.
            return checked((int)(records * bytesPerRecord));
        }

        /// <summary>
        /// Bytes of the skippable free span starting at the position (at most maxBytes),
        /// 0 when data follows - the free half of NextSpan, but cheap in used areas: it
        /// stops at the first used record instead of looking for the next free run.
        /// </summary>
        public long FreeBytesAt(long position, long maxBytes)
        {
            if (maxBytes <= 0 || position % bytesPerRecord != 0) return 0;
            var records = maxBytes / bytesPerRecord;
            var freeRun = FreeRecordsAt(position / bytesPerRecord, records);
            return freeRun > 0 && IsSkippable(freeRun, records) ? freeRun * bytesPerRecord : 0;
        }

        long FreeRecordsAt(long first, long records)
        {
            var run = 0L;
            while (run < records && IsFree(first + run)) run++;
            return run;
        }

        //A short free run that reaches the end of the allowed span is still skipped whole:
        //the caller's span ends there anyway (run or chunk boundary)
        static bool IsSkippable(long freeRun, long records)
            => freeRun >= MinSkipRecords || freeRun == records;
    }
}
