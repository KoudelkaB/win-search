using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using search.Core;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class NtfsMetadataTests
    {
        [Fact]
        public void ServiceMetadataFramingRoundTripsHitsAndMisses()
        {
            var expected = new NtfsFileMetadata(0x21, 123456789,
                DateTime.UtcNow.ToFileTimeUtc());
            using var stream = new MemoryStream();
            ServicePipe.WriteMetadata(stream, null);
            ServicePipe.WriteMetadata(stream, expected);

            stream.Position = 0;
            Assert.Null(ServicePipe.ReadMetadata(stream));
            Assert.Equal(expected, ServicePipe.ReadMetadata(stream));
            Assert.Equal(stream.Length, stream.Position);
        }

        [Fact]
        public void SnapshotsApplyAllAttributesAndPreserveFileReferences()
        {
            var changed = new DateTime(2026, 7, 23, 12, 0, 0);
            var snapshot = new NodeMetadataSnapshot(
                FileAttributes.ReadOnly | FileAttributes.Hidden, 42, changed);
            var node = new FrnFileNode(@"C:\protected.txt", snapshot,
                0x0007_00000000002A);

            Assert.True(node.Attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.True(node.Attributes.HasFlag(FileAttributes.Hidden));
            Assert.Equal((ulong)42, node.Size);
            Assert.Equal(changed, node.LastChangeTime);
            Assert.Equal(0x0007_00000000002AUL, node.Frn);
        }

        [Fact]
        public void OrdinaryPathNodesDoNotPayForRareNtfsIdentities()
        {
            Assert.Equal(48, RuntimeObjectSize(typeof(FileNode)));
            Assert.Equal(56, RuntimeObjectSize(typeof(FrnFileNode)));
            Assert.Equal(56, RuntimeObjectSize(typeof(DirectoryFileNode)));
            Assert.Equal(64, RuntimeObjectSize(typeof(FrnDirectoryFileNode)));
            Assert.Equal(24, Unsafe.SizeOf<NodeMetadataSnapshot>());
        }

        [Fact]
        public void RawFilesystemEventsDoNotCarryDeferredSnapshotStorage()
        {
            var raw = new FsEvent(WatcherChangeTypes.Changed, @"C:\raw.test",
                frn: 0x0007_00000000002A, ntfsAttributes: 0x20);
            var node = (INode)new FileNode(raw.FullPath,
                new NodeMetadataSnapshot(false, 1, DateTime.MinValue));
            var metadata = FsEvent.MetadataResult(raw.FullPath, node,
                new NodeMetadataSnapshot(false, 2, DateTime.MinValue), 1);

            Assert.Equal(56, RuntimeObjectSize(raw.GetType()));
            Assert.Equal(96, RuntimeObjectSize(metadata.GetType()));
        }

        [Fact]
        public void FileIdReaderReturnsCurrentMetadataAndRejectsAReusedSequence()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[137]);
                var root = Path.GetPathRoot(path);
                if (!string.Equals(new DriveInfo(root).DriveFormat, "NTFS",
                    StringComparison.OrdinalIgnoreCase))
                    return;

                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                Assert.True(GetFileInformationByHandle(file, out var info));
                var frn = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;

                using var reader = NtfsFileMetadataReader.TryOpen(root);
                Assert.NotNull(reader);
                Assert.True(reader.TryRead(frn, out var metadata));
                Assert.Equal((ulong)137, metadata.Size);
                Assert.Equal(0u, metadata.Attributes & 0x10); //Not a directory

                //Same MFT entry with another sequence is a different identity.
                var reused = frn ^ (1UL << 48);
                Assert.False(reader.TryRead(reused, out _));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION information);

        static long RuntimeObjectSize(Type type)
        {
            _ = RuntimeHelpers.GetUninitializedObject(type);
            const int count = 10_000;
            var instances = new object[count];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < count; i++)
                instances[i] = RuntimeHelpers.GetUninitializedObject(type);
            var size = (GC.GetAllocatedBytesForCurrentThread() - before) / count;
            GC.KeepAlive(instances);
            return size;
        }
    }
}
