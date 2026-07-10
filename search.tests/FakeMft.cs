using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using search.Models;

namespace search.Tests
{
    /// <summary>
    /// Builds a synthetic raw $MFT image - FILE records with a real update sequence
    /// array (so MftFixup must succeed on them), resident/non-resident attributes,
    /// attribute lists and extension records - for any record size.
    /// </summary>
    sealed class FakeMft
    {
        public const string Root = @"Q:\";
        public const uint RootEntry = 5;

        readonly List<byte[]> records = new();

        public FakeMft(int bytesPerRecord) => BytesPerRecord = bytesPerRecord;

        public int BytesPerRecord { get; }
        public int Count => records.Count;

        /// <summary>Append zeroed (invalid) records, e.g. for the reserved entries 0-4</summary>
        public FakeMft AddEmpty(int count = 1)
        {
            for (var i = 0; i < count; i++)
                records.Add(new byte[BytesPerRecord]);
            return this;
        }

        public FakeMft AddRaw(byte[] record)
        {
            if (record.Length != BytesPerRecord) throw new ArgumentException();
            records.Add(record);
            return this;
        }

        public FakeMft AddRecord(bool inUse = true, bool directory = false, ulong baseReference = 0, params byte[][] attributes)
            => AddRaw(Record(BytesPerRecord, inUse, directory, baseReference, attributes));

        /// <summary>The root directory record; must land on entry 5</summary>
        public FakeMft AddRoot()
        {
            if (Count != RootEntry) throw new InvalidOperationException($"The root must be entry {RootEntry}.");
            return AddRecord(directory: true, attributes: new[] { FileName(RootEntry, ".", ns: 3) });
        }

        public byte[] Image(int tailBytes = 0)
        {
            var image = new byte[(long)records.Count * BytesPerRecord + tailBytes];
            for (var i = 0; i < records.Count; i++)
                records[i].CopyTo(image, (long)i * BytesPerRecord);
            return image;
        }

        /// <summary>Run the streaming parse over the image; tailBytes exercises a partial last record</summary>
        public List<INode> Parse(int chunkBytes = MftChunkReader.DefaultChunkBytes, int tailBytes = 0, Stream stream = null)
        {
            stream ??= new MemoryStream(Image(tailBytes));
            var length = (long)records.Count * BytesPerRecord + tailBytes;
            return MftDriveReader.GetNodes(stream, BytesPerRecord, length, Root, chunkBytes).ToList();
        }

        // ------------------------------------------------------------------
        // FILE record and attribute builders
        // ------------------------------------------------------------------

        public static byte[] Record(int size, bool inUse = true, bool directory = false, ulong baseReference = 0, params byte[][] attributes)
        {
            var record = new byte[size];
            record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';

            // One USA slot per 512-byte sector; sizes not divisible by 512 use a single stride
            var usaCount = (ushort)(size % 512 == 0 ? size / 512 + 1 : 2);
            var attributesOffset = (48 + 2 * usaCount + 7) & ~7;
            W16(record, 4, 48);
            W16(record, 6, usaCount);
            W16(record, 20, (ushort)attributesOffset);
            W16(record, 22, (ushort)((inUse ? 1 : 0) | (directory ? 2 : 0)));
            W64(record, 32, baseReference);

            var offset = attributesOffset;
            foreach (var attribute in attributes)
            {
                attribute.CopyTo(record, offset);
                offset += attribute.Length;
            }
            W32(record, offset, 0xffffffff); // attribute terminator

            // Inverse fixup: store the true sector-end bytes in the USA, stamp the USN over them
            var stride = size / (usaCount - 1);
            record[48] = 0xEF;
            record[49] = 0xBE;
            for (var i = 1; i < usaCount; i++)
            {
                var sectorEnd = i * stride - 2;
                record[48 + 2 * i] = record[sectorEnd];
                record[48 + 2 * i + 1] = record[sectorEnd + 1];
                record[sectorEnd] = 0xEF;
                record[sectorEnd + 1] = 0xBE;
            }

            return record;
        }

        public static byte[] Resident(uint type, byte[] value)
        {
            var length = (24 + value.Length + 7) & ~7;
            var attribute = new byte[length];
            W32(attribute, 0, type);
            W32(attribute, 4, (uint)length);
            W32(attribute, 16, (uint)value.Length);
            W16(attribute, 20, 24);
            value.CopyTo(attribute, 24);
            return attribute;
        }

        public static byte[] StandardInfo(DateTime created, DateTime modified, DateTime accessed)
        {
            var value = new byte[48];
            W64(value, 0, (ulong)created.ToFileTimeUtc());
            W64(value, 8, (ulong)modified.ToFileTimeUtc());
            W64(value, 24, (ulong)accessed.ToFileTimeUtc());
            return Resident(0x10, value);
        }

        /// <summary>$FILE_NAME; ns: 0 POSIX, 1 Win32, 2 DOS, 3 Win32+DOS</summary>
        public static byte[] FileName(uint parent, string name, ulong size = 0, uint flags = 0, byte ns = 1, DateTime created = default)
        {
            var value = new byte[66 + name.Length * 2];
            W64(value, 0, parent);
            if (created != default)
                W64(value, 8, (ulong)created.ToFileTimeUtc());
            W64(value, 48, size);
            W32(value, 56, flags);
            value[64] = (byte)name.Length;
            value[65] = ns;
            Encoding.Unicode.GetBytes(name).CopyTo(value, 66);
            return Resident(0x30, value);
        }

        public static byte[] ResidentData(int valueLength) => Resident(0x80, new byte[valueLength]);

        public static byte[] NonResidentData(ulong realSize)
        {
            var attribute = new byte[72];
            W32(attribute, 0, 0x80);
            W32(attribute, 4, 72);
            attribute[8] = 1;   // non-resident
            W16(attribute, 32, 64); // data runs offset (empty run list)
            W64(attribute, 40, realSize);
            W64(attribute, 48, realSize);
            W64(attribute, 56, realSize);
            return attribute;
        }

        /// <summary>$ATTRIBUTE_LIST with a single unnamed $DATA entry pointing at target</summary>
        public static byte[] AttributeListWithData(uint target)
        {
            var entry = new byte[32];
            W32(entry, 0, 0x80);
            W16(entry, 4, 32);
            W64(entry, 16, target);
            return Resident(0x20, entry);
        }

        static void W16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        static void W64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    }
}
