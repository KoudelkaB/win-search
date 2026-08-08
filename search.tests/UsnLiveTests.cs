using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using search.Core;
using search.Models;
using Xunit;

namespace search.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class LiveNtfsCollection
    {
        public const string Name = "Live NTFS journal";
    }

    /// <summary>
    /// Against the real USN journal of C: - runs unelevated (the FSCTLs work through the
    /// root-directory handle + FSCTL_READ_UNPRIVILEGED_USN_JOURNAL). Skips itself cleanly
    /// on volumes without a readable journal.
    /// </summary>
    [Collection(LiveNtfsCollection.Name)]
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

        sealed class LiveFrnNode : INode
        {
            readonly ulong frn;
            readonly string path;
            readonly INode parent;

            public LiveFrnNode(ulong frn, string path, INode parent,
                NodeMetadataSnapshot snapshot)
            {
                this.frn = frn;
                this.path = path;
                this.parent = parent;
                Attributes = snapshot.Attributes;
                Size = snapshot.Size;
                LastChangeTime = snapshot.LastChangeTime;
            }

            public override ulong Frn => frn;
            public override string FullName => path;
            public override string Name => Path.GetFileName(path);
            public override INode PathParent => parent;
            public override FileAttributes Attributes { get; protected set; }
            public override ulong Size { get; protected set; }
            public override DateTime LastChangeTime { get; protected set; }
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
            var dir = Path.Combine(Path.GetTempPath(), $"usn-live-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            //Written before the journal is positioned, so no record ever puts this file's
            //reference into the map: its delete is the genuinely unresolvable one that has
            //to fall back to reconciling the parent directory.
            var unknown = Path.Combine(dir, "unknown.txt");
            File.WriteAllText(unknown, "x");
            var watcher = UsnDriveWatcher.TryStart(root,
                e => { events.Enqueue(e); return Task.CompletedTask; }, _ => { }, _ => { });
            if (watcher == null)
            {
                //An NTFS system volume always has a journal - failing to open it there is a bug
                Assert.False(string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                    $"USN journal failed to open on NTFS volume {root}");
                return; //No readable journal on this volume - nothing to test
            }
            try
            {
                try
                {
                    var file = Path.Combine(dir, "created.txt");
                    File.WriteAllText(file, "x");
                    Assert.True(await WaitFor(() => events.Any(e =>
                            e.ChangeType == WatcherChangeTypes.Created && string.Equals(e.FullPath, file, StringComparison.OrdinalIgnoreCase))),
                        $"no Created event for {file}; got: {string.Join("; ", events)}");

                    //A rename resolves its new path by file id even though the journal record
                    //has no name. The old path comes from the map entry this watcher wrote for
                    //its own Created - the index apply is asynchronous and must not be required.
                    var renamed = Path.Combine(dir, "renamed.txt");
                    File.Move(file, renamed);
                    Assert.True(await WaitFor(() => events.Any(e =>
                            e.ChangeType == WatcherChangeTypes.Renamed
                            && string.Equals(e.FullPath, renamed, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(e.OldFullPath, file, StringComparison.OrdinalIgnoreCase))),
                        $"no Renamed event {file} -> {renamed}; got: {string.Join("; ", events)}");

                    //The watcher named this one itself, so its delete is exact - no reconcile
                    File.Delete(renamed);
                    Assert.True(await WaitFor(() => events.Any(e =>
                            e.ChangeType == WatcherChangeTypes.Deleted
                            && string.Equals(e.FullPath, renamed, StringComparison.OrdinalIgnoreCase))),
                        $"no exact Deleted event for {renamed}; got: {string.Join("; ", events)}");

                    //A delete of a file the map does not know cannot be named (unprivileged
                    //records are nameless) - the watcher must reconcile the parent directory
                    File.Delete(unknown);
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
        public async Task JournalReportsTheExactPathWhenAnIndexedFileIsDeleted()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            var events = new ConcurrentQueue<FsEvent>();
            var indexed = new ConcurrentDictionary<string, INode>(StringComparer.OrdinalIgnoreCase);
            var lookup = FSChangeProcessor.Lookup;
            var target = "";
            UsnDriveWatcher watcher = null;
            var dir = Path.Combine(Path.GetTempPath(), $"usn-indexed-delete-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                FSChangeProcessor.Lookup = path => indexed.TryGetValue(path, out var node) ? node : null;
                watcher = UsnDriveWatcher.TryStart(root, e =>
                {
                    events.Enqueue(e);
                    //Mirror the application handler synchronously. UsnDriveWatcher remaps
                    //the FRN after this callback, so the following delete must resolve from
                    //the same live index entry rather than fall back to a directory reconcile.
                    if (e.ChangeType == WatcherChangeTypes.Created
                        && string.Equals(e.FullPath, target, StringComparison.OrdinalIgnoreCase))
                        indexed[e.FullPath] = new FileNode(e.FullPath);
                    return Task.CompletedTask;
                }, _ => { }, _ => { });
                if (watcher == null)
                {
                    Assert.False(string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                        $"USN journal failed to open on NTFS volume {root}");
                    return;
                }

                target = Path.Combine(dir, "single.bin");
                File.WriteAllBytes(target, new byte[4096]);
                Assert.True(await WaitFor(() => indexed.ContainsKey(target)),
                    $"the created file was not indexed; got: {string.Join("; ", events)}");

                File.Delete(target);
                Assert.True(await WaitFor(() => events.Any(e =>
                        e.ChangeType == WatcherChangeTypes.Deleted
                        && string.Equals(e.FullPath, target, StringComparison.OrdinalIgnoreCase))),
                    $"no exact Deleted event for {target}; got: {string.Join("; ", events)}");
            }
            finally
            {
                watcher?.Dispose();
                FSChangeProcessor.Lookup = lookup;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// "Write foo.tmp, rename it over foo" is how editors, compilers and CLI tools save
        /// a file. Both records normally land in ONE journal read batch, so the temp file's
        /// Created is still waiting in the ordered drive queue when the rename is translated
        /// - the index cannot confirm the old path yet. The watcher must still report the
        /// rename; otherwise the queued create lands afterwards and the temp name stays in
        /// the grid forever, for a file that no longer exists under that name.
        /// </summary>
        [Fact]
        public async Task AtomicSaveRenameSurvivesAnIndexApplyThatLagsBehindTheJournal()
        {
            const int saves = 10;
            var root = Path.GetPathRoot(Path.GetTempPath());
            var events = new ConcurrentQueue<FsEvent>();
            var indexed = new ConcurrentDictionary<string, INode>(StringComparer.OrdinalIgnoreCase);
            var lookup = FSChangeProcessor.Lookup;
            var dir = Path.Combine(Path.GetTempPath(), $"usn-atomic-save-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            UsnDriveWatcher watcher = null;
            //The real drive queue is FIFO with a single consumer and coalesces for 200-500 ms
            //before applying anything. Model exactly that: ordered, but never synchronous.
            var applyGate = new object();
            var apply = Task.CompletedTask;
            try
            {
                FSChangeProcessor.Lookup = path => indexed.TryGetValue(path, out var node) ? node : null;
                watcher = UsnDriveWatcher.TryStart(root, e =>
                {
                    events.Enqueue(e);
                    lock (applyGate)
                        return apply = apply.ContinueWith(previous =>
                        {
                            Thread.Sleep(50); //The coalescing window the index apply waits out
                            switch (e.ChangeType)
                            {
                                case WatcherChangeTypes.Created:
                                    indexed[e.FullPath] = new FileNode(e.FullPath);
                                    break;
                                case WatcherChangeTypes.Renamed:
                                    indexed.TryRemove(e.OldFullPath, out _);
                                    indexed[e.FullPath] = new FileNode(e.FullPath);
                                    break;
                                case WatcherChangeTypes.Deleted:
                                    indexed.TryRemove(e.FullPath, out _);
                                    break;
                            }
                        }, TaskScheduler.Default);
                }, _ => { }, _ => { });
                if (watcher == null)
                {
                    Assert.False(string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
                        $"USN journal failed to open on NTFS volume {root}");
                    return;
                }

                var targets = new string[saves];
                var temps = new string[saves];
                for (var i = 0; i < saves; i++)
                {
                    targets[i] = Path.Combine(dir, $"page{i}.html");
                    temps[i] = Path.Combine(dir, $"page{i}.html.tmp.{Environment.ProcessId}.{i:x8}");
                    File.WriteAllText(temps[i], "<html/>");
                    File.Move(temps[i], targets[i], overwrite: true);
                }

                Assert.True(await WaitFor(() => targets.All(indexed.ContainsKey)),
                    $"saved files never reached the index; indexed: {string.Join("; ", indexed.Keys)}"
                    + $"; events: {string.Join("; ", events)}");
                //Nothing may remain indexed under a name that is not on disk any more
                Assert.True(await WaitFor(() => !temps.Any(indexed.ContainsKey)),
                    $"temp names of an atomic save stayed indexed: "
                    + $"{string.Join("; ", temps.Where(indexed.ContainsKey))}"
                    + $"; events: {string.Join("; ", events)}");
            }
            finally
            {
                watcher?.Dispose();
                FSChangeProcessor.Lookup = lookup;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public async Task HardLinkJournalChangeIsRepairedWithoutAnExactMftRescan()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            var updates = new ConcurrentQueue<FsEvent>();
            var rescan = new TaskCompletionSource<DriveScanReason>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var indexed = new ConcurrentDictionary<string, INode>(
                StringComparer.OrdinalIgnoreCase);
            var lookup = FSChangeProcessor.Lookup;
            FSChangeProcessor.Lookup = path =>
                indexed.TryGetValue(path, out var node) ? node : null;
            var watcher = UsnDriveWatcher.TryStart(root, e =>
            {
                if (e.IsHardLinkUpdate)
                {
                    e.MetadataNode.ApplyMetadata(e.MetadataSnapshot.Value);
                    updates.Enqueue(e);
                }
                else if (e.ChangeType == WatcherChangeTypes.Created && e.Frn != 0
                    && INode.TryReadMetadata(e.FullPath, out var snapshot))
                {
                    indexed.TryGetValue(Path.GetDirectoryName(e.FullPath) ?? "",
                        out var parent);
                    indexed[e.FullPath] =
                        new LiveFrnNode(e.Frn, e.FullPath, parent, snapshot);
                }
                return Task.CompletedTask;
            }, reason => rescan.TrySetResult(reason), _ => { });
            if (watcher == null)
            {
                FSChangeProcessor.Lookup = lookup;
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
                Assert.True(await WaitFor(() => indexed.ContainsKey(file)),
                    $"the source file was not mapped by FRN; indexed: {string.Join("; ", indexed.Keys)}");
                Assert.True(CreateHardLink(link, file, IntPtr.Zero),
                    $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

                Assert.True(await WaitFor(() => updates.Any(e =>
                        e.HardLinkParentDeltas.Any(d =>
                            d.SizeDelta == 4096 && d.CountDelta == 1))),
                    $"no targeted hard-link addition; got: {string.Join("; ", updates)}");
                var hardLinkUpdate = updates.First(e => e.HardLinkParentDeltas.Any(d =>
                    d.SizeDelta == 4096 && d.CountDelta == 1));
                Assert.Equal(file, hardLinkUpdate.FullPath, ignoreCase: true);
                var delta = Assert.Single(hardLinkUpdate.HardLinkParentDeltas);
                Assert.Equal(dir, delta.ParentPath, ignoreCase: true);
                Assert.Equal(4096, delta.SizeDelta);
                Assert.Equal(1, delta.CountDelta);

                while (updates.TryDequeue(out _)) { }
                File.WriteAllBytes(file, new byte[8192]);
                Assert.True(await WaitFor(() => updates.Any(e =>
                        e.HardLinkParentDeltas.Any(d =>
                            d.SizeDelta == 8192 && d.CountDelta == 0))),
                    $"no targeted multi-link resize; got: {string.Join("; ", updates)}");

                while (updates.TryDequeue(out _)) { }
                File.Delete(link);
                Assert.True(await WaitFor(() => updates.Any(e =>
                        e.HardLinkParentDeltas.Any(d =>
                            d.SizeDelta == -8192 && d.CountDelta == -1))),
                    $"no targeted hard-link removal; got: {string.Join("; ", updates)}");

                await Task.Delay(UsnDriveWatcher.ExactRescanQuietMs + 500);
                Assert.False(rescan.Task.IsCompleted,
                    "targeted hard-link repair unexpectedly requested an exact MFT rescan");
            }
            finally
            {
                watcher.Dispose();
                FSChangeProcessor.Lookup = lookup;
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
