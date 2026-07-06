using System;
using System.Buffers.Binary;

namespace search.Core
{
    public static class MftFixup
    {
        /// <summary>
        /// Apply the NTFS update sequence array to a FILE record in place.
        /// Returns false when the record is not a valid FILE record.
        /// </summary>
        public static bool Apply(Span<byte> record)
        {
            if (record.Length < 8 || record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
                return false;

            var usaOffset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var usaCount = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
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
    }
}
