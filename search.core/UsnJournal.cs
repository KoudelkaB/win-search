using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace search.Core
{
    /// <summary>
    /// One change record from the NTFS USN change journal. The unprivileged read
    /// (FSCTL_READ_UNPRIVILEGED_USN_JOURNAL) blanks every file name, so consumers must
    /// resolve paths through the file reference numbers: <see cref="UsnJournal.TryResolvePath"/>
    /// for a file that still exists, an externally maintained FRN map for one that does not.
    /// </summary>
    public readonly record struct UsnRecord(ulong Frn, ulong ParentFrn, uint Reason, uint Attributes, string Name);

    /// <summary>
    /// Reader of the NTFS USN change journal of one volume. Unlike ReadDirectoryChangesW the
    /// journal is kernel-persisted on the volume - it has no in-process buffer to overflow, so
    /// changes are never silently dropped (only a wrapped/recreated journal loses history, which
    /// <see cref="ReadBatch"/> reports so the caller can rescan the drive).
    /// Works unelevated: the FSCTLs are issued on a handle to the volume's ROOT DIRECTORY
    /// (a 0/attributes-only volume handle fails them with ERROR_INVALID_FUNCTION), falling back
    /// from the privileged read to FSCTL_READ_UNPRIVILEGED_USN_JOURNAL on access denied.
    /// </summary>
    public sealed class UsnJournal : IDisposable
    {
        // Reasons
        public const uint ReasonDataOverwrite = 0x00000001;
        public const uint ReasonDataExtend = 0x00000002;
        public const uint ReasonDataTruncation = 0x00000004;
        public const uint ReasonFileCreate = 0x00000100;
        public const uint ReasonFileDelete = 0x00000200;
        public const uint ReasonRenameOldName = 0x00001000;
        public const uint ReasonRenameNewName = 0x00002000;
        public const uint ReasonBasicInfoChange = 0x00008000;
        public const uint ReasonHardLinkChange = 0x00010000;
        public const uint ReasonCompressionChange = 0x00020000;
        public const uint ReasonEncryptionChange = 0x00040000;

        /// <summary>Everything the index cares about - contents, existence, names, attributes</summary>
        const uint ReasonMask = ReasonDataOverwrite | ReasonDataExtend | ReasonDataTruncation
            | ReasonFileCreate | ReasonFileDelete | ReasonRenameOldName | ReasonRenameNewName
            | ReasonBasicInfoChange | ReasonHardLinkChange | ReasonCompressionChange | ReasonEncryptionChange;

        const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
        const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
        const uint FSCTL_READ_UNPRIVILEGED_USN_JOURNAL = 0x000903ab;
        const uint FSCTL_CREATE_USN_JOURNAL = 0x000900e7;
        //Keep live delivery enabled. Return-only-on-close can suppress intermediate
        //records, but a long-open writer would then violate the sub-second grid target.
        internal const uint ReturnOnlyOnClose = 0;

        const int ERROR_INVALID_FUNCTION = 1;
        const int ERROR_ACCESS_DENIED = 5;
        const int ERROR_NOT_SUPPORTED = 50;
        const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
        const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

        readonly SafeFileHandle root; //Root-directory handle - FSCTL target and OpenFileById hint
        readonly byte[] readInput = new byte[40];   //READ_USN_JOURNAL_DATA_V0
        readonly byte[] buffer = new byte[1 << 18]; //One read batch of records
        ulong journalId;
        long position;
        bool unprivileged; //FSCTL_READ_USN_JOURNAL denied => use the unprivileged variant

        public string Root { get; }

        UsnJournal(string rootPath, SafeFileHandle handle) { Root = rootPath; root = handle; }

        /// <summary>
        /// Open the journal of the volume mounted at e.g. "C:\" and position at its current
        /// end. Returns null when the volume has no readable journal (non-NTFS, no journal
        /// and no rights to create one, FSCTL unsupported) - the caller then falls back to
        /// FileSystemWatcher.
        /// Call before starting the drive scan: every change from this moment on is replayed,
        /// so nothing can fall between the scan snapshot and the first read.
        /// </summary>
        public static UsnJournal TryOpen(string driveRoot)
        {
            //The FSCTLs' accepted handle differs between systems/rights: unelevated Win10
            //rejects them on a volume handle (ERROR_INVALID_FUNCTION) but accepts them on the
            //volume's root DIRECTORY; classic (elevated) usage documents the volume handle.
            //Try both - whichever can answer the query is used for everything.
            foreach (var path in new[] { driveRoot, @"\\.\" + driveRoot.TrimEnd('\\') })
            {
                var handle = CreateFile(path, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                var journal = new UsnJournal(driveRoot, handle);
                if (journal.Query(out journal.journalId, out journal.position)) return journal;
                //An NTFS volume can simply have no journal (common on secondary data disks).
                //An elevated process may create it; unelevated the attempt is denied => watcher.
                if (Marshal.GetLastWin32Error() == ERROR_JOURNAL_NOT_ACTIVE
                    && TryCreateJournal(driveRoot)
                    && journal.Query(out journal.journalId, out journal.position)) return journal;
                journal.Dispose();
            }
            return null;
        }

        /// <summary>
        /// FSCTL_CREATE_USN_JOURNAL - needs write access to the volume (elevation)
        /// </summary>
        static bool TryCreateJournal(string driveRoot)
        {
            using var volume = CreateFile(@"\\.\" + driveRoot.TrimEnd('\\'), GENERIC_READ | GENERIC_WRITE,
                SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (volume.IsInvalid) return false;
            var create = new byte[16]; //CREATE_USN_JOURNAL_DATA
            BitConverter.GetBytes(32UL << 20).CopyTo(create, 0); //MaximumSize 32 MB (the OS default)
            BitConverter.GetBytes(8UL << 20).CopyTo(create, 8);  //AllocationDelta 8 MB
            return DeviceIoControl(volume, FSCTL_CREATE_USN_JOURNAL, create, create.Length, null, 0, out _, IntPtr.Zero);
        }

        bool Query(out ulong id, out long nextUsn)
        {
            var data = new byte[80]; //USN_JOURNAL_DATA_V0/V1/V2 all fit
            id = 0; nextUsn = 0;
            if (!DeviceIoControl(root, FSCTL_QUERY_USN_JOURNAL, null, 0, data, data.Length, out _, IntPtr.Zero))
                return false;
            id = BitConverter.ToUInt64(data, 0);
            nextUsn = BitConverter.ToInt64(data, 16);
            return true;
        }

        /// <summary>
        /// Read the next batch of records, blocking until at least one is available (falling
        /// back to a short poll when the volume rejects a waiting read). An empty list with
        /// journalInvalid=false is a benign timeout - just call again.
        /// journalInvalid=true means history was lost (journal wrapped, deleted or recreated):
        /// the journal has re-positioned at the current end and the caller must rescan the drive.
        /// Returns null when the journal cannot be read on this volume/system at all (volume
        /// removed, FSCTL unsupported, access denied even to the unprivileged read, or errors
        /// that persist across resyncs) - the caller must stop and fall back to a watcher.
        /// </summary>
        public List<UsnRecord> ReadBatch(out bool journalInvalid)
        {
            journalInvalid = false;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                BitConverter.GetBytes(position).CopyTo(readInput, 0);       //StartUsn
                BitConverter.GetBytes(ReasonMask).CopyTo(readInput, 8);     //ReasonMask
                BitConverter.GetBytes(ReturnOnlyOnClose).CopyTo(readInput, 12);
                //Finite wait, never 0=forever: Dispose of a handle with a blocked FSCTL on it
                //waits for that FSCTL, so the reader must wake periodically to notice a stop
                BitConverter.GetBytes(2UL).CopyTo(readInput, 16);           //Timeout
                BitConverter.GetBytes(1UL).CopyTo(readInput, 24);           //BytesToWaitFor (>0 = wait)
                BitConverter.GetBytes(journalId).CopyTo(readInput, 32);     //UsnJournalID

                var code = unprivileged ? FSCTL_READ_UNPRIVILEGED_USN_JOURNAL : FSCTL_READ_USN_JOURNAL;
                if (DeviceIoControl(root, code, readInput, readInput.Length, buffer, buffer.Length, out var got, IntPtr.Zero))
                {
                    consecutiveResyncs = 0;
                    if (got < sizeof(long)) return new List<UsnRecord>();
                    position = BitConverter.ToInt64(buffer, 0);
                    return Parse(buffer, got);
                }

                switch (Marshal.GetLastWin32Error())
                {
                    case ERROR_ACCESS_DENIED when !unprivileged:      //Unelevated process, or a
                    case ERROR_INVALID_FUNCTION when !unprivileged:   //handle/system where only the
                    case ERROR_NOT_SUPPORTED when !unprivileged:      //access-checked read dispatches
                        unprivileged = true;
                        continue;
                    case ERROR_ACCESS_DENIED:      //Even the unprivileged read is denied
                    case ERROR_INVALID_FUNCTION:   //FSCTL not supported here (pre-1709 Windows,
                    case ERROR_NOT_SUPPORTED:      //filter drivers, exotic volumes)
                        return null;
                    case ERROR_JOURNAL_DELETE_IN_PROGRESS:
                        Thread.Sleep(1000); //Deletion takes a moment - resync would fail too
                        goto default;
                    default:
                        //Journal wrapped past our position, deleted, recreated with a new id...
                        //=> resync to the live journal end and tell the caller to rescan. A
                        //volume where resyncs never lead to a working read (query keeps failing
                        //or every read errors again) is unreadable => stop, fall back to watcher.
                        journalInvalid = true;
                        return ++consecutiveResyncs <= 3 && Query(out journalId, out position)
                            ? new List<UsnRecord>() : null;
                }
            }
            journalInvalid = true;
            return ++consecutiveResyncs <= 3 && Query(out journalId, out position) ? new List<UsnRecord>() : null;
        }

        int consecutiveResyncs; //Resyncs since the last successful read - a cap turns error loops into a fallback

        /// <summary>
        /// Parse USN_RECORD_V2 entries from a read batch (first 8 bytes are the next USN).
        /// Internal for tests.
        /// </summary>
        internal static List<UsnRecord> Parse(byte[] data, int length)
        {
            var records = new List<UsnRecord>();
            var offset = sizeof(long);
            while (offset + 60 <= length)
            {
                var recordLength = BitConverter.ToInt32(data, offset);
                if (recordLength < 60 || offset + recordLength > length) break;
                var major = BitConverter.ToUInt16(data, offset + 4);
                if (major == 2) //V3/V4 (128-bit ReFS refs, range tracking) are never requested
                {
                    var nameLength = BitConverter.ToUInt16(data, offset + 56);
                    var nameOffset = BitConverter.ToUInt16(data, offset + 58);
                    var name = nameLength > 0 && offset + nameOffset + nameLength <= length
                        ? Encoding.Unicode.GetString(data, offset + nameOffset, nameLength)
                        : ""; //The unprivileged read blanks all names
                    records.Add(new UsnRecord(
                        BitConverter.ToUInt64(data, offset + 8),   //FileReferenceNumber
                        BitConverter.ToUInt64(data, offset + 16),  //ParentFileReferenceNumber
                        BitConverter.ToUInt32(data, offset + 40),  //Reason
                        BitConverter.ToUInt32(data, offset + 52),  //FileAttributes
                        name));
                }
                offset += recordLength;
            }
            return records;
        }

        /// <summary>
        /// Current full path of a file/directory that still exists on the volume, by its file
        /// reference number (works unelevated for anything the user may access). Null when the
        /// file is gone or inaccessible - deleted files must resolve through the FRN map instead.
        /// </summary>
        public string TryResolvePath(ulong frn)
        {
            var id = new FILE_ID_DESCRIPTOR { dwSize = Marshal.SizeOf<FILE_ID_DESCRIPTOR>(), Type = 0, FileId = (long)frn };
            using var handle = OpenFileById(root, ref id, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
            if (handle.IsInvalid) return null;
            var path = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length > path.Capacity) return null;
            var result = path.ToString();
            //GetFinalPathNameByHandle returns the \\?\ form
            return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result.Substring(4) : result;
        }

        public void Dispose() => root.Dispose();

        #region interop
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint SHARE_ALL = 0x7; //read | write | delete
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        struct FILE_ID_DESCRIPTOR { public int dwSize; public int Type; public long FileId; long pad; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern SafeFileHandle OpenFileById(SafeFileHandle hVolumeHint, ref FILE_ID_DESCRIPTOR lpFileId,
            uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
        #endregion
    }
}
