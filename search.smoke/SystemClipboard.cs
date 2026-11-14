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
        const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_UNICODETEXT = 13, CF_ENHMETAFILE = 14, CF_HDROP = 15,
            GMEM_MOVEABLE = 0x0002;
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

        public static void SetText(string text)
        {
            var memory = Global(Encoding.Unicode.GetBytes(text + "\0"));
            Write(() => SetClipboardData(CF_UNICODETEXT, memory));
        }

        /// <summary>
        /// Everything on the clipboard that can be copied - text in all its forms, file lists,
        /// images as DIB (Windows offers them beside the bitmap), the formats of other programs,
        /// vector pictures as the bits of their enhanced metafile (Windows offers one beside an
        /// old metafile picture). The bitmap, the old metafile and the palette are GDI handles
        /// that Windows makes again from those forms; owner-drawn formats cannot be kept.
        /// </summary>
        public static (uint Format, byte[] Data)[] Snapshot() => Read(() =>
        {
            var formats = new List<(uint, byte[])>();
            for (uint format = 0; (format = EnumClipboardFormats(format)) != 0;)
            {
                if (format is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or (>= 0x80 and <= 0x8E) or (>= 0x300 and <= 0x3FF)) continue;
                var data = GetClipboardData(format);
                if (format == CF_ENHMETAFILE)
                {
                    var length = data == IntPtr.Zero ? 0 : GetEnhMetaFileBits(data, 0, null);
                    if (length == 0) continue;
                    var bits = new byte[length];
                    if (GetEnhMetaFileBits(data, length, bits) == length) formats.Add((format, bits));
                    continue;
                }
                var size = data == IntPtr.Zero ? 0 : (long)GlobalSize(data);
                if (size == 0) continue;
                var pointer = GlobalLock(data);
                if (pointer == IntPtr.Zero) continue;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, bytes.Length);
                    formats.Add((format, bytes));
                }
                finally { GlobalUnlock(data); }
            }
            return formats.ToArray();
        });

        /// <summary>Put a snapshot back (an empty one leaves the clipboard empty)</summary>
        public static void Restore((uint Format, byte[] Data)[] snapshot) => Write(() =>
        {
            //The clipboard owns what it is given: the metafile as well as the memory
            foreach (var (format, data) in snapshot)
                SetClipboardData(format, format == CF_ENHMETAFILE ? SetEnhMetaFileBits((uint)data.Length, data) : Global(data));
            return IntPtr.Zero;
        });

        static IntPtr Global(byte[] bytes)
        {
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, bytes.Length));
            Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
            GlobalUnlock(memory);
            return memory;
        }

        static T Read<T>(Func<T> read)
        {
            Open(IntPtr.Zero);
            try { return read(); }
            finally { CloseClipboard(); }
        }

        /// <summary>
        /// A write needs an owner window: EmptyClipboard of a clipboard opened without one
        /// leaves no owner, and SetClipboardData then may fail. A hidden message-only window
        /// owns it for the write; the data stays when the window goes.
        /// </summary>
        static void Write(Func<IntPtr> set)
        {
            var owner = CreateWindowEx(0, "STATIC", "search.smoke clipboard", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (owner == IntPtr.Zero) throw new IOException($"No window for the clipboard (error {Marshal.GetLastWin32Error()})");
            try
            {
                Open(owner);
                try
                {
                    EmptyClipboard();
                    set();
                }
                finally { CloseClipboard(); }
            }
            finally { DestroyWindow(owner); }
        }

        /// <summary>Another program may hold the clipboard a moment</summary>
        static void Open(IntPtr owner)
        {
            for (var attempt = 0; !OpenClipboard(owner); attempt++)
            {
                if (attempt == 40) throw new IOException("The clipboard stays in use by another program");
                Thread.Sleep(50);
            }
        }

        static readonly IntPtr HWND_MESSAGE = new(-3);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, uint size);
        [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr metafile, uint size, byte[] data);
        [DllImport("gdi32.dll")] static extern IntPtr SetEnhMetaFileBits(uint size, byte[] data);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    }
}
#endif
