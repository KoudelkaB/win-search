using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace search.Core
{
    /// <summary>
    /// Read-only access to the raw $MFT of an NTFS volume.
    /// Opening the volume handle requires administrator rights - this is the only
    /// privileged operation in the whole application and is shared by the app
    /// (direct read, elevated broker) and the WinSearchService service.
    /// </summary>
    public sealed class RawMft : IDisposable
    {
        const uint AttributeAttributeList = 0x20;
        const uint AttributeData = 0x80;
        const uint AttributeBitmap = 0xb0;
        const uint AttributeTerminator = 0xffffffff;

        readonly NativeVolume volume;
        readonly List<DataRun> runs;
        readonly MftRecordBitmap bitmap;

        RawMft(NativeVolume volume, List<DataRun> runs, long length, MftRecordBitmap bitmap)
        {
            this.volume = volume;
            this.runs = runs;
            this.bitmap = bitmap;
            Length = length;
        }

        public int BytesPerMftRecord => volume.BytesPerMftRecord;

        /// <summary>
        /// Size of the $MFT unnamed data stream in bytes
        /// </summary>
        public long Length { get; }

        /// <summary>
        /// Open the $MFT of the volume mounted at e.g. "C:\".
        /// Throws on non-NTFS volumes and when the process lacks the rights to open the volume.
        /// </summary>
        public static RawMft Open(string volumeMountPoint)
        {
            var volume = NativeVolume.Open(volumeMountPoint);
            try
            {
                var record = new byte[volume.BytesPerMftRecord];
                volume.Read(checked(volume.MftStartLcn * volume.BytesPerCluster), record, 0, record.Length);
                if (!MftFixup.Apply(record))
                    throw new InvalidDataException("The $MFT file record is corrupt.");

                var (runs, dataSize) = MftDataRuns(record);
                var length = checked((long)dataSize);
                return new RawMft(volume, runs, length, TryReadBitmap(volume, record, runs, length));
            }
            catch
            {
                volume.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Sequential read-only stream over the logical $MFT byte stream, walked
        /// run-by-run with sparse runs zero-filled so record indexes stay aligned.
        /// Only the caller's buffer is filled - the whole $MFT (possibly GBs) is
        /// never held in memory. The stream stays valid while this RawMft is open.
        /// </summary>
        public Stream CreateStream() => new MftDataStream(this);

        /// <summary>
        /// Stream the logical $MFT byte stream to the sink as (buffer, usedCount), free
        /// records included as zero bytes (the unframed ServicePipe.ProtocolVersion).
        /// </summary>
        public void CopyTo(Action<byte[], int> sink, int chunkSize = 1 << 23)
        {
            byte[] zero = null;
            CopyTo(sink, count =>
            {
                zero ??= new byte[chunkSize];
                while (count > 0)
                {
                    var n = (int)Math.Min(count, zero.Length);
                    sink(zero, n);
                    count -= n;
                }
            }, chunkSize);
        }

        /// <summary>
        /// Stream the logical $MFT byte stream as data chunks (buffer, usedCount) and
        /// zero spans (byteCount) of free records that are not read at all - the zero
        /// sink can encode them in a few bytes instead of gigabytes (MftFrameStream).
        /// Two chunkSize buffers are held in memory: the next chunk is read from the disk
        /// while a sink still writes the previous one to the pipe - read one, then write
        /// it, took the sum of both times. Sinks are called in order, never concurrently.
        /// </summary>
        public void CopyTo(Action<byte[], int> data, Action<long> zeros, int chunkSize = 1 << 23)
        {
            using var stream = new MftDataStream(this);
            var buffers = new[] { new byte[chunkSize], new byte[chunkSize] };
            var pending = Task.CompletedTask;
            var turn = 0;
            try
            {
                while (true)
                {
                    var skipped = 0L;
                    long ahead;
                    while ((ahead = stream.ZeroBytesAhead) > 0)
                    {
                        stream.SkipZeros(ahead);
                        skipped += ahead;
                    }
                    if (skipped > 0)
                    {
                        pending.GetAwaiter().GetResult();
                        pending = Task.Run(() => zeros(skipped));
                    }

                    var buffer = buffers[turn];
                    var count = 0;
                    int read;
                    while (count < buffer.Length && (count == 0 || stream.ZeroBytesAhead == 0)
                        && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
                        count += read;
                    // The other buffer's write must finish before a sink sees this one
                    pending.GetAwaiter().GetResult();
                    if (count == 0) break;
                    pending = Task.Run(() => data(buffer, count));
                    turn ^= 1;
                }
            }
            catch
            {
                // Never return while a sink may still be writing from a buffer
                try { pending.Wait(); } catch { }
                throw;
            }
        }

        public void Dispose() => volume.Dispose();

        sealed class MftDataStream : Stream, IMftZeroSpans
        {
            readonly RawMft mft;
            int run;
            long runPosition; // Bytes consumed of the current run
            long position;    // Logical position in the $MFT data stream

            public MftDataStream(RawMft mft) => this.mft = mft;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => mft.Length;
            public override long Position { get => position; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                count = (int)Math.Min(count, mft.Length - position);
                if (count <= 0) return 0;

                var bytesPerCluster = (long)mft.volume.BytesPerCluster;
                while (run < mft.runs.Count)
                {
                    var current = mft.runs[run];
                    var runBytes = checked((long)current.Clusters * bytesPerCluster);
                    if (runPosition >= runBytes)
                    {
                        run++;
                        runPosition = 0;
                        continue;
                    }

                    var chunk = (int)Math.Min(count, runBytes - runPosition);
                    var free = false;
                    if (mft.bitmap != null)
                        chunk = mft.bitmap.NextSpan(position, chunk, out free);
                    if (current.IsSparse || free)
                        Array.Clear(buffer, offset, chunk);
                    else
                        mft.volume.Read(checked((ulong)(current.Lcn * bytesPerCluster + runPosition)), buffer, offset, chunk);
                    runPosition += chunk;
                    position += chunk;
                    return chunk;
                }

                throw new InvalidDataException("The $MFT data runs do not cover the whole $MFT.");
            }

            /// <summary>
            /// The free span (bitmap) or sparse run ahead, within the current run
            /// </summary>
            public long ZeroBytesAhead
            {
                get
                {
                    var bytesPerCluster = (long)mft.volume.BytesPerCluster;
                    while (run < mft.runs.Count && runPosition >= checked((long)mft.runs[run].Clusters * bytesPerCluster))
                    {
                        run++;
                        runPosition = 0;
                    }
                    if (run >= mft.runs.Count || position >= mft.Length) return 0;
                    var current = mft.runs[run];
                    var max = Math.Min(checked((long)current.Clusters * bytesPerCluster) - runPosition,
                        mft.Length - position);
                    if (current.IsSparse) return max;
                    if (mft.bitmap == null) return 0;
                    return mft.bitmap.FreeBytesAt(position, max);
                }
            }

            public void SkipZeros(long count)
            {
                var bytesPerCluster = (long)mft.volume.BytesPerCluster;
                while (count > 0)
                {
                    if (run >= mft.runs.Count || position >= mft.Length)
                        throw new InvalidOperationException("Skipped past the end of the $MFT.");
                    var runBytes = checked((long)mft.runs[run].Clusters * bytesPerCluster);
                    if (runPosition >= runBytes)
                    {
                        run++;
                        runPosition = 0;
                        continue;
                    }
                    var step = Math.Min(count, Math.Min(runBytes - runPosition, mft.Length - position));
                    runPosition += step;
                    position += step;
                    count -= step;
                }
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// The in-use bitmap of the $MFT records, or null when it cannot be read - then
        /// every record is read as before
        /// </summary>
        static MftRecordBitmap TryReadBitmap(NativeVolume volume, byte[] baseRecord,
            List<DataRun> mftRuns, long mftLength)
        {
            try
            {
                var bits = ReadBitmapBytes(baseRecord, index => ReadMftRecord(volume, mftRuns, index),
                    (position, buffer, offset, count) => volume.Read(checked((ulong)position), buffer, offset, count),
                    (long)volume.BytesPerCluster);
                var recordCount = mftLength / volume.BytesPerMftRecord;
                if (bits == null || (long)bits.Length * 8 < recordCount) return null;
                return new MftRecordBitmap(bits, recordCount, volume.BytesPerMftRecord);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Read count bytes at the absolute volume byte position</summary>
        internal delegate void VolumeReader(long position, byte[] buffer, int offset, int count);

        /// <summary>
        /// The $MFT's own $BITMAP value from all its extents. A $MFT fragmented enough to
        /// need an attribute list (seen on a 7 GB $MFT) may keep the bitmap in extension
        /// records - or start it in the base record and continue it there - so when a list
        /// exists, every record it names for the bitmap is used. readRecord returns a
        /// fixed-up FILE record by index (null when unreadable). Null when any extent is
        /// missing.
        /// </summary>
        internal static byte[] ReadBitmapBytes(byte[] baseRecord, Func<long, byte[]> readRecord,
            VolumeReader read, long bytesPerCluster)
        {
            var records = new List<byte[]>();
            if (FindUnnamedAttributes(baseRecord, AttributeAttributeList).Count == 0)
                records.Add(baseRecord);
            else
            {
                var list = ReadAttributeValue(new[] { baseRecord }, AttributeAttributeList, read, bytesPerCluster);
                if (list == null) return null;
                foreach (var index in AttributeListRecords(list, AttributeBitmap))
                {
                    var record = index == 0 ? baseRecord : readRecord(index);
                    if (record == null) return null;
                    records.Add(record);
                }
            }
            return ReadAttributeValue(records, AttributeBitmap, read, bytesPerCluster);
        }

        /// <summary>
        /// Distinct $MFT record indexes that an $ATTRIBUTE_LIST names for the unnamed
        /// attribute of the given type, in list (= starting VCN) order
        /// </summary>
        internal static List<long> AttributeListRecords(ReadOnlySpan<byte> list, uint attributeType)
        {
            var result = new List<long>();
            var offset = 0;
            while (offset + 26 <= list.Length)
            {
                var length = (int)U16(list[(offset + 4)..]);
                if (length < 26 || offset + length > list.Length)
                    break;
                if (U32(list[offset..]) == attributeType && list[offset + 6] == 0)
                {
                    var index = (long)(U64(list[(offset + 16)..]) & 0xffffffffffff);
                    if (!result.Contains(index)) result.Add(index);
                }
                offset += length;
            }
            return result;
        }

        /// <summary>
        /// One fixed-up FILE record of the $MFT by index, or null when it cannot be read
        /// </summary>
        static byte[] ReadMftRecord(NativeVolume volume, List<DataRun> mftRuns, long index)
        {
            var bytesPerRecord = volume.BytesPerMftRecord;
            var offset = checked(index * bytesPerRecord);
            var bytesPerCluster = (long)volume.BytesPerCluster;
            foreach (var run in mftRuns)
            {
                var runBytes = checked((long)run.Clusters * bytesPerCluster);
                if (offset >= runBytes)
                {
                    offset -= runBytes;
                    continue;
                }
                if (run.IsSparse || offset + bytesPerRecord > runBytes) return null;
                var record = new byte[bytesPerRecord];
                volume.Read(checked((ulong)(run.Lcn * bytesPerCluster + offset)), record, 0, record.Length);
                return MftFixup.Apply(record) ? record : null;
            }
            return null;
        }

        /// <summary>
        /// Offsets of every instance of the unnamed attribute of the given type within
        /// the record (one per VCN range it holds)
        /// </summary>
        static List<int> FindUnnamedAttributes(ReadOnlySpan<byte> record, uint attributeType)
        {
            var result = new List<int>();
            var offset = (int)U16(record[20..]);
            while (offset + 24 <= record.Length)
            {
                var type = U32(record[offset..]);
                if (type == AttributeTerminator)
                    break;

                var length = (int)U32(record[(offset + 4)..]);
                if (length < 24 || offset + length > record.Length)
                    break;

                if (type == attributeType && record[offset + 9] == 0)
                    result.Add(offset);

                offset += length;
            }
            return result;
        }

        /// <summary>
        /// Value of the unnamed attribute of the given type - resident, or non-resident
        /// read up to its valid data length, its instances (one per starting VCN) spread
        /// over the given records. Null when it is missing or not fully mapped.
        /// </summary>
        static byte[] ReadAttributeValue(IReadOnlyList<byte[]> records, uint attributeType,
            VolumeReader read, long bytesPerCluster)
        {
            var instances = new List<(byte[] Record, int Offset)>();
            foreach (var record in records)
                foreach (var at in FindUnnamedAttributes(record, attributeType))
                    instances.Add((record, at));
            if (instances.Count == 0) return null;

            var (first, firstAt) = instances[0];
            if (first[firstAt + 8] == 0)
            {
                if (instances.Count != 1) return null;
                var length = (int)U32(first.AsSpan(firstAt + 4));
                var valueLength = (int)U32(first.AsSpan(firstAt + 16));
                var valueOffset = (int)U16(first.AsSpan(firstAt + 20));
                if (valueOffset + valueLength > length) return null;
                return first.AsSpan(firstAt + valueOffset, valueLength).ToArray();
            }

            // Only the instance starting at VCN 0 carries the sizes
            byte[] value = null;
            foreach (var (record, at) in instances)
                if (record[at + 8] != 0 && U32(record.AsSpan(at + 4)) >= 64 && U64(record.AsSpan(at + 16)) == 0)
                {
                    var validLength = U64(record.AsSpan(at + 56));
                    if (validLength > int.MaxValue) return null;
                    value = new byte[(int)validLength];
                    break;
                }
            if (value == null) return null;

            var filled = 0L;
            foreach (var (record, at) in instances)
            {
                var length = (int)U32(record.AsSpan(at + 4));
                if (record[at + 8] == 0 || length < 64) return null;
                var runOffset = (int)U16(record.AsSpan(at + 32));
                if (runOffset >= length) return null;
                var position = checked((long)U64(record.AsSpan(at + 16)) * bytesPerCluster);
                foreach (var run in DecodeDataRuns(record.AsSpan(at + runOffset, length - runOffset)))
                {
                    var runBytes = checked((long)run.Clusters * bytesPerCluster);
                    var chunk = (int)Math.Clamp(value.Length - position, 0, runBytes);
                    if (chunk > 0 && !run.IsSparse)
                        read(checked(run.Lcn * bytesPerCluster), value, (int)position, chunk);
                    filled += chunk;
                    position += runBytes;
                }
            }
            return filled == value.Length ? value : null;
        }

        static (List<DataRun> Runs, ulong DataSize) MftDataRuns(ReadOnlySpan<byte> record)
        {
            var offset = (int)U16(record[20..]);
            while (offset + 24 <= record.Length)
            {
                var type = U32(record[offset..]);
                if (type == AttributeTerminator)
                    break;

                var length = (int)U32(record[(offset + 4)..]);
                if (length < 24 || offset + length > record.Length)
                    break;

                if (type == AttributeData && record[offset + 9] == 0)
                {
                    if (record[offset + 8] == 0 || length < 64)
                        break;

                    var runOffset = (int)U16(record[(offset + 32)..]);
                    if (runOffset >= length)
                        break;

                    return (DecodeDataRuns(record.Slice(offset + runOffset, length - runOffset)), U64(record[(offset + 48)..]));
                }

                offset += length;
            }

            throw new InvalidDataException("Unable to locate the $MFT data runs.");
        }

        static List<DataRun> DecodeDataRuns(ReadOnlySpan<byte> runs)
        {
            var result = new List<DataRun>();
            var lcn = 0L;
            var offset = 0;
            while (offset < runs.Length && runs[offset] != 0)
            {
                var lengthSize = runs[offset] & 0xf;
                var offsetSize = runs[offset] >> 4;
                offset++;
                if (lengthSize == 0 || offset + lengthSize + offsetSize > runs.Length)
                    break;

                var clusters = ReadUnsigned(runs.Slice(offset, lengthSize));
                offset += lengthSize;

                if (offsetSize == 0)
                {
                    result.Add(new DataRun(0, clusters, true));
                }
                else
                {
                    lcn = checked(lcn + ReadSigned(runs.Slice(offset, offsetSize)));
                    offset += offsetSize;
                    result.Add(new DataRun(lcn, clusters, false));
                }
            }

            return result;
        }

        static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
        {
            var value = 0UL;
            for (var i = bytes.Length - 1; i >= 0; i--)
                value = (value << 8) | bytes[i];
            return value;
        }

        static long ReadSigned(ReadOnlySpan<byte> bytes)
        {
            var value = (long)(sbyte)bytes[^1];
            for (var i = bytes.Length - 2; i >= 0; i--)
                value = (value << 8) | bytes[i];
            return value;
        }

        static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        sealed record DataRun(long Lcn, ulong Clusters, bool IsSparse);

        sealed class NativeVolume : IDisposable
        {
            readonly SafeFileHandle handle;
            byte[] scratch = Array.Empty<byte>();

            NativeVolume(SafeFileHandle handle, ushort bytesPerSector, ulong bytesPerCluster, ulong mftStartLcn, int bytesPerMftRecord)
            {
                this.handle = handle;
                BytesPerSector = bytesPerSector;
                BytesPerCluster = bytesPerCluster;
                MftStartLcn = mftStartLcn;
                BytesPerMftRecord = bytesPerMftRecord;
            }

            public ushort BytesPerSector { get; }
            public ulong BytesPerCluster { get; }
            public ulong MftStartLcn { get; }
            public int BytesPerMftRecord { get; }

            public static NativeVolume Open(string volumeMountPoint)
            {
                var volumeName = new StringBuilder(1024);
                if (!GetVolumeNameForVolumeMountPoint(volumeMountPoint, volumeName, volumeName.Capacity))
                    throw new IOException($"Unable to resolve volume name for {volumeMountPoint}.");

                var volume = volumeName.ToString().TrimEnd('\\');
                var handle = CreateFile(volume, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                    throw new IOException($"Unable to open volume {volumeMountPoint}. Make sure the process has administrator privileges.");

                try
                {
                    var boot = new byte[512];
                    ReadAligned(handle, 0, boot, 0, boot.Length);
                    return FromBootSector(handle, boot);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            public void Read(ulong absolutePosition, byte[] buffer, int offset, int count)
            {
                if (count == 0) return;

                var sector = BytesPerSector;
                var alignedStart = absolutePosition / sector * sector;
                var skip = checked((int)(absolutePosition - alignedStart));
                var alignedLength = Align(skip + count, sector);
                if (scratch.Length < alignedLength)
                    scratch = new byte[alignedLength];
                ReadAligned(handle, alignedStart, scratch, 0, alignedLength);
                Buffer.BlockCopy(scratch, skip, buffer, offset, count);
            }

            public void Dispose() => handle.Dispose();

            static NativeVolume FromBootSector(SafeFileHandle handle, byte[] boot)
            {
                if (BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(3)) != 0x202020205346544e)
                    throw new InvalidDataException("This is not an NTFS disk.");

                var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
                var sectorsPerCluster = boot[13];
                var bytesPerCluster = checked((ulong)bytesPerSector * sectorsPerCluster);
                var mftStartLcn = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(48));
                var clustersPerMftRecord = boot[64];
                var bytesPerMftRecord = clustersPerMftRecord >= 128
                    ? 1 << (256 - clustersPerMftRecord)
                    : checked(clustersPerMftRecord * bytesPerSector * sectorsPerCluster);

                return new NativeVolume(handle, bytesPerSector, bytesPerCluster, mftStartLcn, bytesPerMftRecord);
            }

            static int Align(int value, int alignment)
                => checked(((value + alignment - 1) / alignment) * alignment);

            static void ReadAligned(SafeFileHandle handle, ulong absolutePosition, byte[] buffer, int offset, int count)
            {
                var read = 0;
                while (read < count)
                {
                    var chunk = count - read;
                    var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        var address = pinned.AddrOfPinnedObject() + offset + read;
                        var overlapped = new NativeOverlapped(checked(absolutePosition + (ulong)read));
                        if (!ReadFile(handle, address, checked((uint)chunk), out var bytesRead, ref overlapped) || bytesRead == 0)
                            throw new IOException("Unable to read volume information.");
                        read += checked((int)bytesRead);
                    }
                    finally
                    {
                        pinned.Free();
                    }
                }
            }
        }

        [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false)]
        static extern bool GetVolumeNameForVolumeMountPoint(string volumeName, StringBuilder uniqueVolumeName, int uniqueNameBufferCapacity);

        [DllImport("kernel32", CharSet = CharSet.Auto, BestFitMapping = false)]
        static extern SafeFileHandle CreateFile(string lpFileName, System.IO.FileAccess fileAccess, System.IO.FileShare fileShare, IntPtr lpSecurityAttributes, FileMode fileMode, int dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32", CharSet = CharSet.Auto)]
        static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        enum FileMode : int
        {
            Open = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NativeOverlapped
        {
            IntPtr privateLow;
            IntPtr privateHigh;
            ulong offset;
            IntPtr eventHandle;

            public NativeOverlapped(ulong offset)
            {
                privateLow = IntPtr.Zero;
                privateHigh = IntPtr.Zero;
                this.offset = offset;
                eventHandle = IntPtr.Zero;
            }
        }
    }
}
