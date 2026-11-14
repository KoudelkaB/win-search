#if WINDOWS_HOST
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace search.Smoke
{
    /// <summary>
    /// The Windows clipboard as Explorer sees it: CF_HDROP file lists with the
    /// "Preferred DropEffect" that tells a paste to copy (1) or move (2)
    /// </summary>
    internal static class SystemClipboard
    {
        const uint CF_UNICODETEXT = 13, CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;
        static readonly uint DropEffectFormat = RegisterClipboardFormat("Preferred DropEffect");

        public static string[] Files() => Read(() =>
        {
            var drop = GetClipboardData(CF_HDROP);
            if (drop == IntPtr.Zero) return Array.Empty<string>();
            var count = DragQueryFile(drop, uint.MaxValue, null, 0);
            return Enumerable.Range(0, (int)count).Select(i =>
            {
                var name = new StringBuilder(32768);
                DragQueryFile(drop, (uint)i, name, (uint)name.Capacity);
                return name.ToString();
            }).ToArray();
        });

        /// <summary>1 copy, 2 move, 0 when the clipboard does not say</summary>
        public static int DropEffect() => Read(() =>
        {
            var data = GetClipboardData(DropEffectFormat);
            if (data == IntPtr.Zero) return 0;
            var pointer = GlobalLock(data);
            try { return pointer == IntPtr.Zero ? 0 : Marshal.ReadInt32(pointer); }
            finally { GlobalUnlock(data); }
        });

        public static string Text() => Read(() =>
        {
            var data = GetClipboardData(CF_UNICODETEXT);
            if (data == IntPtr.Zero) return null;
            var pointer = GlobalLock(data);
            try { return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer); }
            finally { GlobalUnlock(data); }
        });

        /// <summary>Files for a paste, as Explorer's Copy puts them</summary>
        public static void SetFiles(IReadOnlyList<string> files)
        {
            //DROPFILES (offset, point, fNC, fWide) and a double-null-terminated UTF-16 list
            var payload = Encoding.Unicode.GetBytes(string.Concat(files.Select(f => f + "\0")) + "\0");
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(20 + payload.Length));
            var pointer = GlobalLock(memory);
            Marshal.WriteInt32(pointer, 0, 20);
            Marshal.WriteInt64(pointer, 4, 0);
            Marshal.WriteInt32(pointer, 12, 0);
            Marshal.WriteInt32(pointer, 16, 1);
            Marshal.Copy(payload, 0, pointer + 20, payload.Length);
            GlobalUnlock(memory);
            Write(() => SetClipboardData(CF_HDROP, memory));
        }

        /// <summary>Put the text back (null leaves the clipboard empty)</summary>
        public static void SetText(string text)
        {
            if (text == null)
            {
                Write(() => IntPtr.Zero);
                return;
            }
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
            GlobalUnlock(memory);
            Write(() => SetClipboardData(CF_UNICODETEXT, memory));
        }

        static T Read<T>(Func<T> read)
        {
            Open();
            try { return read(); }
            finally { CloseClipboard(); }
        }

        static void Write(Func<IntPtr> set)
        {
            Open();
            try
            {
                EmptyClipboard();
                set();
            }
            finally { CloseClipboard(); }
        }

        /// <summary>Another program may hold the clipboard a moment</summary>
        static void Open()
        {
            for (var attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
            {
                if (attempt == 40) throw new IOException("The clipboard stays in use by another program");
                Thread.Sleep(50);
            }
        }

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, uint size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    }
}
#endif
