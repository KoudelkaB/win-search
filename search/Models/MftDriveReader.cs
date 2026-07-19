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
        const byte NoNameRank = 0x7f;
        const byte OwnSingleLink = 0x80;
        const byte NameRankMask = 0x7f;

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
            // Parsing-only data lives in sidecars, not in every long-lived MftNode. Parent
            // reference includes its sequence. The high bit of nameRanks marks one base link.
            var parentReferences = new ulong[recordCount];
            var nameRanks = new byte[recordCount];
            Array.Fill(nameRanks, NoNameRank);
            var baseHardLinks = new NonBlocking.ConcurrentDictionary<uint, ulong[]>();

            // What extension records contribute to their base record (keyed by the base index
            // taken from the extension's own header - no $ATTRIBUTE_LIST parsing needed, so it
            // works even when the list itself is non-resident): the unnamed $DATA size of a
            // heavily fragmented file, and the parents of hard-link names that overflowed out
            // of the base record. pendingSizes holds the files whose base carried no $DATA.
            // Sequence numbers ride along so stale references (records freed and reused while
            // the $MFT streamed by) are rejected instead of mixing two unrelated files.
            var extensionSizes = new NonBlocking.ConcurrentDictionary<uint, (ulong Size, ushort Sequence)>();
            var extensionLinks = new NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, List<ulong> Parents)>();
            var extensionNames = new NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, int Rank, string Name, ulong Parent)>();
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
                                ScanExtension(record, baseReference, extensionSizes, extensionLinks, extensionNames);
                            continue;
                        }

                        var index = first + i;
                        var node = ParseRecord(record, index, rootName, pendingSizes,
                            out var parentReference, out var nameRank, out var linkParents, out var hasOwnSingleLink);
                        parsed[index] = node;
                        if (node == null) continue;
                        parentReferences[index] = parentReference;
                        nameRanks[index] = (byte)(nameRank | (hasOwnSingleLink ? OwnSingleLink : 0));
                        if (linkParents != null) baseHardLinks[(uint)index] = linkParents;
                    }
                });
            }, chunkBytes);

            if (recordCount == 0)
                return Enumerable.Empty<INode>();

            // Every extension record has been seen now - resolve the deferred sizes
            foreach (var node in pendingSizes)
                if (extensionSizes.TryGetValue(node.EntryNumber, out var e) && SequencesMatch(e.Sequence, node.SequenceNumber))
                    node.SetSize(e.Size);

            // $FILE_NAMEs overflow into extension records too - without this a file whose
            // Win32 name moved out of a crowded base record shows up under its DOS 8.3
            // name (DOTNET~4.EXE), or not at all when the base kept no name
            var displacedOwnLinks = new Dictionary<uint, ulong>();
            foreach (var (baseIndex, n) in extensionNames)
            {
                if (baseIndex >= (uint)parsed.Length || baseIndex == RootEntryNumber) continue;
                var node = parsed[baseIndex];
                if (node != null && SequencesMatch(n.Sequence, node.SequenceNumber)
                    && n.Rank < (nameRanks[baseIndex] & NameRankMask))
                {
                    //The chosen path may move to an extension name. Preserve the base's
                    //single hard-link parent only for the rare records that need it later.
                    if ((nameRanks[baseIndex] & OwnSingleLink) != 0)
                        displacedOwnLinks[baseIndex] = parentReferences[baseIndex];
                    node.SetName(n.Name);
                    parentReferences[baseIndex] = n.Parent;
                    nameRanks[baseIndex] = (byte)((nameRanks[baseIndex] & OwnSingleLink) | n.Rank);
                }
            }

            // Still nameless - every $FILE_NAME lost, so its path is unknowable.
            for (var i = 0; i < parsed.Length; i++)
                if (parsed[i]?.Name == null) parsed[i] = null;

            foreach (var node in parsed)
            {
                if (node == null) continue;
                var parentReference = parentReferences[node.EntryNumber];
                var parentEntry = (uint)(parentReference & FileReferenceMask);
                if (parentEntry != node.EntryNumber && parentEntry < (uint)parsed.Length
                    && parsed[parentEntry] is { } parent
                    && SequencesMatch((ushort)(parentReference >> 48), parent.SequenceNumber))
                    node.Parent = parent;
            }

            DropOrphans(parsed);

            // Merge overflowed hard-link parents into their (surviving) base records
            foreach (var (baseIndex, contribution) in extensionLinks)
            {
                if (baseIndex >= (uint)parsed.Length || parsed[baseIndex] is not { } node || node.IsDirectory
                    || !SequencesMatch(contribution.Sequence, node.SequenceNumber))
                    continue;
                var links = new List<ulong>(1 + contribution.Parents.Count);
                if (baseHardLinks.TryGetValue(baseIndex, out var ownLinks)) links.AddRange(ownLinks);
                // A base record holding only the DOS name counts no link of its own - its
                // Win32 pair is one of the extension's names and must not count twice
                else if ((nameRanks[baseIndex] & OwnSingleLink) != 0)
                    links.Add(displacedOwnLinks.TryGetValue(baseIndex, out var displaced)
                        ? displaced : parentReferences[baseIndex]);
                links.AddRange(contribution.Parents);
                baseHardLinks[baseIndex] = links.ToArray();
            }

            CalculateFolderSizes(parsed, baseHardLinks);

            var liveCount = 0;
            foreach (var node in parsed)
                if (node != null) liveCount++;
            var dense = new INode[liveCount];
            var at = 0;
            foreach (var node in parsed)
                if (node != null)
                {
                    node.SetPathHash(NodePath.ComputePathHash(node));
                    dense[at++] = node;
                }
            //Only genuinely multi-linked live records need to survive as sparse metadata.
            //The USN journal reports a hard-link mutation but an unprivileged record cannot
            //name the removed link, so the watcher uses this bit to request an exact MFT
            //folder-size rebuild after the change storm goes quiet.
            var hardLinkedEntries = new List<uint>();
            foreach (var (entry, links) in baseHardLinks)
                if (links.Length > 1 && entry < (uint)parsed.Length && parsed[entry] != null)
                    hardLinkedEntries.Add(entry);
            hardLinkedEntries.Sort();
            return new MftNodeCollection(parsed, dense, hardLinkedEntries.ToArray());
        }

        static MftNode ParseRecord(ReadOnlySpan<byte> record, int index, string rootName,
            ConcurrentQueue<MftNode> pendingSizes, out ulong parentReference, out byte nameRank,
            out ulong[] hardLinks, out bool hasOwnSingleLink)
        {
            parentReference = 0;
            nameRank = NoNameRank;
            hardLinks = null;
            hasOwnSingleLink = false;
            var headerFlags = U16(record[22..]);
            if ((headerFlags & 0x1) == 0)
                return null;

            var isDirectory = (headerFlags & 0x2) != 0;
            var sequenceNumber = U16(record[16..]);

            string name = null;
            var bestNameRank = (int)NoNameRank;
            ulong bestParentReference = 0, fileNameSize = 0;
            uint fileNameFlags = 0;
            // Every non-DOS $FILE_NAME is one directory entry (hard link). Folder sizes
            // must count the file once per link - that is what a directory walk and
            // Explorer's folder properties do. Allocated only for multi-link files.
            var linkCount = 0;
            ulong firstLinkParent = 0;
            List<ulong> linkParents = null;
            ulong fnModified = 0;
            var hasStandardInfo = false;
            ulong siModified = 0;
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
                                siModified = U64(value[8..]);
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
                                if (rank < bestNameRank && 66 + nameBytes <= valueLength)
                                {
                                    bestNameRank = rank;
                                    name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, nameBytes)));
                                    bestParentReference = U64(value);
                                    fnModified = U64(value[16..]);
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

            // No usable $FILE_NAME in the base record - keep the node anyway: the name may
            // live in an extension record and is merged in after the last chunk. A node
            // still nameless then is dropped before the parent pass.
            if (!isDirectory && !hasDataSize)
                dataSize = fileNameSize;

            // Mask to standard FILE_ATTRIBUTE_* bits - $FILE_NAME flags carry 0x10000000 for directories,
            // which must not leak into FileAttributes (the header flag below is authoritative)
            var attributes = (FileAttributes)(fileNameFlags & 0x00FFFFFF);
            if (isDirectory)
                attributes |= FileAttributes.Directory;

            parentReference = bestParentReference;
            nameRank = (byte)bestNameRank;
            hardLinks = linkParents?.ToArray();
            hasOwnSingleLink = linkCount == 1;
            var node = new MftNode(
                ((ulong)sequenceNumber << 48) | (uint)index,
                index == RootEntryNumber ? rootName : name,
                attributes,
                isDirectory ? 0UL : dataSize,
                Time(hasStandardInfo ? siModified : fnModified));

            // The unnamed $DATA lives in an extension record - resolve after the last chunk
            if (!isDirectory && !hasDataSize)
                pendingSizes.Enqueue(node);

            return node;
        }

        /// <summary>
        /// Collect what an extension record contributes to its base: the unnamed $DATA
        /// size (resident, or the non-resident instance starting at VCN 0), the parents
        /// of any non-DOS $FILE_NAME (hard-link names overflowed from the base), and the
        /// best-ranked name itself - the base may have kept only its DOS 8.3 name
        /// </summary>
        static void ScanExtension(ReadOnlySpan<byte> record, ulong baseReference, NonBlocking.ConcurrentDictionary<uint, (ulong Size, ushort Sequence)> sizes, NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, List<ulong> Parents)> links, NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, int Rank, string Name, ulong Parent)> names)
        {
            var baseIndex = (uint)(baseReference & FileReferenceMask);
            var baseSequence = (ushort)(baseReference >> 48);
            List<ulong> parents = null;
            string bestName = null;
            var bestRank = int.MaxValue;
            ulong bestParent = 0;
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

                        var nameBytes = value[64] * 2;
                        var rank = value[65] switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }; // Win32, Win32+DOS, POSIX, DOS
                        if (rank < bestRank && 66 + nameBytes <= valueLength)
                        {
                            bestRank = rank;
                            bestName = new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, nameBytes)));
                            bestParent = U64(value);
                        }
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

            if (bestName != null)
                names.AddOrUpdate(baseIndex, (baseSequence, bestRank, bestName, bestParent),
                    (_, old) => old.Rank <= bestRank ? old : (baseSequence, bestRank, bestName, bestParent));
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
        static void DropOrphans(MftNode[] nodes)
        {
            if (nodes.Length <= RootEntryNumber || nodes[RootEntryNumber] == null)
            {
                //Without the root no canonical path can be constructed. Returning nodes
                //under invented paths is more dangerous than retrying on the next scan.
                Array.Clear(nodes);
                return;
            }

            const byte Keep = 1, Drop = 2, Visiting = 3;
            var state = new byte[nodes.Length];
            var chain = new List<MftNode>(64);
            foreach (var start in nodes)
            {
                if (start == null) continue;
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

            for (var i = 0; i < nodes.Length; i++)
                if (nodes[i] != null && state[i] == Drop) nodes[i] = null;
        }

        static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        // Local time, not UTC: FileNode (watcher/walk) reports FileSystemInfo local times
        // and the grid binds the values directly - UTC here showed MFT rows shifted by the
        // whole UTC offset against the same file re-indexed by the watcher
        static DateTime Time(ulong fileTime)
            => fileTime == 0 || fileTime > MaxFileTime ? DateTime.MinValue : DateTime.FromFileTime((long)fileTime);

        /// <summary>
        /// A file counts once per hard link (per non-DOS $FILE_NAME), so folder sizes
        /// match what a directory walk and Explorer's folder properties report.
        /// </summary>
        static void CalculateFolderSizes(MftNode[] nodes,
            NonBlocking.ConcurrentDictionary<uint, ulong[]> hardLinks)
        {
            foreach (var node in nodes)
            {
                if (node == null || node.IsDirectory) continue;

                if (!hardLinks.TryGetValue(node.EntryNumber, out var links))
                {
                    AddToChain(node.Parent, node.Size);
                }
                else
                {
                    foreach (var link in links)
                    {
                        var entry = (uint)(link & FileReferenceMask);
                        if (entry < (uint)nodes.Length && nodes[entry] is { } parent && parent != node
                            && SequencesMatch((ushort)(link >> 48), parent.SequenceNumber))
                            AddToChain(parent, node.Size);
                    }
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
            readonly ulong frn;
            string name;
            int pathHash;

            public MftNode(ulong frn, string name, FileAttributes attributes, ulong size, DateTime lastChangeTime)
            {
                this.frn = frn;
                this.name = name;
                Attributes = attributes;
                Size = size;
                LastChangeTime = lastChangeTime;
            }

            public uint EntryNumber => (uint)(frn & FileReferenceMask);
            public MftNode Parent { get; set; }

            /// <summary>This record's sequence number - stale references to a reused entry are detected against it</summary>
            public ushort SequenceNumber => (ushort)(frn >> 48);

            /// <summary>Full NTFS file reference (sequence + entry) - the key USN journal records carry</summary>
            public override ulong Frn => frn;

            public override FileAttributes Attributes { get; protected set; }
            public override string Name => name;
            public override ulong Size { get; protected set; }

            /// <summary>
            /// The path lives in the parent chain (roots and orphans are terminal at the
            /// drive root, exactly like the old memoized BuildFullName)
            /// </summary>
            public override INode PathParent => Parent;

            /// <summary>
            /// Built on demand - full paths are no longer stored per node.
            /// NodePath keys, sorts and filters nodes without ever calling this in bulk.
            /// </summary>
            public override string FullName => Parent == null
                ? name + Path.DirectorySeparatorChar : NodePath.Materialize(this);

            public override string ParentName => Parent?.Name ?? "";
            public override string Folder => Parent?.FullName ?? "";
            public override DateTime LastChangeTime { get; protected set; }

            internal override bool TryGetPathHash(out int hash)
            {
                hash = pathHash;
                return true;
            }

            public void AddSize(ulong size) => Size += size;
            public void SetSize(ulong size) => Size = size;

            public void SetName(string name) => this.name = name;
            public void SetPathHash(int hash) => pathHash = hash;
        }

        sealed class MftNodeCollection : IFrnNodeSource
        {
            readonly MftNode[] byEntry;
            readonly INode[] dense;
            readonly uint[] hardLinkedEntries;

            public MftNodeCollection(MftNode[] byEntry, INode[] dense, uint[] hardLinkedEntries)
            {
                this.byEntry = byEntry;
                this.dense = dense;
                this.hardLinkedEntries = hardLinkedEntries;
            }

            public int Count => dense.Length;
            public IReadOnlyList<INode> DenseNodes => dense;

            public bool TryGetByFrn(ulong frn, out INode node)
            {
                var entry = frn & FileReferenceMask;
                if (entry < (ulong)byEntry.Length && byEntry[(int)entry] is { } found && found.Frn == frn)
                {
                    node = found;
                    return true;
                }
                node = null;
                return false;
            }

            public bool HasMultipleLinks(ulong frn)
            {
                var entry = frn & FileReferenceMask;
                return entry < (ulong)byEntry.Length
                    && byEntry[(int)entry] is { } found
                    && found.Frn == frn
                    && Array.BinarySearch(hardLinkedEntries, (uint)entry) >= 0;
            }

            public IEnumerator<INode> GetEnumerator() => ((IEnumerable<INode>)dense).GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => dense.GetEnumerator();
        }
    }
}
