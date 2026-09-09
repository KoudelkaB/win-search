using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace search.Models
{
    /// <summary>
    /// One row's name viewed in place inside the <see cref="MftTable"/> name blob - Latin-1
    /// bytes for the overwhelming majority of names, UTF-16 for the few that need it. All
    /// comparisons work without materializing a string; the callers that need chars get
    /// them into a caller-provided scratch span (names are at most 255 chars).
    /// </summary>
    internal readonly ref struct NameSpan
    {
        public const int MaxChars = 256;
        readonly ReadOnlySpan<byte> latin1;
        readonly ReadOnlySpan<char> wide;
        readonly bool isWide;

        public NameSpan(ReadOnlySpan<byte> latin1)
        {
            this.latin1 = latin1;
            wide = default;
            isWide = false;
        }

        public NameSpan(ReadOnlySpan<char> chars)
        {
            latin1 = default;
            wide = chars;
            isWide = true;
        }

        public int Length => isWide ? wide.Length : latin1.Length;
        public bool IsEmpty => Length == 0;
        public char this[int index] => isWide ? wide[index] : (char)latin1[index];
        public char Last => Length == 0 ? '\0' : this[Length - 1];

        public void CopyTo(Span<char> destination)
        {
            if (isWide) wide.CopyTo(destination);
            else Encoding.Latin1.GetChars(latin1, destination);
        }

        /// <summary>The characters, decoded into scratch when they are not already chars.</summary>
        public ReadOnlySpan<char> Chars(Span<char> scratch)
        {
            if (isWide) return wide;
            if (latin1.Length > scratch.Length) scratch = new char[latin1.Length];
            var count = Encoding.Latin1.GetChars(latin1, scratch);
            return scratch[..count];
        }

        public bool EqualsIgnoreCase(ReadOnlySpan<char> other)
        {
            if (other.Length != Length) return false;
            if (isWide) return wide.Equals(other, StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < other.Length; i++)
                if (char.ToUpperInvariant((char)latin1[i]) != char.ToUpperInvariant(other[i]))
                    return false;
            return true;
        }

        public bool EqualsIgnoreCase(NameSpan other)
        {
            if (other.Length != Length) return false;
            if (!isWide && !other.isWide)
            {
                for (var i = 0; i < latin1.Length; i++)
                    if (char.ToUpperInvariant((char)latin1[i]) != char.ToUpperInvariant((char)other.latin1[i]))
                        return false;
                return true;
            }
            Span<char> scratch = stackalloc char[MaxChars];
            return EqualsIgnoreCase(other.Chars(scratch));
        }

        /// <summary>Continue the path hash exactly like NodePath hashes the same chars.</summary>
        public uint Hash(uint hash)
        {
            if (isWide)
            {
                foreach (var c in wide) hash = (hash ^ char.ToUpperInvariant(c)) * NodePath.FnvPrime;
            }
            else
            {
                foreach (var b in latin1) hash = (hash ^ char.ToUpperInvariant((char)b)) * NodePath.FnvPrime;
            }
            return hash;
        }

        public int CompareTo(ReadOnlySpan<char> other, StringComparison comparison)
        {
            Span<char> scratch = stackalloc char[MaxChars];
            return Chars(scratch).CompareTo(other, comparison);
        }

        public int CompareTo(NameSpan other, StringComparison comparison)
        {
            Span<char> a = stackalloc char[MaxChars];
            Span<char> b = stackalloc char[MaxChars];
            return Chars(a).CompareTo(other.Chars(b), comparison);
        }

        public bool Contains(ReadOnlySpan<char> value, StringComparison comparison)
        {
            Span<char> scratch = stackalloc char[MaxChars];
            return Chars(scratch).Contains(value, comparison);
        }

        public bool StartsWith(ReadOnlySpan<char> value, StringComparison comparison)
        {
            Span<char> scratch = stackalloc char[MaxChars];
            return Chars(scratch).StartsWith(value, comparison);
        }

        public bool EndsWith(ReadOnlySpan<char> value, StringComparison comparison)
        {
            Span<char> scratch = stackalloc char[MaxChars];
            return Chars(scratch).EndsWith(value, comparison);
        }

        public bool Equals(ReadOnlySpan<char> value, StringComparison comparison)
        {
            Span<char> scratch = stackalloc char[MaxChars];
            return Chars(scratch).Equals(value, comparison);
        }

        public override string ToString()
            => isWide ? new string(wide) : Encoding.Latin1.GetString(latin1);
    }

    /// <summary>
    /// Deduplicating builder of the name blob: every distinct name is stored once as a
    /// 16-bit length prefix (bit 15 = UTF-16 payload) followed by Latin-1 bytes or, for the
    /// rare name outside Latin-1, UTF-16 code units at an even offset.
    /// </summary>
    internal sealed class NameBlobBuilder
    {
        const int WideFlag = 0x8000;
        readonly Dictionary<string, int> offsets;
        byte[] blob;
        int length;

        /// <param name="byReference">
        /// Deduplicate by string instance instead of content - for callers whose names are
        /// already canonical (pooled) instances, which makes every lookup a pointer hash.
        /// </param>
        public NameBlobBuilder(int estimatedBytes = 1 << 16, bool byReference = false)
        {
            blob = new byte[Math.Max(16, estimatedBytes)];
            offsets = byReference
                ? new Dictionary<string, int>(ReferenceEqualityComparer.Instance)
                : new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public int UniqueNames => offsets.Count;
        public int Length => length;

        public int Add(string name)
        {
            name ??= "";
            if (offsets.TryGetValue(name, out var existing)) return existing;
            if (name.Length >= WideFlag)
                throw new InvalidDataException($"Name too long for the name blob: {name.Length} chars.");
            var wide = false;
            foreach (var c in name)
                if (c > 0xFF)
                {
                    wide = true;
                    break;
                }
            var start = length;
            if (wide && (start & 1) != 0) start++; //UTF-16 payload starts on an even offset
            var payload = wide ? name.Length * 2 : name.Length;
            Ensure(start + 2 + payload);
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(start),
                (ushort)(name.Length | (wide ? WideFlag : 0)));
            if (wide)
                MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(blob.AsSpan(start + 2));
            else
                Encoding.Latin1.GetBytes(name, blob.AsSpan(start + 2));
            length = start + 2 + payload;
            offsets[name] = start;
            return start;
        }

        public byte[] ToArray()
        {
            var result = new byte[length];
            Array.Copy(blob, result, length);
            return result;
        }

        void Ensure(int needed)
        {
            if (needed <= blob.Length) return;
            var grown = new byte[Math.Max(needed, checked(blob.Length * 2))];
            Array.Copy(blob, grown, length);
            blob = grown;
        }

        public static NameSpan Read(byte[] blob, int offset)
        {
            var prefix = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset));
            var count = prefix & ~WideFlag;
            return (prefix & WideFlag) != 0
                ? new NameSpan(MemoryMarshal.Cast<byte, char>(blob.AsSpan(offset + 2, count * 2)))
                : new NameSpan(blob.AsSpan(offset + 2, count));
        }
    }

    /// <summary>
    /// The immutable result of one $MFT scan as columns, not objects: every row is a live
    /// file or directory (plus one extra row per additional hard-link name). Nothing is
    /// allocated per file - names live once in a shared blob, parents are row indexes and
    /// the file reference table is a row number per MFT entry. The rest of the app still
    /// speaks <see cref="INode"/>: rows are exposed through small flyweight
    /// <see cref="MftNode"/> handles that are created on first use and cached, so the same
    /// row always yields the same object and reference identity keeps working everywhere.
    /// Hot paths (filtering, top-N sorts, path hashing) read the columns directly.
    /// </summary>
    internal sealed class MftTable : IFrnNodeSource
    {
        const ulong FileReferenceMask = 0xffffffffffff;
        const int ChunkBits = 10;
        const int ChunkSize = 1 << ChunkBits;

        internal readonly int[] Parent;         //Row of the parent directory; -1 = path-terminal (drive root)
        internal readonly int[] NameOffset;     //Into Names
        internal readonly ulong[] Size;         //Files: data size; directories: aggregated subtree bytes
        internal readonly long[] TimeTicks;     //Local DateTime ticks; 0 = unknown
        internal readonly uint[] Attributes;
        internal readonly ulong[] Frn;          //sequence << 48 | entry
        internal readonly int[] PathHash;       //NodePath hash of the row's full path
        internal readonly uint[] Descendants;   //Directories: entries below; files: unused
        internal readonly byte[] Names;
        readonly int[] rowByEntry;              //Base row + 1 per MFT entry; 0 = no live record
        readonly uint[] hardLinkedEntries;
        readonly int[] hardLinkOffsets;
        readonly ulong[] hardLinkParents;
        readonly Dictionary<ulong, int[]> aliasRows;
        readonly MftNode[][] handles;
        readonly RowList rows;

        public MftTable(string root, int rootRow, int[] parent, int[] nameOffset, ulong[] size,
            long[] timeTicks, uint[] attributes, ulong[] frn, int[] pathHash, uint[] descendants,
            byte[] names, int[] rowByEntry, uint[] hardLinkedEntries, int[] hardLinkOffsets,
            ulong[] hardLinkParents, Dictionary<ulong, int[]> aliasRows, MftLoadTiming loadTiming)
        {
            Root = root;
            RootRow = rootRow;
            Parent = parent;
            NameOffset = nameOffset;
            Size = size;
            TimeTicks = timeTicks;
            Attributes = attributes;
            Frn = frn;
            PathHash = pathHash;
            Descendants = descendants;
            Names = names;
            this.rowByEntry = rowByEntry;
            this.hardLinkedEntries = hardLinkedEntries;
            this.hardLinkOffsets = hardLinkOffsets;
            this.hardLinkParents = hardLinkParents;
            this.aliasRows = aliasRows;
            LoadTiming = loadTiming;
            handles = new MftNode[(parent.Length + ChunkSize - 1) >> ChunkBits][];
            rows = new RowList(this);
        }

        /// <summary>Full path of the drive root, e.g. "C:\" - the terminal of every chain.</summary>
        public string Root { get; }
        public int RootRow { get; }
        public int Count => Parent.Length;
        public MftLoadTiming LoadTiming { get; }
        public IReadOnlyList<INode> DenseNodes => rows;

        /// <summary>Bytes of column storage per row (without the shared name blob).</summary>
        public const int BytesPerRow = 4 + 4 + 8 + 8 + 4 + 8 + 4 + 4;

        public long StorageBytes => (long)Count * BytesPerRow + Names.LongLength
            + (long)rowByEntry.Length * sizeof(int);

        // ------------------------------------------------------------------
        // Column access
        // ------------------------------------------------------------------

        public bool IsDirectory(int row) => (Attributes[row] & (uint)FileAttributes.Directory) != 0;
        public NameSpan NameAt(int row) => NameBlobBuilder.Read(Names, NameOffset[row]);
        public string NameString(int row) => NameAt(row).ToString();
        public DateTime TimeAt(int row)
        {
            var ticks = Volatile.Read(ref TimeTicks[row]);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Local);
        }

        internal static long TicksOf(DateTime time) => time == DateTime.MinValue ? 0 : time.Ticks;

        /// <summary>
        /// Path-terminal rows print their own name plus the separator (the drive root, so
        /// "C:" + "\"); every other row is its chain of names below that.
        /// </summary>
        public string FullName(int row)
        {
            var terminal = row;
            var length = 0;
            var depth = 0;
            for (var guard = 0; Parent[terminal] >= 0 && guard < NodePath.MaxWalk; guard++)
            {
                length += NameAt(terminal).Length + 1;
                terminal = Parent[terminal];
                depth++;
            }
            var prefix = TerminalFullName(terminal);
            if (depth == 0) return prefix;
            var total = prefix.Length + length - (prefix.Length > 0 && prefix[^1] == '\\' ? 1 : 0);
            return string.Create(total, (this, row, prefix, depth), static (span, state) =>
            {
                var (table, leaf, prefix, depth) = state;
                var at = span.Length;
                var current = leaf;
                for (var i = 0; i < depth; i++)
                {
                    var name = table.NameAt(current);
                    at -= name.Length;
                    name.CopyTo(span.Slice(at, name.Length));
                    if (i + 1 < depth || prefix.Length == 0 || prefix[^1] != '\\')
                        span[--at] = '\\';
                    current = table.Parent[current];
                }
                prefix.AsSpan().CopyTo(span);
            });
        }

        string TerminalFullName(int row)
            => row == RootRow ? Root : NameString(row) + Path.DirectorySeparatorChar;

        // ------------------------------------------------------------------
        // Handles
        // ------------------------------------------------------------------

        /// <summary>The one INode object standing for this row - created on first use.</summary>
        public MftNode Handle(int row)
        {
            var chunk = handles[row >> ChunkBits];
            if (chunk == null)
            {
                var fresh = new MftNode[Math.Min(ChunkSize, Count - (row & ~(ChunkSize - 1)))];
                chunk = Interlocked.CompareExchange(ref handles[row >> ChunkBits], fresh, null) ?? fresh;
            }
            var slot = row & (ChunkSize - 1);
            var existing = chunk[slot];
            if (existing != null) return existing;
            var created = new MftNode(this, row);
            return Interlocked.CompareExchange(ref chunk[slot], created, null) ?? created;
        }

        /// <summary>The row of a handle belonging to this table, else -1.</summary>
        public int RowOf(INode node) => node is MftNode h && ReferenceEquals(h.Table, this) ? h.Row : -1;

        /// <summary>How many handles exist - diagnostics only.</summary>
        internal int MaterializedHandles
        {
            get
            {
                var count = 0;
                foreach (var chunk in handles)
                    if (chunk != null)
                        foreach (var handle in chunk)
                            if (handle != null) count++;
                return count;
            }
        }

        // ------------------------------------------------------------------
        // File references and hard links
        // ------------------------------------------------------------------

        public bool TryGetRowByFrn(ulong frn, out int row)
        {
            var entry = frn & FileReferenceMask;
            if (entry < (ulong)rowByEntry.Length)
            {
                var slot = rowByEntry[(int)entry];
                if (slot != 0 && Frn[slot - 1] == frn)
                {
                    row = slot - 1;
                    return true;
                }
            }
            row = -1;
            return false;
        }

        public bool TryGetByFrn(ulong frn, out INode node)
        {
            if (TryGetRowByFrn(frn, out var row))
            {
                node = Handle(row);
                return true;
            }
            node = null;
            return false;
        }

        public bool HasMultipleLinks(ulong frn)
            => TryGetRowByFrn(frn, out _)
                && Array.BinarySearch(hardLinkedEntries, (uint)(frn & FileReferenceMask)) >= 0;

        public bool TryGetLinkParents(ulong frn, out ReadOnlyMemory<ulong> parents)
        {
            parents = default;
            if (!TryGetRowByFrn(frn, out _)) return false;
            var at = Array.BinarySearch(hardLinkedEntries, (uint)(frn & FileReferenceMask));
            if (at < 0) return false;
            parents = new ReadOnlyMemory<ulong>(hardLinkParents,
                hardLinkOffsets[at], hardLinkOffsets[at + 1] - hardLinkOffsets[at]);
            return true;
        }

        public IReadOnlyList<INode> GetFileLinks(ulong frn)
        {
            if (!TryGetRowByFrn(frn, out var row)) return Array.Empty<INode>();
            if (!aliasRows.TryGetValue(frn, out var aliases)) return new INode[] { Handle(row) };
            var result = new INode[aliases.Length + 1];
            result[0] = Handle(row);
            for (var i = 0; i < aliases.Length; i++) result[i + 1] = Handle(aliases[i]);
            return result;
        }

        public IEnumerator<INode> GetEnumerator() => rows.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>The table behind a dense node list, when the list is a table's row view.</summary>
        public static MftTable TryFromDense(IEnumerable<INode> nodes)
            => nodes switch
            {
                MftTable table => table,
                RowList list => list.Table,
                _ => null
            };

        sealed class RowList : IReadOnlyList<INode>
        {
            public readonly MftTable Table;
            readonly MftTable table;
            public RowList(MftTable table) => Table = this.table = table;
            public int Count => table.Count;
            public INode this[int index] => table.Handle(index);
            public IEnumerator<INode> GetEnumerator()
            {
                for (var i = 0; i < table.Count; i++) yield return table.Handle(i);
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    /// <summary>
    /// Flyweight over one <see cref="MftTable"/> row: 40 bytes that exist only while
    /// something (the grid, a delta, a watcher map) actually refers to the row. All values
    /// read and write the table's columns, so a handle never goes stale and directory
    /// aggregates maintained through it are visible to row-based sweeps as well.
    /// </summary>
    internal sealed class MftNode : INode
    {
        internal readonly MftTable Table;
        internal readonly int Row;
        string name; //Materialized on first use; the grid and window sorts read it repeatedly

        internal MftNode(MftTable table, int row)
        {
            Table = table;
            Row = row;
        }

        public uint EntryNumber => (uint)(Frn & 0xffffffffffff);
        public ushort SequenceNumber => (ushort)(Frn >> 48);
        public override ulong Frn => Table.Frn[Row];

        public override FileAttributes Attributes
        {
            get => (FileAttributes)Table.Attributes[Row];
            protected set => Table.Attributes[Row] = (uint)value;
        }

        public override string Name => name ??= Table.NameString(Row);

        public override ulong Size
        {
            get => Volatile.Read(ref Table.Size[Row]);
            protected set => Volatile.Write(ref Table.Size[Row], value);
        }

        public override uint Count
        {
            get => IsDirectory ? Volatile.Read(ref Table.Descendants[Row]) : 1U;
            protected set
            {
                if (IsDirectory) Volatile.Write(ref Table.Descendants[Row], value);
            }
        }

        public override string FullName => Table.FullName(Row);
        public override INode PathParent => Table.Parent[Row] is var parent and >= 0 ? Table.Handle(parent) : null;
        public override string ParentName => Table.Parent[Row] is var parent and >= 0 ? Table.NameString(parent) : "";
        public override string Folder => Table.Parent[Row] is var parent and >= 0 ? Table.FullName(parent) : "";

        public override DateTime LastChangeTime
        {
            get => Table.TimeAt(Row);
            protected set => Volatile.Write(ref Table.TimeTicks[Row], MftTable.TicksOf(value));
        }

        internal override bool TryGetPathHash(out int hash)
        {
            hash = Table.PathHash[Row];
            return true;
        }
    }
}
