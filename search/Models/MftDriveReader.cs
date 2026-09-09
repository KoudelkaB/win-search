using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// Parses a raw $MFT byte stream (delivered by MftSource) into an <see cref="MftTable"/>
    /// in a single pass over bounded chunks - the whole $MFT is never held in memory.
    /// All parsing lives here in the app - the service and broker only ship bytes.
    ///
    /// Records are parsed into columns indexed by MFT entry number (no object per record),
    /// linked, aggregated and then compacted into the dense row table the app indexes.
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
        readonly record struct FileLink(ulong Parent, string Name);
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

        /// <summary>Per-entry parse results; a live record has a non-null name (or gets one from an extension).</summary>
        sealed class Records
        {
            public readonly string[] Name;
            public readonly ulong[] ParentReference; //Includes the sequence; reused as the size scratch after linking
            public readonly byte[] NameRank;          //High bit marks one base link
            public readonly ushort[] Sequence;
            public readonly ulong[] Size;
            public readonly long[] TimeTicks;
            public readonly uint[] Attributes;
            public readonly bool[] Live;
            public readonly int[] Parent;             //Resolved parent entry, -1 = none
            public readonly int[] Depth;              //Directory depth during aggregation, then the path hash
            public readonly uint[] Descendants;

            public Records(int count)
            {
                Name = new string[count];
                ParentReference = new ulong[count];
                NameRank = new byte[count];
                Array.Fill(NameRank, NoNameRank);
                Sequence = new ushort[count];
                Size = new ulong[count];
                TimeTicks = new long[count];
                Attributes = new uint[count];
                Live = new bool[count];
                Parent = new int[count];
                Array.Fill(Parent, -1);
                Depth = new int[count];
                Descendants = new uint[count];
            }

            public int Count => Name.Length;
            public bool IsDirectory(int entry) => (Attributes[entry] & (uint)FileAttributes.Directory) != 0;
            public bool IsLiveDirectory(int entry) => Live[entry] && IsDirectory(entry);
        }

        public static IEnumerable<INode> GetNodes(Stream mft, int bytesPerRecord, long length,
            string driveRoot, int chunkBytes = MftChunkReader.DefaultChunkBytes,
            CancellationToken cancellationToken = default, bool drainOnCancellation = true)
        {
            if (mft == null)
                return Enumerable.Empty<INode>();

            var phase = Stopwatch.StartNew();
            var rootName = driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var recordCount = checked((int)(length / bytesPerRecord));
            var records = new Records(recordCount);
            var baseHardLinks = new NonBlocking.ConcurrentDictionary<uint, FileLink[]>();

            // What extension records contribute to their base record (keyed by the base index
            // taken from the extension's own header - no $ATTRIBUTE_LIST parsing needed, so it
            // works even when the list itself is non-resident): the unnamed $DATA size of a
            // heavily fragmented file, and the parents of hard-link names that overflowed out
            // of the base record. pendingSizes holds the files whose base carried no $DATA.
            // Sequence numbers ride along so stale references (records freed and reused while
            // the $MFT streamed by) are rejected instead of mixing two unrelated files.
            var extensionSizes = new NonBlocking.ConcurrentDictionary<uint, (ulong Size, ushort Sequence)>();
            var extensionLinks = new NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, List<FileLink> Parents)>();
            var extensionNames = new NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, int Rank, string Name, ulong Parent)>();
            var pendingSizes = new ConcurrentQueue<int>();
            var parallelOptions = new ParallelOptions { CancellationToken = cancellationToken };

            MftNamePoolStats nameStats;
            using (var namePool = new MftNamePool(recordCount))
            {
                MftChunkReader.Read(mft, bytesPerRecord, length, (buffer, first, count) =>
                {
                    Parallel.ForEach(Partitioner.Create(0, count), parallelOptions, range =>
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
                                    ScanExtension(record, baseReference, extensionSizes, extensionLinks,
                                        extensionNames, namePool);
                                continue;
                            }

                            var index = first + i;
                            if (ParseRecord(record, index, rootName, records, pendingSizes, namePool,
                                    out var linkParents))
                                if (linkParents != null) baseHardLinks[(uint)index] = linkParents;
                        }
                    });
                }, chunkBytes, cancellationToken, drainOnCancellation);
                nameStats = namePool.Stats;
            }
            var readParseMs = phase.ElapsedMilliseconds;
            phase.Restart();
            cancellationToken.ThrowIfCancellationRequested();

            if (recordCount == 0)
                return Enumerable.Empty<INode>();

            // Every extension record has been seen now - resolve the deferred sizes
            foreach (var entry in pendingSizes)
                if (extensionSizes.TryGetValue((uint)entry, out var e) && SequencesMatch(e.Sequence, records.Sequence[entry]))
                    records.Size[entry] = e.Size;

            // $FILE_NAMEs overflow into extension records too - without this a file whose
            // Win32 name moved out of a crowded base record shows up under its DOS 8.3
            // name (DOTNET~4.EXE), or not at all when the base kept no name
            var displacedOwnLinks = new Dictionary<uint, FileLink>();
            foreach (var (baseIndex, n) in extensionNames)
            {
                if (baseIndex >= (uint)recordCount || baseIndex == RootEntryNumber) continue;
                if (records.Live[baseIndex] && SequencesMatch(n.Sequence, records.Sequence[baseIndex])
                    && n.Rank < (records.NameRank[baseIndex] & NameRankMask))
                {
                    //The chosen path may move to an extension name. Preserve the base's
                    //single hard-link parent only for the rare records that need it later.
                    if ((records.NameRank[baseIndex] & OwnSingleLink) != 0)
                        displacedOwnLinks[baseIndex] = new FileLink(records.ParentReference[baseIndex], records.Name[baseIndex]);
                    records.Name[baseIndex] = n.Name;
                    records.ParentReference[baseIndex] = n.Parent;
                    records.NameRank[baseIndex] = (byte)((records.NameRank[baseIndex] & OwnSingleLink) | n.Rank);
                }
            }

            // Still nameless - every $FILE_NAME lost, so its path is unknowable.
            for (var i = 0; i < recordCount; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (records.Live[i] && records.Name[i] == null) records.Live[i] = false;
            }

            for (var i = 0; i < recordCount; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!records.Live[i]) continue;
                var parentReference = records.ParentReference[i];
                var parentEntry = (uint)(parentReference & FileReferenceMask);
                if (parentEntry != (uint)i && parentEntry < (uint)recordCount
                    && records.IsLiveDirectory((int)parentEntry)
                    && SequencesMatch((ushort)(parentReference >> 48), records.Sequence[parentEntry]))
                    records.Parent[i] = (int)parentEntry;
            }

            // Merge overflowed hard-link names before discarding an orphaned canonical
            // path: another link may still have a live parent.
            foreach (var (baseIndex, contribution) in extensionLinks)
            {
                if (baseIndex >= (uint)recordCount || !records.Live[baseIndex] || records.IsDirectory((int)baseIndex)
                    || !SequencesMatch(contribution.Sequence, records.Sequence[baseIndex]))
                    continue;
                var links = new List<FileLink>(1 + contribution.Parents.Count);
                if (baseHardLinks.TryGetValue(baseIndex, out var ownLinks)) links.AddRange(ownLinks);
                // A base record holding only the DOS name counts no link of its own - its
                // Win32 pair is one of the extension's names and must not count twice
                else if ((records.NameRank[baseIndex] & OwnSingleLink) != 0)
                    links.Add(displacedOwnLinks.TryGetValue(baseIndex, out var displaced)
                        ? displaced : new FileLink(records.ParentReference[baseIndex], records.Name[baseIndex]));
                links.AddRange(contribution.Parents);
                baseHardLinks[baseIndex] = links.ToArray();
            }

            bool LinkParentIsLive(FileLink link)
            {
                var parentEntry = link.Parent & FileReferenceMask;
                return parentEntry < (ulong)recordCount
                    && records.IsLiveDirectory((int)parentEntry)
                    && SequencesMatch((ushort)(link.Parent >> 48), records.Sequence[parentEntry]);
            }

            var linkedEntries = baseHardLinks.Keys.Where(entry => records.Live[entry]).ToArray();
            DropOrphans(records, cancellationToken);
            foreach (var entry in linkedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var live = baseHardLinks[entry].Where(LinkParentIsLive).Distinct().ToArray();
                baseHardLinks[entry] = live;
                if (live.Length == 0) continue;
                if (!records.Live[entry])
                {
                    //The canonical name's parent is gone but another link survives - the
                    //file is reachable through it, so it is rescued under that name.
                    records.Name[entry] = live[0].Name;
                    records.Parent[entry] = (int)(live[0].Parent & FileReferenceMask);
                    records.Live[entry] = true;
                }
            }

            var linkMs = phase.ElapsedMilliseconds;
            phase.Restart();
            CalculateFolderSizesAndDirectoryPathHashes(records, baseHardLinks, rootName + Path.DirectorySeparatorChar,
                cancellationToken);
            var aggregateHashMs = phase.ElapsedMilliseconds;
            phase.Restart();
            var table = BuildTable(records, baseHardLinks, LinkParentIsLive, rootName, nameStats,
                readParseMs, linkMs, aggregateHashMs, phase, cancellationToken);
            return table;
        }

        static bool ParseRecord(ReadOnlySpan<byte> record, int index, string rootName,
            Records records, ConcurrentQueue<int> pendingSizes, MftNamePool namePool,
            out FileLink[] hardLinks)
        {
            hardLinks = null;
            var headerFlags = U16(record[22..]);
            if ((headerFlags & 0x1) == 0)
                return false;

            var isDirectory = (headerFlags & 0x2) != 0;
            var sequenceNumber = U16(record[16..]);

            // Names are decoded from the record only once their role is known: the chosen
            // name once per record, link names only for multi-link files. Most records
            // also carry a DOS 8.3 name that is never selected and never a link - it used
            // to be allocated, hashed and pooled for nothing on every scan.
            var bestNameRank = (int)NoNameRank;
            int bestNameOffset = -1, bestNameBytes = 0;
            ulong bestParentReference = 0, fileNameSize = 0;
            uint fileNameFlags = 0;
            // Every non-DOS $FILE_NAME is one directory entry (hard link). Folder sizes
            // must count the file once per link - that is what a directory walk and
            // Explorer's folder properties do. Allocated only for multi-link files.
            var linkCount = 0;
            ulong firstLinkParent = 0;
            int firstLinkOffset = 0, firstLinkBytes = 0;
            List<FileLink> linkParents = null;
            ulong fnModified = 0;
            var hasStandardInfo = false;
            ulong siModified = 0;
            uint? siFlags = null;
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
                                if (valueLength >= 36) siFlags = U32(value[32..]);
                                break;

                            case AttributeFileName when valueLength >= 66:
                                var nameBytes = value[64] * 2;
                                var rank = value[65] switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }; // Win32, Win32+DOS, POSIX, DOS
                                if (66 + nameBytes > valueLength) break;
                                var nameOffset = offset + valueOffset + 66; // Within the record
                                if (!isDirectory && value[65] != 2) // A DOS name shadows its Win32 pair - not a separate link
                                {
                                    if (linkCount++ == 0)
                                    {
                                        firstLinkParent = U64(value);
                                        firstLinkOffset = nameOffset;
                                        firstLinkBytes = nameBytes;
                                    }
                                    else
                                    {
                                        linkParents ??= new List<FileLink>(4)
                                        {
                                            new FileLink(firstLinkParent,
                                                DecodeName(record, firstLinkOffset, firstLinkBytes, namePool))
                                        };
                                        linkParents.Add(new FileLink(U64(value),
                                            DecodeName(record, nameOffset, nameBytes, namePool)));
                                    }
                                }
                                if (rank < bestNameRank)
                                {
                                    bestNameRank = rank;
                                    bestNameOffset = nameOffset;
                                    bestNameBytes = nameBytes;
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
            string name = null;
            if (bestNameOffset >= 0)
            {
                var chars = MemoryMarshal.Cast<byte, char>(record.Slice(bestNameOffset, bestNameBytes));
                name = index == RootEntryNumber ? new string(chars) : namePool.Canonicalize(chars);
            }
            if (!isDirectory && !hasDataSize)
                dataSize = fileNameSize;

            // Mask to standard FILE_ATTRIBUTE_* bits - $FILE_NAME flags carry 0x10000000 for directories,
            // which must not leak into FileAttributes (the header flag below is authoritative)
            //The $FILE_NAME copy can lag attribute-only changes until a name is updated.
            //Use the same authoritative standard information as the displayed timestamp.
            var attributes = (FileAttributes)((siFlags ?? fileNameFlags) & 0x00FFFFFF)
                & ~FileAttributes.Directory;
            if (isDirectory)
                attributes |= FileAttributes.Directory;

            records.ParentReference[index] = bestParentReference;
            records.NameRank[index] = (byte)(bestNameRank | (linkCount == 1 ? OwnSingleLink : 0));
            records.Sequence[index] = sequenceNumber;
            records.Name[index] = index == RootEntryNumber ? rootName : name;
            records.Attributes[index] = (uint)attributes;
            records.Size[index] = isDirectory ? 0 : dataSize;
            records.TimeTicks[index] = Time(hasStandardInfo ? siModified : fnModified);
            records.Live[index] = true;
            hardLinks = linkParents?.ToArray();

            // The unnamed $DATA lives in an extension record - resolve after the last chunk
            if (!isDirectory && !hasDataSize)
                pendingSizes.Enqueue(index);

            return true;
        }

        static string DecodeName(ReadOnlySpan<byte> record, int offset, int bytes, MftNamePool namePool)
            => namePool.Canonicalize(MemoryMarshal.Cast<byte, char>(record.Slice(offset, bytes)));

        /// <summary>
        /// Collect what an extension record contributes to its base: the unnamed $DATA
        /// size (resident, or the non-resident instance starting at VCN 0), the parents
        /// of any non-DOS $FILE_NAME (hard-link names overflowed from the base), and the
        /// best-ranked name itself - the base may have kept only its DOS 8.3 name
        /// </summary>
        static void ScanExtension(ReadOnlySpan<byte> record, ulong baseReference,
            NonBlocking.ConcurrentDictionary<uint, (ulong Size, ushort Sequence)> sizes,
            NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, List<FileLink> Parents)> links,
            NonBlocking.ConcurrentDictionary<uint, (ushort Sequence, int Rank, string Name, ulong Parent)> names,
            MftNamePool namePool)
        {
            var baseIndex = (uint)(baseReference & FileReferenceMask);
            var baseSequence = (ushort)(baseReference >> 48);
            List<FileLink> parents = null;
            int bestNameOffset = -1, bestNameBytes = 0;
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
                        var nameBytes = value[64] * 2;
                        if (66 + nameBytes > valueLength) { offset += length; continue; }
                        var nameOffset = offset + valueOffset + 66;
                        if (value[65] != 2) // A DOS name shadows its Win32 pair - not a separate link
                            (parents ??= new List<FileLink>(2)).Add(new FileLink(U64(value),
                                DecodeName(record, nameOffset, nameBytes, namePool)));
                        var rank = value[65] switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }; // Win32, Win32+DOS, POSIX, DOS
                        if (rank < bestRank)
                        {
                            bestRank = rank;
                            bestNameOffset = nameOffset;
                            bestNameBytes = nameBytes;
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
                        var merged = new List<FileLink>(old.Parents.Count + parents.Count);
                        merged.AddRange(old.Parents);
                        merged.AddRange(parents);
                        return (old.Sequence, merged);
                    });

            if (bestNameOffset >= 0)
            {
                var bestName = DecodeName(record, bestNameOffset, bestNameBytes, namePool);
                names.AddOrUpdate(baseIndex, (baseSequence, bestRank, bestName, bestParent),
                    (_, old) => old.Rank <= bestRank ? old : (baseSequence, bestRank, bestName, bestParent));
            }
        }

        /// <summary>
        /// A file reference is current only when its embedded sequence number matches the
        /// record's - a mismatch means the record was freed and reused while the $MFT
        /// streamed by. Zero acts as a wildcard (references written without a sequence).
        /// </summary>
        static bool SequencesMatch(ushort reference, ushort record)
            => reference == 0 || record == 0 || reference == record;

        /// <summary>
        /// Remove records whose parent chain does not reach the drive root. Their parent was
        /// deleted or its record reused while the $MFT streamed by (NTFS only deletes empty
        /// directories, so such a file is normally deleted too), or the records are corrupt
        /// or cyclic. Either way the real path is unknowable - a made-up one would collide
        /// with the drive root and break every file operation. Anything that still exists
        /// is re-delivered by the change watcher or the next rescan.
        /// </summary>
        static void DropOrphans(Records records, CancellationToken cancellationToken)
        {
            if (records.Count <= RootEntryNumber || !records.Live[RootEntryNumber])
            {
                //Without the root no canonical path can be constructed. Returning nodes
                //under invented paths is more dangerous than retrying on the next scan.
                Array.Clear(records.Live);
                return;
            }

            const byte Keep = 1, Drop = 2, Visiting = 3;
            var state = new byte[records.Count];
            var chain = new List<int>(64);
            for (var start = 0; start < records.Count; start++)
            {
                if ((start & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!records.Live[start]) continue;
                var entry = start;
                byte verdict = 0;
                while (verdict == 0)
                {
                    var seen = state[entry];
                    if (seen == Keep || seen == Drop) verdict = seen;
                    else if (seen == Visiting) verdict = Drop; // Parent cycle
                    else if (entry == RootEntryNumber) verdict = Keep;
                    else if (records.Parent[entry] < 0) verdict = Drop;
                    else
                    {
                        state[entry] = Visiting;
                        chain.Add(entry);
                        entry = records.Parent[entry];
                    }
                }
                if (state[entry] == 0) state[entry] = verdict; // The deciding node itself
                foreach (var visited in chain) state[visited] = verdict;
                chain.Clear();
            }

            for (var i = 0; i < records.Count; i++)
                if (records.Live[i] && state[i] == Drop) records.Live[i] = false;
        }

        static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);

        // Local time, not UTC: FileNode (watcher/walk) reports FileSystemInfo local times
        // and the grid binds the values directly - UTC here showed MFT rows shifted by the
        // whole UTC offset against the same file re-indexed by the watcher
        static long Time(ulong fileTime)
            => fileTime == 0 || fileTime > MaxFileTime ? 0 : DateTime.FromFileTime((long)fileTime).Ticks;

        /// <summary>
        /// A file counts once per hard link (per non-DOS $FILE_NAME), so folder sizes
        /// and item counts match what a directory walk and Explorer's folder properties
        /// report. Directory Count is the number of descendants and excludes itself.
        ///
        /// Aggregate direct file contributions first, then fold each directory into its
        /// parent exactly once (children are complete before their parent). The parent
        /// reference column is dead after tree linking, so it is reused as the size scratch.
        /// Directory path hashes are finalized top-down; file hashes then use their parent's
        /// hash during the dense build instead of re-hashing every ancestor.
        /// </summary>
        static void CalculateFolderSizesAndDirectoryPathHashes(Records records,
            NonBlocking.ConcurrentDictionary<uint, FileLink[]> hardLinks, string rootFullName,
            CancellationToken cancellationToken)
        {
            var folderSizes = records.ParentReference;
            Array.Clear(folderSizes);
            if (records.Count <= RootEntryNumber || !records.Live[RootEntryNumber]) return;

            var depth = records.Depth; //Temporarily the directory depth; overwritten by the path hash below
            var directories = new List<int>();
            var chain = new List<int>(64);
            depth[RootEntryNumber] = 1;
            var maxDepth = 1;
            for (var entry = 0; entry < records.Count; entry++)
            {
                if ((entry & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!records.IsLiveDirectory(entry)) continue;
                directories.Add(entry);
                if (depth[entry] == 0)
                {
                    //DropOrphans already proved that every surviving chain reaches the root.
                    var current = entry;
                    while (current >= 0 && depth[current] == 0)
                    {
                        chain.Add(current);
                        current = records.Parent[current];
                    }
                    var d = current >= 0 ? depth[current] : 0;
                    for (var i = chain.Count - 1; i >= 0; i--)
                        depth[chain[i]] = ++d;
                    chain.Clear();
                }
                if (depth[entry] > maxDepth) maxDepth = depth[entry];
            }

            var levels = new List<int>[maxDepth + 1];
            foreach (var directory in directories)
                (levels[depth[directory]] ??= new List<int>()).Add(directory);

            void Add(int parent, ulong size, uint count)
            {
                if (parent < 0 || !records.IsDirectory(parent)) return;
                folderSizes[parent] = unchecked(folderSizes[parent] + size);
                records.Descendants[parent] = (uint)Math.Min(uint.MaxValue, (ulong)records.Descendants[parent] + count);
            }

            //Each file contributes only to its immediate link parent(s).
            for (var entry = 0; entry < records.Count; entry++)
            {
                if ((entry & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!records.Live[entry] || records.IsDirectory(entry)) continue;

                if (!hardLinks.TryGetValue((uint)entry, out var links))
                    Add(records.Parent[entry], records.Size[entry], 1);
                else
                {
                    foreach (var link in links)
                    {
                        var parentEntry = (uint)(link.Parent & FileReferenceMask);
                        if (parentEntry < (uint)records.Count && parentEntry != (uint)entry
                            && records.Live[parentEntry]
                            && SequencesMatch((ushort)(link.Parent >> 48), records.Sequence[parentEntry]))
                            Add((int)parentEntry, records.Size[entry], 1);
                    }
                }
            }

            //Children are complete before their parent, so every directory is propagated
            //once. It contributes its aggregate bytes, plus itself and all descendants.
            for (var level = maxDepth; level >= 1; level--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (levels[level] == null) continue;
                foreach (var directory in levels[level])
                {
                    records.Size[directory] = folderSizes[directory];
                    Add(records.Parent[directory], records.Size[directory], records.Descendants[directory] + 1);
                }
            }

            //Replace the temporary depth with the real cached path hash, parents first.
            var hashes = records.Depth;
            foreach (var directory in levels[1])
                hashes[directory] = (int)NodePath.HashChars(NodePath.FnvSeed, rootFullName);
            for (var level = 2; level <= maxDepth; level++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (levels[level] == null) continue;
                foreach (var directory in levels[level])
                    hashes[directory] = ChildPathHash(records, records.Parent[directory], records.Name[directory], rootFullName);
            }
        }

        /// <summary>The path hash of a child from its parent's finished hash - NodePath.ComputeChildPathHash for columns.</summary>
        static int ChildPathHash(Records records, int parent, string name, string rootFullName)
        {
            var hash = (uint)records.Depth[parent];
            //The root's own full name ends with the separator; every other directory's tail is its name
            var tailEndsWithSeparator = parent == RootEntryNumber && rootFullName[^1] == '\\';
            if (!tailEndsWithSeparator) hash = (hash ^ '\\') * NodePath.FnvPrime;
            return (int)NodePath.HashChars(hash, name);
        }

        /// <summary>
        /// Compact the sparse entry columns into the dense row table (entry order kept, the
        /// hard-link alias rows appended after every base row) and encode the shared name blob.
        /// </summary>
        static MftTable BuildTable(Records records, NonBlocking.ConcurrentDictionary<uint, FileLink[]> baseHardLinks,
            Func<FileLink, bool> linkParentIsLive, string rootName, MftNamePoolStats nameStats,
            long readParseMs, long linkMs, long aggregateHashMs, Stopwatch phase, CancellationToken cancellationToken)
        {
            var rootFullName = rootName + Path.DirectorySeparatorChar;
            var count = records.Count;
            var rowByEntry = new int[count];
            var rowCount = 0;
            for (var entry = 0; entry < count; entry++)
            {
                if ((entry & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (records.Live[entry]) rowByEntry[entry] = ++rowCount;
            }

            // Search indexes every hard-link name; the FRN table keeps one identity per record.
            // Aliases come after aggregation, which already counted each link.
            var aliasSpecs = new List<(int Entry, int ParentEntry, string Name)>();
            var hardLinkSets = new List<(uint Entry, ulong[] Parents)>();
            foreach (var (entry, links) in baseHardLinks.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry >= (uint)count || !records.Live[entry]) continue;
                var live = links.Where(linkParentIsLive).ToArray();
                if (live.Length > 1) hardLinkSets.Add((entry, live.Select(link => link.Parent).ToArray()));
                foreach (var link in links)
                {
                    var parentEntry = (int)(link.Parent & FileReferenceMask);
                    if (parentEntry == records.Parent[entry] && link.Name == records.Name[entry]) continue;
                    if (parentEntry >= count || !records.Live[parentEntry]) continue;
                    aliasSpecs.Add(((int)entry, parentEntry, link.Name));
                }
            }

            var total = checked(rowCount + aliasSpecs.Count);
            var parent = new int[total];
            var nameOffset = new int[total];
            var size = new ulong[total];
            var time = new long[total];
            var attributes = new uint[total];
            var frn = new ulong[total];
            var pathHash = new int[total];
            var descendants = new uint[total];
            //Names are pooled canonical instances, so the blob deduplicates by reference:
            //hashing a pointer instead of 50 characters for every one of millions of rows.
            var blob = new NameBlobBuilder(Math.Max(1 << 16, rowCount * 12), byReference: true);
            var rootRow = count > RootEntryNumber && records.Live[RootEntryNumber]
                ? rowByEntry[RootEntryNumber] - 1 : -1;

            //The blob appends sequentially; everything else fills in parallel.
            for (var entry = 0; entry < count; entry++)
            {
                if ((entry & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var slot = rowByEntry[entry];
                if (slot != 0) nameOffset[slot - 1] = blob.Add(records.Name[entry]);
            }
            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.ForEach(Partitioner.Create(0, count, 65536), options, range =>
            {
                for (var entry = range.Item1; entry < range.Item2; entry++)
                {
                    var slot = rowByEntry[entry];
                    if (slot == 0) continue;
                    var row = slot - 1;
                    var parentEntry = records.Parent[entry];
                    parent[row] = parentEntry < 0 ? -1 : rowByEntry[parentEntry] - 1;
                    size[row] = records.Size[entry];
                    time[row] = records.TimeTicks[entry];
                    attributes[row] = records.Attributes[entry];
                    frn[row] = ((ulong)records.Sequence[entry] << 48) | (uint)entry;
                    descendants[row] = records.Descendants[entry];
                    pathHash[row] = records.IsDirectory(entry)
                        ? records.Depth[entry]
                        : ChildPathHash(records, parentEntry, records.Name[entry], rootFullName);
                }
            });

            var aliasRows = new Dictionary<ulong, int[]>();
            var aliasRow = rowCount;
            foreach (var group in aliasSpecs.GroupBy(spec => spec.Entry))
            {
                var baseRow = rowByEntry[group.Key] - 1;
                var rows = new List<int>();
                foreach (var (entry, parentEntry, name) in group)
                {
                    var row = aliasRow++;
                    parent[row] = rowByEntry[parentEntry] - 1;
                    nameOffset[row] = blob.Add(name);
                    size[row] = size[baseRow];
                    time[row] = time[baseRow];
                    attributes[row] = attributes[baseRow];
                    frn[row] = frn[baseRow];
                    pathHash[row] = ChildPathHash(records, parentEntry, name, rootFullName);
                    rows.Add(row);
                }
                aliasRows[frn[baseRow]] = rows.ToArray();
            }

            //Retain only the sparse parent topology of genuinely multi-linked live files.
            //It lets the USN watcher diff one file's current links and adjust the affected
            //directory aggregates instead of rebuilding the complete MFT. A flat layout
            //avoids one dictionary/object allocation per multi-linked record.
            hardLinkSets.Sort((a, b) => a.Entry.CompareTo(b.Entry));
            var hardLinkedEntries = new uint[hardLinkSets.Count];
            var hardLinkOffsets = new int[hardLinkSets.Count + 1];
            var hardLinkParents = new ulong[hardLinkSets.Sum(x => x.Parents.Length)];
            var hardLinkAt = 0;
            for (var i = 0; i < hardLinkSets.Count; i++)
            {
                hardLinkedEntries[i] = hardLinkSets[i].Entry;
                hardLinkOffsets[i] = hardLinkAt;
                hardLinkSets[i].Parents.CopyTo(hardLinkParents, hardLinkAt);
                hardLinkAt += hardLinkSets[i].Parents.Length;
            }
            hardLinkOffsets[hardLinkSets.Count] = hardLinkAt;

            var timing = new MftLoadTiming(readParseMs, linkMs, aggregateHashMs, phase.ElapsedMilliseconds,
                nameStats.NamesSeen, nameStats.UniqueNames, nameStats.SavedBytes);
            return new MftTable(rootFullName, rootRow, parent, nameOffset, size, time, attributes, frn,
                pathHash, descendants, blob.ToArray(), rowByEntry, hardLinkedEntries, hardLinkOffsets,
                hardLinkParents, aliasRows, timing);
        }
    }
}
