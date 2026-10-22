using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace search
{
    /// <summary>
    /// Files on the system clipboard, with the "cut" marker every file manager understands:
    /// Windows Explorer's "Preferred DropEffect", GNOME's x-special/gnome-copied-files and
    /// KDE's application/x-kde-cutselection. Pasting in Explorer, Nautilus or Dolphin then
    /// moves instead of copying, and their cut files paste here as a move.
    /// </summary>
    internal static class FileClipboard
    {
        static readonly DataFormat<byte[]> PreferredDropEffect = DataFormat.CreateBytesPlatformFormat("Preferred DropEffect");
        static readonly DataFormat<byte[]> GnomeCopiedFiles = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
        static readonly DataFormat<byte[]> KdeCutSelection = DataFormat.CreateBytesPlatformFormat("application/x-kde-cutselection");

        /// <summary>
        /// The files this process last put on the clipboard. At exit their temporary copies
        /// (extracted archive entries) stay, so another application can still paste them.
        /// </summary>
        public static IReadOnlyList<string> LastSetFiles { get; private set; } = Array.Empty<string>();

        static IClipboard ClipboardOf(TopLevel top) => top?.Clipboard
            ?? throw new InvalidOperationException("The clipboard is not available.");

        public static async Task SetFiles(TopLevel top, IReadOnlyList<string> files, bool cut)
        {
            var storage = top.StorageProvider;
            var transfer = new DataTransfer();
            foreach (var path in files)
            {
                IStorageItem item = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);
                if (item != null) transfer.Add(DataTransferItem.CreateFile(item));
            }
            var marker = new DataTransferItem();
            if (OperatingSystem.IsWindows())
                marker.Set(PreferredDropEffect, BitConverter.GetBytes(cut ? 2 : 1));
            else
            {
                var uris = string.Join("\n", files.Select(f => new Uri(f).AbsoluteUri));
                marker.Set(GnomeCopiedFiles, Encoding.UTF8.GetBytes((cut ? "cut\n" : "copy\n") + uris));
                if (cut) marker.Set(KdeCutSelection, Encoding.ASCII.GetBytes("1"));
            }
            transfer.Add(marker);
            await ClipboardOf(top).SetDataAsync(transfer);
            LastSetFiles = files.ToArray();
            StorageMaintenance.CleanupClipboardMaterializations(App.ClipboardTempFolder, files);
        }

        public static async Task<string[]> GetFiles(TopLevel top)
        {
            var items = await ClipboardOf(top).TryGetFilesAsync();
            if (items == null || items.Length == 0)
            {
                //Some X11 file managers offer only the GNOME list
                if (!OperatingSystem.IsWindows() && await ReadGnomeList(top) is { } gnome) return gnome.Files;
                return Array.Empty<string>();
            }
            return items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }

        /// <summary>True when the clipboard files were cut (pasting them moves)</summary>
        public static async Task<bool> RequestsMove(TopLevel top)
        {
            try
            {
                var clipboard = ClipboardOf(top);
                if (OperatingSystem.IsWindows())
                {
                    var bytes = await clipboard.TryGetValueAsync(PreferredDropEffect);
                    return bytes is { Length: >= sizeof(int) } && BitConverter.ToInt32(bytes, 0) == 2;
                }
                if (await ReadGnomeList(top) is { } gnome) return gnome.Cut;
                var kde = await clipboard.TryGetValueAsync(KdeCutSelection);
                return kde is { Length: > 0 } && kde[0] == (byte)'1';
            }
            catch { return false; }
        }

        static async Task<(bool Cut, string[] Files)?> ReadGnomeList(TopLevel top)
        {
            var bytes = await ClipboardOf(top).TryGetValueAsync(GnomeCopiedFiles);
            if (bytes is not { Length: > 0 }) return null;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;
            var files = lines.Skip(1)
                .Select(l => Uri.TryCreate(l.Trim(), UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
                .Where(p => p != null).ToArray();
            return (lines[0].Trim() == "cut", files);
        }

        public static Task SetText(TopLevel top, string text) => ClipboardOf(top).SetTextAsync(text);

        public static Task<string> GetText(TopLevel top) => ClipboardOf(top).TryGetTextAsync();

        public static Task Clear(TopLevel top) => ClipboardOf(top).ClearAsync();
    }

    /// <summary>
    /// CF_HDROP straight through user32 - used by the headless elevated broker, which has
    /// no UI framework to own a clipboard
    /// </summary>
    internal static class Win32Clipboard
    {
        const uint CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] static extern bool EmptyClipboard();
        [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")] static extern bool CloseClipboard();
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);

        public static void SetFileDropList(this IEnumerable<string> files)
        {
            //DROPFILES header (20 bytes: offset, point, fNC, fWide) + double-null-terminated UTF-16 list
            var list = string.Concat(files.Select(f => f + "\0")) + "\0";
            var payload = Encoding.Unicode.GetBytes(list);
            var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(20 + payload.Length));
            if (memory == IntPtr.Zero) throw new OutOfMemoryException();
            var pointer = GlobalLock(memory);
            Marshal.WriteInt32(pointer, 0, 20);   //pFiles
            Marshal.WriteInt64(pointer, 4, 0);    //pt
            Marshal.WriteInt32(pointer, 12, 0);   //fNC
            Marshal.WriteInt32(pointer, 16, 1);   //fWide
            Marshal.Copy(payload, 0, pointer + 20, payload.Length);
            GlobalUnlock(memory);
            for (var attempt = 0; ; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero)) break;
                if (attempt >= 10)
                {
                    GlobalFree(memory);
                    throw new IOException("The clipboard is in use by another application.");
                }
                Thread.Sleep(50);
            }
            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_HDROP, memory) == IntPtr.Zero) GlobalFree(memory);
            }
            finally { CloseClipboard(); }
        }
    }
}
