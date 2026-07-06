using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// Parses a raw $MFT (already acquired into an MftBuffer by MftSource) into INodes.
    /// All parsing lives here in the app - the service and broker only ship bytes.
    /// </summary>
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

        public static IEnumerable<INode> GetNodes(MftBuffer mft, DriveInfo drive)
        {
            if (mft == null || mft.RecordCount == 0)
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
                    valid[i] = MftFixup.Apply(mft.Record(i));
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
    }
}
