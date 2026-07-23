using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace search.Core
{
    /// <summary>
    /// Current file metadata returned by NTFS for an open file-reference handle.
    /// LastWriteFileTimeUtc is a raw FILETIME so it can cross the service/broker
    /// protocols without time-zone or DateTime-kind conversions.
    /// </summary>
    public readonly record struct NtfsFileMetadata(
        uint Attributes, ulong Size, long LastWriteFileTimeUtc);

    /// <summary>
    /// Opens files by their NTFS file reference number and queries their current
    /// metadata through the file system. Unlike a raw $MFT record read this sees
    /// the file system's current view, reports the exact EOF for files whose $DATA
    /// lives in an extension record, and can use backup privilege for ACL-protected
    /// entries when hosted by the service/elevated broker.
    /// </summary>
    public sealed class NtfsFileMetadataReader : IDisposable
    {
        readonly SafeFileHandle volumeHint;

        NtfsFileMetadataReader(SafeFileHandle volumeHint) => this.volumeHint = volumeHint;

        /// <summary>
        /// Open a reusable root-directory handle for a volume mount point such as C:\.
        /// Returns null for unsupported/unavailable volumes.
        /// </summary>
        public static NtfsFileMetadataReader TryOpen(string volumeMountPoint)
        {
            if (string.IsNullOrWhiteSpace(volumeMountPoint)) return null;
            TryEnableBackupPrivilege();
            try
            {
                var handle = CreateFile(volumeMountPoint, FILE_READ_ATTRIBUTES, SHARE_ALL,
                    IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }
                return new NtfsFileMetadataReader(handle);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Query one exact NTFS file reference. OpenFileById validates the embedded
        /// sequence number, so a freed-and-reused MFT entry does not return metadata
        /// for the replacement file.
        /// </summary>
        public bool TryRead(ulong frn, out NtfsFileMetadata metadata)
        {
            metadata = default;
            if (frn == 0 || volumeHint.IsInvalid || volumeHint.IsClosed) return false;
            try
            {
                var id = new FILE_ID_DESCRIPTOR
                {
                    dwSize = Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                    Type = FileIdType,
                    FileId = unchecked((long)frn)
                };
                using var file = OpenFileById(volumeHint, ref id, FILE_READ_ATTRIBUTES,
                    SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
                if (file.IsInvalid || !GetFileInformationByHandle(file, out var info))
                    return false;

                var size = ((ulong)info.FileSizeHigh << 32) | info.FileSizeLow;
                var fileTime = ((long)info.LastWriteTimeHigh << 32)
                    | info.LastWriteTimeLow;
                metadata = new NtfsFileMetadata(info.FileAttributes,
                    (info.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? 0UL : size,
                    fileTime);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose() => volumeHint.Dispose();

        static int privilegeAttempted;

        /// <summary>
        /// The LocalSystem service and elevated broker carry SeBackupPrivilege but it
        /// is commonly disabled in their token. Enable it once per process. Unelevated
        /// callers simply fail this best-effort step and retain normal ACL semantics.
        /// </summary>
        static void TryEnableBackupPrivilege()
        {
            if (System.Threading.Interlocked.Exchange(ref privilegeAttempted, 1) != 0) return;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
                    return;
                using (token)
                {
                    if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid))
                        return;
                    var privileges = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    };
                    AdjustTokenPrivileges(token, false, ref privileges, 0,
                        IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        }

        const int FileIdType = 0;
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint SHARE_ALL = 0x7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        const uint TOKEN_QUERY = 0x8;
        const uint SE_PRIVILEGE_ENABLED = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_ID_DESCRIPTOR
        {
            public int dwSize;
            public int Type;
            public long FileId;
            public long ExtendedFileIdPadding;
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

        [StructLayout(LayoutKind.Sequential)]
        struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern SafeFileHandle OpenFileById(SafeFileHandle hVolumeHint,
            ref FILE_ID_DESCRIPTOR lpFileId, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess,
            out SafeFileHandle tokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool LookupPrivilegeValue(string systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(SafeFileHandle tokenHandle,
            bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, int bufferLength,
            IntPtr previousState, IntPtr returnLength);
    }
}
