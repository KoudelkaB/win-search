using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace search.Models.LinuxService
{
    /// <summary>
    /// Wire contract between the app and the Linux file system service
    /// (FileSearchManager --service), shared by both sides so the framing cannot drift.
    ///
    /// A frame is a little-endian u32 payload length and the payload: a message type byte and
    /// its fields (BinaryWriter encoding - strings are 7-bit length-prefixed UTF-8).
    /// Client: Hello(version, mode) first, then Watch(root) for each root on one long-lived
    /// connection that receives Changes, or a single Walk(root) on a connection of its own.
    /// The service answers Hello with Welcome, Watch with WatchResult and streams WalkNodes
    /// chunks closed by WalkEnd. Times are UTC ticks; attributes are computed for the client.
    /// </summary>
    internal static class ServiceProtocol
    {
        public const string SocketPath = "/run/file-search-manager/service.sock";
        public const ushort Version = 1;
        public const int MaxRequestFrame = 64 * 1024;
        public const int MaxResponseFrame = 64 * 1024 * 1024;

        //Client -> service
        public const byte Hello = 1, Watch = 2, Walk = 3;
        //Service -> client
        public const byte Welcome = 101, WatchResult = 102, WalkNodes = 103, WalkEnd = 104, Changes = 110;
        //Hello modes: only what the user may see, or every file (administrators)
        public const byte ModeOwn = 0, ModeAll = 1;
        //Change kinds
        public const byte Upsert = 1, Delete = 2, Rename = 3, Rescan = 4;
        //What an Upsert of a folder says about the content below it
        public const byte ContentUnchanged = 0,
            //Appeared with content no change reports (moved in from where the client could not see it): index it
            ContentNew = 1,
            //Its permissions changed and the user can list it: index what is below unless indexed already
            ContentVisible = 2,
            //Its permissions changed and the user cannot list it any more: drop what is indexed below it
            ContentHidden = 3;

        public static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            stream.Write(length);
            stream.Write(payload);
            stream.Flush();
        }

        /// <summary>The next frame's payload; null at a clean end of the stream</summary>
        public static byte[] ReadFrame(Stream stream, int maxLength)
        {
            Span<byte> length = stackalloc byte[4];
            var read = stream.ReadAtLeast(length, 4, throwOnEndOfStream: false);
            if (read == 0) return null;
            if (read < 4) throw new EndOfStreamException();
            var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size <= 0 || size > maxLength) throw new InvalidDataException($"Frame of {size} bytes on the service socket.");
            var payload = new byte[size];
            stream.ReadExactly(payload);
            return payload;
        }

        /// <summary>A message under construction: its type byte is already written</summary>
        public static (MemoryStream Buffer, BinaryWriter Writer) Begin(byte type)
        {
            var buffer = new MemoryStream();
            var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
            writer.Write(type);
            return (buffer, writer);
        }

        public static void Send(Stream stream, (MemoryStream Buffer, BinaryWriter Writer) message)
        {
            message.Writer.Flush();
            WriteFrame(stream, message.Buffer.GetBuffer().AsSpan(0, (int)message.Buffer.Length));
            message.Writer.Dispose();
            message.Buffer.Dispose();
        }

        public static BinaryReader Open(byte[] payload, out byte type)
        {
            var reader = new BinaryReader(new MemoryStream(payload, writable: false), Encoding.UTF8);
            type = reader.ReadByte();
            return reader;
        }

        public static void WriteMetadata(BinaryWriter w, in ServiceMetadata m)
        {
            w.Write((uint)m.Attributes);
            w.Write(m.Size);
            w.Write(m.UtcTicks);
            w.Write(m.Count);
        }

        public static ServiceMetadata ReadMetadata(BinaryReader r)
            => new((FileAttributes)r.ReadUInt32(), r.ReadUInt64(), r.ReadInt64(), r.ReadInt64());

        public static void WriteChange(BinaryWriter w, in ServiceChange c)
        {
            w.Write(c.Kind);
            switch (c.Kind)
            {
                case Upsert:
                    w.Write(c.Path);
                    w.Write(c.Content);
                    WriteMetadata(w, c.Metadata);
                    break;
                case Delete:
                    w.Write(c.Path);
                    break;
                case Rename:
                    w.Write(c.OldPath);
                    w.Write(c.Path);
                    break;
            }
        }

        public static ServiceChange ReadChange(BinaryReader r)
        {
            var kind = r.ReadByte();
            return kind switch
            {
                Upsert => new ServiceChange(kind, r.ReadString(), null, r.ReadByte(), ReadMetadata(r)),
                Delete => new ServiceChange(kind, r.ReadString(), null, ContentUnchanged, default),
                Rename => ReadRename(r),
                Rescan => new ServiceChange(kind, null, null, ContentUnchanged, default),
                _ => throw new InvalidDataException($"Unknown change kind {kind} on the service socket.")
            };

            static ServiceChange ReadRename(BinaryReader r)
            {
                var old = r.ReadString();
                return new ServiceChange(Rename, r.ReadString(), old, ContentUnchanged, default);
            }
        }
    }

    /// <summary>
    /// What the service tells about one item: attributes as the client's own process would
    /// see them, the file size (a folder's total in a walk), the last write time in UTC ticks
    /// (0 = unknown) and, in a walk, the number of items below a folder
    /// </summary>
    internal readonly record struct ServiceMetadata(FileAttributes Attributes, ulong Size, long UtcTicks, long Count)
    {
        public bool IsDirectory => Attributes.HasFlag(FileAttributes.Directory);

        public NodeMetadataSnapshot ToSnapshot()
            => new(Attributes, IsDirectory ? 0 : Size,
                UtcTicks == 0 ? default : new DateTime(UtcTicks, DateTimeKind.Utc).ToLocalTime());

        public static ServiceMetadata From(NodeMetadataSnapshot utcSnapshot, long count = 0)
            => new(utcSnapshot.Attributes, utcSnapshot.Size, utcSnapshot.LastChangeTime.Ticks, count);
    }

    /// <summary>
    /// One change of a watched root. Upsert carries the item's state after the change (it
    /// exists); Rename moves an item - with its whole subtree - from OldPath to Path; Rescan
    /// asks for the root to be scanned again (lost events).
    /// Content: what an Upsert of a folder says about what is below it (ContentNew...).
    /// </summary>
    internal readonly record struct ServiceChange(byte Kind, string Path, string OldPath, byte Content,
        ServiceMetadata Metadata)
    {
        public static ServiceChange Of(byte kind, string path = null, string oldPath = null)
            => new(kind, path, oldPath, ServiceProtocol.ContentUnchanged, default);
    }
}
