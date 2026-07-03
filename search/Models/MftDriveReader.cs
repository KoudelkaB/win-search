using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace search.Models
{
    static class MftDriveReader
    {
        const uint RootEntryNumber = 5;

        const uint AttributeStandardInformation = 0x10;
        const uint AttributeAttributeList = 0x20;
        const uint AttributeFileName = 0x30;
        const uint AttributeData = 0x80;
        const uint AttributeTerminator = 0xffffffff;
        const ulong FileReferenceMask = 0xffffffffffff;
        const ulong MaxFileTime = 2650467743999999999; // DateTime.MaxValue.ToFileTimeUtc()

        public static IEnumerable<INode> GetNodes(DriveInfo drive)
        {
            if (drive?.IsReady != true || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Empty<INode>();

            MftBuffer mft;
            using (var volume = NativeVolume.Open(drive))
                mft = ReadMft(volume);

            if (mft.RecordCount == 0)
                return Enumerable.Empty<INode>();

            var driveRoot = drive.RootDirectory.FullName;
            var rootName = driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var valid = new bool[mft.RecordCount];
            var parsed = new MftNode[mft.RecordCount];

            // Fix up every record before any parsing: parsing may follow an attribute
            // list into another record, which is only safe once no record is mutated.
            Parallel.ForEach(Partitioner.Create(0, mft.RecordCount), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                    valid[i] = ApplyFixup(mft.Record(i));
            });

            Parallel.ForEach(Partitioner.Create(0, mft.RecordCount), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                    if (valid[i])
                        parsed[i] = ParseRecord(mft, valid, i, driveRoot, rootName);
            });

            var nodes = new Dictionary<uint, MftNode>(parsed.Count(n => n != null));
            foreach (var node in parsed)
                if (node != null)
                    nodes.TryAdd(node.EntryNumber, node);

            foreach (var node in nodes.Values)
            {
                if (node.ParentEntryNumber != node.EntryNumber && nodes.TryGetValue(node.ParentEntryNumber, out var parent))
                    node.Parent = parent;
            }

            // Precompute full paths in parallel - the caller keys its dictionary by FullName
            Parallel.ForEach(Partitioner.Create(0, parsed.Length), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                    _ = parsed[i]?.FullName;
            });

            CalculateFolderSizes(nodes.Values);
            return nodes.Values;
        }

        static MftBuffer ReadMft(NativeVolume volume)
        {
            var record = new byte[volume.BytesPerMftRecord];
            volume.Read(checked(volume.MftStartLcn * volume.BytesPerCluster), record, 0, record.Length);
            if (!ApplyFixup(record))
                throw new InvalidDataException("The $MFT file record is corrupt.");

            var (runs, dataSize) = MftDataRuns(record);
            var buffer = new MftBuffer(volume.BytesPerMftRecord, checked((long)dataSize));

            var position = 0L;
            foreach (var run in runs)
            {
                var runBytes = checked((long)(run.Clusters * volume.BytesPerCluster));
                var runPosition = 0L;
                while (runPosition < runBytes && position < buffer.Length)
                {
                    var (segment, segmentOffset) = buffer.Locate(position);
                    var chunk = (int)Math.Min(Math.Min(segment.Length - segmentOffset, runBytes - runPosition), buffer.Length - position);
                    if (!run.IsSparse)
                        volume.Read(checked((ulong)(run.Lcn * (long)volume.BytesPerCluster + runPosition)), segment, segmentOffset, chunk);
                    position += chunk;
                    runPosition += chunk;
                }
            }

            if (position < buffer.Length)
                throw new InvalidDataException("The $MFT data runs do not cover the whole $MFT.");

            return buffer;
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

        static bool ApplyFixup(Span<byte> record)
        {
            if (record.Length < 8 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
                return false;

            var usaOffset = (int)U16(record[4..]);
            var usaCount = (int)U16(record[6..]);
            if (usaCount < 2 || usaOffset < 8 || usaOffset + usaCount * 2 > record.Length || record.Length % (usaCount - 1) != 0)
                return false;

            var stride = record.Length / (usaCount - 1);
            for (var i = 1; i < usaCount; i++)
            {
                var sectorEnd = i * stride - 2;
                if (record[sectorEnd] != record[usaOffset] || record[sectorEnd + 1] != record[usaOffset + 1])
                    return false;

                record[sectorEnd] = record[usaOffset + 2 * i];
                record[sectorEnd + 1] = record[usaOffset + 2 * i + 1];
            }

            return true;
        }

        static MftNode ParseRecord(MftBuffer mft, bool[] valid, int index, string driveRoot, string rootName)
        {
            ReadOnlySpan<byte> record = mft.Record(index);
            var headerFlags = U16(record[22..]);
            if ((headerFlags & 0x1) == 0)
                return null;

            if ((U64(record[32..]) & FileReferenceMask) != 0)
                return null; // extension record; its attributes are reached through the base record's attribute list

            var isDirectory = (headerFlags & 0x2) != 0;

            string name = null;
            var nameRank = int.MaxValue;
            ulong parentReference = 0, fileNameSize = 0;
            uint fileNameFlags = 0;
            ulong fnCreated = 0, fnModified = 0, fnAccessed = 0;
            var hasStandardInfo = false;
            ulong siCreated = 0, siModified = 0, siAccessed = 0;
            var hasDataSize = false;
            ulong dataSize = 0;
            var attributeList = default(ReadOnlySpan<byte>);

            var offset = (int)U16(record[20..]);
            while (offset + 24 <= record.Length)
            {
                var type = U32(record[offset..]);
                if (type == AttributeTerminator)
                    break;

                var length = (int)U32(record[(offset + 4)..]);
                if (length < 24 || offset + length > record.Length)
                    break;

                var nonResident = record[offset + 8] != 0;
                var attributeNameLength = record[offset + 9];

                if (!nonResident)
                {
                    var valueLength = (int)U32(record[(offset + 16)..]);
                    var valueOffset = (int)U16(record[(offset + 20)..]);
                    if (valueOffset >= 0 && valueLength >= 0 && valueOffset + valueLength <= length)
                    {
                        var value = record.Slice(offset + valueOffset, valueLength);
                        switch (type)
                        {
                            case AttributeStandardInformation when valueLength >= 32:
                                siCreated = U64(value);
                                siModified = U64(value[8..]);
                                siAccessed = U64(value[24..]);
                                hasStandardInfo = true;
                                break;

                            case AttributeAttributeList:
                                attributeList = value;
                                break;

                            case AttributeFileName when valueLength >= 66:
                                var nameBytes = value[64] * 2;
                                var rank = value[65] switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }; // Win32, Win32+DOS, POSIX, DOS
                                if (rank < nameRank && 66 + nameBytes <= valueLength)
                                {
                                    nameRank = rank;
                                    name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, nameBytes)));
                                    parentReference = U64(value) & FileReferenceMask;
                                    fnCreated = U64(value[8..]);
                                    fnModified = U64(value[16..]);
                                    fnAccessed = U64(value[32..]);
                                    fileNameSize = U64(value[48..]);
                                    fileNameFlags = U32(value[56..]);
                                }
                                break;

                            case AttributeData when attributeNameLength == 0 && !hasDataSize:
                                dataSize = (ulong)valueLength;
                                hasDataSize = true;
                                break;
                        }
                    }
                }
                else if (type == AttributeData && attributeNameLength == 0 && !hasDataSize && length >= 64 && U64(record[(offset + 16)..]) == 0)
                {
                    dataSize = U64(record[(offset + 48)..]);
                    hasDataSize = true;
                }

                offset += length;
            }

            if (name == null)
                return null;

            if (!isDirectory && !hasDataSize && !TryDataSizeFromAttributeList(mft, valid, attributeList, index, out dataSize))
                dataSize = fileNameSize;

            // Mask to standard FILE_ATTRIBUTE_* bits - $FILE_NAME flags carry 0x10000000 for directories,
            // which must not leak into FileAttributes (the header flag below is authoritative)
            var attributes = (FileAttributes)(fileNameFlags & 0x00FFFFFF);
            if (isDirectory)
                attributes |= FileAttributes.Directory;

            return new MftNode(
                driveRoot,
                (uint)index,
                (uint)parentReference,
                index == RootEntryNumber ? rootName : name,
                attributes,
                isDirectory ? 0UL : dataSize,
                Time(hasStandardInfo ? siCreated : fnCreated),
                Time(hasStandardInfo ? siModified : fnModified),
                Time(hasStandardInfo ? siAccessed : fnAccessed));
        }

        static bool TryDataSizeFromAttributeList(MftBuffer mft, bool[] valid, ReadOnlySpan<byte> list, int baseIndex, out ulong dataSize)
        {
            dataSize = 0;
            var offset = 0;
            while (offset + 26 <= list.Length)
            {
                var type = U32(list[offset..]);
                if (type == 0 || type == AttributeTerminator)
                    break;

                var entryLength = (int)U16(list[(offset + 4)..]);
                if (entryLength < 26 || offset + entryLength > list.Length)
                    break;

                if (type == AttributeData && list[offset + 6] == 0 && U64(list[(offset + 8)..]) == 0)
                {
                    var target = (long)(U64(list[(offset + 16)..]) & FileReferenceMask);
                    return target != baseIndex && target < mft.RecordCount && valid[target] &&
                           TryUnnamedDataSize(mft.Record((int)target), out dataSize);
                }

                offset += entryLength;
            }

            return false;
        }

        static bool TryUnnamedDataSize(ReadOnlySpan<byte> record, out ulong dataSize)
        {
            dataSize = 0;
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
                    if (record[offset + 8] == 0)
                    {
                        dataSize = U32(record[(offset + 16)..]);
                        return true;
                    }

                    if (length >= 64 && U64(record[(offset + 16)..]) == 0)
                    {
                        dataSize = U64(record[(offset + 48)..]);
                        return true;
                    }
                }

                offset += length;
            }

            return false;
        }

        static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        static DateTime Time(ulong fileTime)
            => fileTime == 0 || fileTime > MaxFileTime ? DateTime.MinValue : DateTime.FromFileTimeUtc((long)fileTime);

        static void CalculateFolderSizes(IEnumerable<MftNode> nodes)
        {
            foreach (var node in nodes.Where(n => !n.IsDirectory))
            {
                // The depth cap guards against parent cycles in corrupt records
                var depth = 0;
                for (var parent = node.Parent; parent != null && depth++ < 255; parent = parent.Parent)
                    parent.AddSize(node.Size);
            }
        }

        sealed record DataRun(long Lcn, ulong Clusters, bool IsSparse);

        sealed class MftBuffer
        {
            const int SegmentShift = 24;
            const int SegmentSize = 1 << SegmentShift; // 16 MB: multiple of any record size, no single huge allocation

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
        }

        sealed class MftNode : INode
        {
            readonly string driveRoot;
            string fullName;

            public MftNode(string driveRoot, uint entryNumber, uint parentEntryNumber, string name, FileAttributes attributes, ulong size, DateTime creationTime, DateTime lastChangeTime, DateTime lastAccessTime)
            {
                this.driveRoot = driveRoot;
                EntryNumber = entryNumber;
                ParentEntryNumber = parentEntryNumber;
                Name = name;
                Attributes = attributes;
                Size = size;
                CreationTime = creationTime;
                LastChangeTime = lastChangeTime;
                LastAccessTime = lastAccessTime;
            }

            public uint EntryNumber { get; }
            public uint ParentEntryNumber { get; }
            public MftNode Parent { get; set; }

            public override FileAttributes Attributes { get; protected set; }
            public override string Name { get; }
            public override ulong Size { get; protected set; }
            public override string FullName => fullName ??= BuildFullName(0);
            public override string ParentName => Parent?.Name ?? "";
            public override DateTime CreationTime { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
            public override DateTime LastAccessTime { get; protected set; }

            public void AddSize(ulong size) => Size += size;

            string BuildFullName(int depth)
            {
                // Memoize every level so ancestor paths are built once and shared;
                // the depth cap guards against parent cycles in corrupt records
                var cached = fullName;
                if (cached != null) return cached;
                if (EntryNumber == RootEntryNumber || Parent == null || Parent == this || depth > 255)
                    return driveRoot;

                return fullName = Path.Combine(Parent.BuildFullName(depth + 1), Name);
            }
        }

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

            public static NativeVolume Open(DriveInfo drive)
            {
                var volumeName = new StringBuilder(1024);
                if (!GetVolumeNameForVolumeMountPoint(drive.RootDirectory.FullName, volumeName, volumeName.Capacity))
                    throw new IOException($"Unable to resolve volume name for {drive.RootDirectory.FullName}.");

                var volume = volumeName.ToString().TrimEnd('\\');
                var handle = CreateFile(volume, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
                if (handle == null || handle.IsInvalid)
                    throw new IOException($"Unable to open volume {drive.Name}. Make sure the application has administrator privileges.");

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
