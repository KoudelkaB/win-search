using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        const uint AttributeData = 0x80;
        const uint AttributeTerminator = 0xffffffff;

        readonly NativeVolume volume;
        readonly List<DataRun> runs;

        RawMft(NativeVolume volume, List<DataRun> runs, long length)
        {
            this.volume = volume;
            this.runs = runs;
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
                return new RawMft(volume, runs, checked((long)dataSize));
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
        /// Stream the logical $MFT byte stream to the sink as (buffer, usedCount).
        /// Only chunkSize bytes are held in memory.
        /// </summary>
        public void CopyTo(Action<byte[], int> sink, int chunkSize = 1 << 20)
        {
            using var stream = CreateStream();
            var chunk = new byte[chunkSize];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
                sink(chunk, count);
        }

        public void Dispose() => volume.Dispose();

        sealed class MftDataStream : Stream
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
                    if (current.IsSparse)
                        Array.Clear(buffer, offset, chunk);
                    else
                        mft.volume.Read(checked((ulong)(current.Lcn * bytesPerCluster + runPosition)), buffer, offset, chunk);
                    runPosition += chunk;
                    position += chunk;
                    return chunk;
                }

                throw new InvalidDataException("The $MFT data runs do not cover the whole $MFT.");
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
