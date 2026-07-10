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
    /// Parses a raw $MFT byte stream (delivered by MftSource) into INodes in a single
    /// pass over bounded chunks - the whole $MFT is never held in memory.
    /// All parsing lives here in the app - the service and broker only ship bytes.
    ///
    /// The only cross-record dependency is the unnamed $DATA size of a heavily
    /// fragmented file, which lives in an extension record. Every extension record
    /// names its base record in its own header, so their data sizes are collected
    /// into a dictionary keyed by base index while streaming and the affected base
    /// records are resolved after the last chunk - no $ATTRIBUTE_LIST parsing and
    /// no random access into the $MFT is ever needed (and the size is found even
    /// when the attribute list is non-resident).
    /// </summary>
    static class MftDriveReader
    {
        const uint RootEntryNumber = 5;

        const uint AttributeStandardInformation = 0x10;
        const uint AttributeFileName = 0x30;
        const uint AttributeData = 0x80;
        const uint AttributeTerminator = 0xffffffff;
        const ulong FileReferenceMask = 0xffffffffffff;
        const ulong MaxFileTime = 2650467743999999999; // DateTime.MaxValue.ToFileTimeUtc()

        public static IEnumerable<INode> GetNodes(Stream mft, int bytesPerRecord, long length, string driveRoot, int chunkBytes = MftChunkReader.DefaultChunkBytes)
        {
            if (mft == null)
                return Enumerable.Empty<INode>();

            var rootName = driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var recordCount = checked((int)(length / bytesPerRecord));
            var parsed = new MftNode[recordCount];

            // What extension records contribute to their base record (keyed by the base index
            // taken from the extension's own header - no $ATTRIBUTE_LIST parsing needed, so it
            // works even when the list itself is non-resident): the unnamed $DATA size of a
            // heavily fragmented file, and the parents of hard-link names that overflowed out
            // of the base record. pendingSizes holds the files whose base carried no $DATA.
            // Sequence numbers ride along so stale references (records freed and reused while
            // the $MFT streamed by) are rejected instead of mixing two unrelated files.
            var extensionSizes = new ConcurrentDictionary<uint, (ulong Size, ushort Sequence)>();
            var extensionLinks = new ConcurrentDictionary<uint, (ushort Sequence, List<ulong> Parents)>();
            var pendingSizes = new ConcurrentQueue<MftNode>();

            MftChunkReader.Read(mft, bytesPerRecord, length, (buffer, first, count) =>
            {
                Parallel.ForEach(Partitioner.Create(0, count), range =>
                {
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        var record = buffer.AsSpan(i * bytesPerRecord, bytesPerRecord);
                        if (!MftFixup.Apply(record))
                            continue;

                        var baseReference = U64(record[32..]);
                        if ((baseReference & FileReferenceMask) != 0)
                        {
                            // Only in-use extensions - a freed one may point at a base index
                            // that has since been reused by a different file
                            if ((U16(record[22..]) & 0x1) != 0)
                                ScanExtension(record, baseReference, extensionSizes, extensionLinks);
                            continue;
                        }

                        parsed[first + i] = ParseRecord(record, first + i, driveRoot, rootName, pendingSizes);
                    }
                });
            }, chunkBytes);

            if (recordCount == 0)
                return Enumerable.Empty<INode>();

            // Every extension record has been seen now - resolve the deferred sizes
            foreach (var node in pendingSizes)
                if (extensionSizes.TryGetValue(node.EntryNumber, out var e) && SequencesMatch(e.Sequence, node.SequenceNumber))
                    node.SetSize(e.Size);

            var nodes = new Dictionary<uint, MftNode>(parsed.Count(n => n != null));
            foreach (var node in parsed)
                if (node != null)
                    nodes.TryAdd(node.EntryNumber, node);

            foreach (var node in nodes.Values)
            {
                if (node.ParentEntryNumber != node.EntryNumber
                    && nodes.TryGetValue(node.ParentEntryNumber, out var parent)
                    && SequencesMatch(node.ParentSequence, parent.SequenceNumber))
                    node.Parent = parent;
            }

            DropOrphans(nodes, recordCount);

            // Merge overflowed hard-link parents into their (surviving) base records
            foreach (var (baseIndex, contribution) in extensionLinks)
            {
                if (!nodes.TryGetValue(baseIndex, out var node) || node.IsDirectory
                    || !SequencesMatch(contribution.Sequence, node.SequenceNumber))
                    continue;
                var links = new List<ulong>(1 + contribution.Parents.Count);
                if (node.LinkParents != null) links.AddRange(node.LinkParents);
                else links.Add((ulong)node.ParentSequence << 48 | node.ParentEntryNumber);
                links.AddRange(contribution.Parents);
                node.LinkParents = links.ToArray();
            }

            CalculateFolderSizes(nodes);
            return nodes.Values;
        }

        static MftNode ParseRecord(ReadOnlySpan<byte> record, int index, string driveRoot, string rootName, ConcurrentQueue<MftNode> pendingSizes)
        {
            var headerFlags = U16(record[22..]);
            if ((headerFlags & 0x1) == 0)
                return null;

            var isDirectory = (headerFlags & 0x2) != 0;
            var sequenceNumber = U16(record[16..]);

            string name = null;
            var nameRank = int.MaxValue;
            ulong parentReference = 0, fileNameSize = 0;
            uint fileNameFlags = 0;
            // Every non-DOS $FILE_NAME is one directory entry (hard link). Folder sizes
            // must count the file once per link - that is what a directory walk and
            // Explorer's folder properties do. Allocated only for multi-link files.
            var linkCount = 0;
            ulong firstLinkParent = 0;
            List<ulong> linkParents = null;
            ulong fnCreated = 0, fnModified = 0, fnAccessed = 0;
            var hasStandardInfo = false;
            ulong siCreated = 0, siModified = 0, siAccessed = 0;
            var hasDataSize = false;
            ulong dataSize = 0;

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

                            case AttributeFileName when valueLength >= 66:
                                var nameBytes = value[64] * 2;
                                var rank = value[65] switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }; // Win32, Win32+DOS, POSIX, DOS
                                if (!isDirectory && value[65] != 2) // A DOS name shadows its Win32 pair - not a separate link
                                {
                                    var linkParent = U64(value);
                                    if (linkCount++ == 0) firstLinkParent = linkParent;
                                    else (linkParents ??= new List<ulong>(4) { firstLinkParent }).Add(linkParent);
                                }
                                if (rank < nameRank && 66 + nameBytes <= valueLength)
                                {
                                    nameRank = rank;
                                    name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, nameBytes)));
                                    parentReference = U64(value);
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

            if (!isDirectory && !hasDataSize)
                dataSize = fileNameSize;

            // Mask to standard FILE_ATTRIBUTE_* bits - $FILE_NAME flags carry 0x10000000 for directories,
            // which must not leak into FileAttributes (the header flag below is authoritative)
            var attributes = (FileAttributes)(fileNameFlags & 0x00FFFFFF);
            if (isDirectory)
                attributes |= FileAttributes.Directory;

            var node = new MftNode(
                driveRoot,
                (uint)index,
                (uint)(parentReference & FileReferenceMask),
                index == RootEntryNumber ? rootName : name,
                attributes,
                isDirectory ? 0UL : dataSize,
                Time(hasStandardInfo ? siCreated : fnCreated),
                Time(hasStandardInfo ? siModified : fnModified),
                Time(hasStandardInfo ? siAccessed : fnAccessed))
            {
                SequenceNumber = sequenceNumber,
                ParentSequence = (ushort)(parentReference >> 48),
                LinkParents = linkParents?.ToArray()
            };

            // The unnamed $DATA lives in an extension record - resolve after the last chunk
            if (!isDirectory && !hasDataSize)
                pendingSizes.Enqueue(node);

            return node;
        }

        /// <summary>
        /// Collect what an extension record contributes to its base: the unnamed $DATA
        /// size (resident, or the non-resident instance starting at VCN 0) and the
        /// parents of any non-DOS $FILE_NAME (hard-link names overflowed from the base)
        /// </summary>
        static void ScanExtension(ReadOnlySpan<byte> record, ulong baseReference, ConcurrentDictionary<uint, (ulong Size, ushort Sequence)> sizes, ConcurrentDictionary<uint, (ushort Sequence, List<ulong> Parents)> links)
        {
            var baseIndex = (uint)(baseReference & FileReferenceMask);
            var baseSequence = (ushort)(baseReference >> 48);
            List<ulong> parents = null;
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
                        sizes[baseIndex] = (U32(record[(offset + 16)..]), baseSequence);
                    else if (length >= 64 && U64(record[(offset + 16)..]) == 0)
                        sizes[baseIndex] = (U64(record[(offset + 48)..]), baseSequence);
                }
                else if (type == AttributeFileName && record[offset + 8] == 0)
                {
                    var valueLength = (int)U32(record[(offset + 16)..]);
                    var valueOffset = (int)U16(record[(offset + 20)..]);
                    if (valueLength >= 66 && valueOffset + valueLength <= length)
                    {
                        var value = record.Slice(offset + valueOffset, valueLength);
                        if (value[65] != 2) // A DOS name shadows its Win32 pair - not a separate link
                            (parents ??= new List<ulong>(2)).Add(U64(value));
                    }
                }

                offset += length;
            }

            if (parents != null)
                links.AddOrUpdate(baseIndex, (baseSequence, parents),
                    (_, old) =>
                    {
                        var merged = new List<ulong>(old.Parents.Count + parents.Count);
                        merged.AddRange(old.Parents);
                        merged.AddRange(parents);
                        return (old.Sequence, merged);
                    });
        }

        /// <summary>
        /// A file reference is current only when its embedded sequence number matches the
        /// record's - a mismatch means the record was freed and reused while the $MFT
        /// streamed by. Zero acts as a wildcard (references written without a sequence).
        /// </summary>
        static bool SequencesMatch(ushort reference, ushort record)
            => reference == 0 || record == 0 || reference == record;

        /// <summary>
        /// Remove nodes whose parent chain does not reach the drive root. Their parent was
        /// deleted or its record reused while the $MFT streamed by (NTFS only deletes empty
        /// directories, so such a file is normally deleted too), or the records are corrupt
        /// or cyclic. Either way the real path is unknowable - a made-up one would collide
        /// with the drive root and break every file operation. Anything that still exists
        /// is re-delivered by the change watcher or the next rescan.
        /// </summary>
        static void DropOrphans(Dictionary<uint, MftNode> nodes, int recordCount)
        {
            if (!nodes.ContainsKey(RootEntryNumber))
                return; // No root record - dropping would empty the whole drive

            const byte Keep = 1, Drop = 2, Visiting = 3;
            var state = new byte[recordCount];
            var chain = new List<MftNode>(64);
            foreach (var start in nodes.Values)
            {
                var node = start;
                byte verdict = 0;
                while (verdict == 0)
                {
                    var seen = state[node.EntryNumber];
                    if (seen == Keep || seen == Drop) verdict = seen;
                    else if (seen == Visiting) verdict = Drop; // Parent cycle
                    else if (node.EntryNumber == RootEntryNumber) verdict = Keep;
                    else if (node.Parent == null) verdict = Drop;
                    else
                    {
                        state[node.EntryNumber] = Visiting;
                        chain.Add(node);
                        node = node.Parent;
                    }
                }
                if (state[node.EntryNumber] == 0) state[node.EntryNumber] = verdict; // The deciding node itself
                foreach (var visited in chain) state[visited.EntryNumber] = verdict;
                chain.Clear();
            }

            foreach (var entry in nodes.Values.Where(n => state[n.EntryNumber] == Drop).Select(n => n.EntryNumber).ToList())
                nodes.Remove(entry);
        }

        static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        static DateTime Time(ulong fileTime)
            => fileTime == 0 || fileTime > MaxFileTime ? DateTime.MinValue : DateTime.FromFileTimeUtc((long)fileTime);

        /// <summary>
        /// A file counts once per hard link (per non-DOS $FILE_NAME), so folder sizes
        /// match what a directory walk and Explorer's folder properties report.
        /// </summary>
        static void CalculateFolderSizes(Dictionary<uint, MftNode> nodes)
        {
            foreach (var node in nodes.Values)
            {
                if (node.IsDirectory) continue;

                if (node.LinkParents == null)
                {
                    AddToChain(node.Parent, node.Size);
                }
                else
                {
                    foreach (var link in node.LinkParents)
                        if (nodes.TryGetValue((uint)(link & FileReferenceMask), out var parent) && parent != node
                            && SequencesMatch((ushort)(link >> 48), parent.SequenceNumber))
                            AddToChain(parent, node.Size);
                }
            }

            static void AddToChain(MftNode parent, ulong size)
            {
                // The depth cap guards against parent cycles in corrupt records
                var depth = 0;
                for (; parent != null && depth++ < 255; parent = parent.Parent)
                    parent.AddSize(size);
            }
        }

        sealed class MftNode : INode
        {
            readonly string driveRoot;

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

            /// <summary>This record's sequence number - stale references to a reused entry are detected against it</summary>
            public ushort SequenceNumber { get; init; }

            /// <summary>The sequence number embedded in the parent reference of the chosen $FILE_NAME</summary>
            public ushort ParentSequence { get; init; }

            /// <summary>
            /// Full file references (entry + sequence) of every hard-link parent when the
            /// file has more than one; null for ordinary single-link files
            /// </summary>
            public ulong[] LinkParents { get; set; }

            public override FileAttributes Attributes { get; protected set; }
            public override string Name { get; }
            public override ulong Size { get; protected set; }

            /// <summary>
            /// The path lives in the parent chain (roots and orphans are terminal at the
            /// drive root, exactly like the old memoized BuildFullName)
            /// </summary>
            public override INode PathParent =>
                EntryNumber == RootEntryNumber || Parent == this ? null : Parent;

            /// <summary>
            /// Built on demand - full paths are no longer stored per node.
            /// NodePath keys, sorts and filters nodes without ever calling this in bulk.
            /// </summary>
            public override string FullName => PathParent == null ? driveRoot : NodePath.Materialize(this);

            public override string ParentName => Parent?.Name ?? "";
            public override string Folder => PathParent?.FullName ?? Path.GetDirectoryName(driveRoot) ?? "";
            public override DateTime CreationTime { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
            public override DateTime LastAccessTime { get; protected set; }

            public void AddSize(ulong size) => Size += size;
            public void SetSize(ulong size) => Size = size;
        }
    }
}
