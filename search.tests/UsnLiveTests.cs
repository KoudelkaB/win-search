using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using search.Core;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Against the real USN journal of C: - runs unelevated (the FSCTLs work through the
    /// root-directory handle + FSCTL_READ_UNPRIVILEGED_USN_JOURNAL). Skips itself cleanly
    /// on volumes without a readable journal.
    /// </summary>
    public class UsnLiveTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 15000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                await Task.Delay(100);
            }
            return condition();
        }

        [Fact]
        public async Task JournalReportsCreateRenameAndUnresolvedDeleteFallsBackToParentReconcile()
        {
            var root = Path.GetPathRoot(Path.GetTempPath()); //C:\ in practice
            var events = new ConcurrentQueue<FsEvent>();
            var reconciled = new ConcurrentQueue<string>();

            var lookup = FSChangeProcessor.Lookup;
            var reconcile = FSChangeProcessor.ReconcileDirs;
            FSChangeProcessor.Lookup = _ => null; //Empty index - deletes cannot resolve through the map
            FSChangeProcessor.ReconcileDirs = dirs => { foreach (var d in dirs) reconciled.Enqueue(d); return Task.CompletedTask; };
            var watcher = UsnDriveWatcher.TryStart(root, e => { events.Enqueue(e); return Task.CompletedTask; }, () => { }, _ => { });
            if (watcher == null)
            {
                //An NTFS system volume always has a journal - failing to open it there is a bug
                Assert.False(string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    $"USN journal failed to open on NTFS volume {root}");
                return; //No readable journal on this volume - nothing to test
            }
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), $"usn-live-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    var file = Path.Combine(dir, "created.txt");
                    File.WriteAllText(file, "x");
                    Assert.True(await WaitFor(() => events.Any(e =>
                            e.ChangeType == WatcherChangeTypes.Created && string.Equals(e.FullPath, file, StringComparison.OrdinalIgnoreCase))),
                        $"no Created event for {file}; got: {string.Join("; ", events)}");

                    //A rename resolves its new path by file id even though the journal record has no name.
                    //The old path comes from the FRN map, which is empty here => reported as Created(new).
                    var renamed = Path.Combine(dir, "renamed.txt");
                    File.Move(file, renamed);
                    Assert.True(await WaitFor(() => events.Any(e =>
                            (e.ChangeType == WatcherChangeTypes.Renamed || e.ChangeType == WatcherChangeTypes.Created)
                            && string.Equals(e.FullPath, renamed, StringComparison.OrdinalIgnoreCase))),
                        $"no Renamed/Created event for {renamed}; got: {string.Join("; ", events)}");

                    //A delete of a file the map does not know cannot be named (unprivileged
                    //records are nameless) - the watcher must reconcile the parent directory
                    File.Delete(renamed);
                    Assert.True(await WaitFor(() => reconciled.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))),
                        $"parent {dir} was not reconciled; reconciled: {string.Join("; ", reconciled)}; events: {string.Join("; ", events)}");
                }
                finally
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            finally
            {
                watcher.Dispose();
                FSChangeProcessor.Lookup = lookup;
                FSChangeProcessor.ReconcileDirs = reconcile;
            }
        }

        [Fact]
        public async Task HardLinkJournalChangeSchedulesAnExactMftRescan()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = UsnDriveWatcher.TryStart(root, _ => Task.CompletedTask,
                () => requested.TrySetResult(), _ => { });
            if (watcher == null)
            {
                Assert.False(string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    $"USN journal failed to open on NTFS volume {root}");
                return;
            }

            var dir = Path.Combine(Path.GetTempPath(), $"usn-hardlink-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "source.bin");
                var link = Path.Combine(dir, "link.bin");
                File.WriteAllBytes(file, new byte[4096]);
                Assert.True(CreateHardLink(link, file, IntPtr.Zero),
                    $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

                var completed = await Task.WhenAny(requested.Task, Task.Delay(15_000));
                Assert.Same(requested.Task, completed);
            }
            finally
            {
                watcher.Dispose();
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
