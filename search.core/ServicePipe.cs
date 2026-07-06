using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace search.Core
{
    /// <summary>
    /// Wire contract between the app and the WinSearchService MFT service.
    /// Shared by the server and the client so the framing cannot drift.
    ///
    /// Request:  version byte, length-prefixed UTF-8 volume mount point (e.g. "C:\").
    /// Response: status byte; on error a length-prefixed UTF-8 message;
    ///           on success int32 bytesPerMftRecord, int64 length, then exactly length raw $MFT bytes.
    /// All integers little-endian. One request per connection.
    /// </summary>
    public static class ServicePipe
    {
        public const string PipeName = "WinSearchMft";
        public const byte ProtocolVersion = 1;
        public const byte StatusOk = 0;
        public const byte StatusError = 1;

        static readonly Regex Volume = new(@"^([A-Za-z]:|\\\\\?\\Volume\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\})\\$", RegexOptions.Compiled);

        /// <summary>
        /// Accept only volume mount points ("C:\" or "\\?\Volume{guid}\") - never arbitrary paths
        /// </summary>
        public static bool IsValidVolume(string mountPoint) => mountPoint != null && Volume.IsMatch(mountPoint);

        public static void WriteString(Stream s, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? "");
            WriteInt32(s, bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        public static string ReadString(Stream s)
        {
            var length = ReadInt32(s);
            if (length < 0 || length > 4096)
                throw new InvalidDataException("Invalid string length on the service pipe.");
            var bytes = new byte[length];
            s.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteInt32(Stream s, int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            s.Write(b);
        }

        public static int ReadInt32(Stream s)
        {
            Span<byte> b = stackalloc byte[4];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        public static void WriteInt64(Stream s, long value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(b, value);
            s.Write(b);
        }

        public static long ReadInt64(Stream s)
        {
            Span<byte> b = stackalloc byte[8];
            s.ReadExactly(b);
            return BinaryPrimitives.ReadInt64LittleEndian(b);
        }
    }
}
