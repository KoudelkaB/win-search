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
            //The drive scan's publication: until it happens nothing is indexed for the
            //drive, so the watcher holds unresolved records back instead of reconciling
            watcher.Populate(Array.Empty<INode>());
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

        [Fact]
        public void FileReferenceResolutionSupportsPathsLongerThanTheInitialBuffer()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"usn-long-path-{Guid.NewGuid():N}");
            try
            {
                var deep = directory;
                while (deep.Length < 1200) deep = Path.Combine(deep, new string('d', 100));
                Directory.CreateDirectory(deep);
                var file = Path.Combine(deep, "long.txt");
                File.WriteAllText(file, "long path");
                using var journal = UsnJournal.TryOpen(Path.GetPathRoot(file));
                Assert.NotNull(journal);
                Assert.True(journal.TryGetFileReference(file, out var frn));
                Assert.Equal(file, journal.TryResolvePath(frn));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task DeletedDescendantOfARenamedDirectoryKeepsItsExactPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"usn-moved-tree-{Guid.NewGuid():N}");
            var before = Path.Combine(directory, "before");
            var after = Path.Combine(directory, "after");
            var oldSub = Path.Combine(before, "sub");
            var oldFile = Path.Combine(oldSub, "child.bin");
            var newFile = Path.Combine(after, "sub", "child.bin");
            var indexed = new ConcurrentDictionary<string, INode>(StringComparer.OrdinalIgnoreCase);
            var events = new ConcurrentQueue<FsEvent>();
            var lookup = FSChangeProcessor.Lookup;
            UsnDriveWatcher watcher = null;
            try
            {
                Directory.CreateDirectory(oldSub);
                File.WriteAllBytes(oldFile, new byte[41]);
                var root = Path.GetPathRoot(directory);
                using var journal = UsnJournal.TryOpen(root);
                Assert.NotNull(journal);
                INode Add(string path, INode parent)
                {
                    Assert.True(journal.TryGetFileReference(path, out var frn));
                    Assert.True(INode.TryReadMetadata(path, out var metadata));
                    return indexed[path] = new LiveFrnNode(frn, path, parent, metadata);
                }
                var top = Add(directory, null);
                var moved = Add(before, top);
                var sub = Add(oldSub, moved);
                Add(oldFile, sub);
                var initial = indexed.Values.ToArray();
                var renameQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                FSChangeProcessor.Lookup = path => indexed.TryGetValue(path, out var node) ? node : null;
                watcher = UsnDriveWatcher.TryStart(root, e =>
                {
                    events.Enqueue(e);
                    if (e.ChangeType == WatcherChangeTypes.Renamed && e.OldFullPath == before)
                    {
                        renameQueued.TrySetResult();
                        return Task.Run(async () =>
                        {
                            await Task.Delay(200); //The ordered model applies after translation.
                            foreach (var node in initial.Where(n => n.FullName == before
                                || n.FullName.StartsWith(before + '\\', StringComparison.OrdinalIgnoreCase)))
                            {
                                indexed.TryRemove(node.FullName, out _);
                                var path = after + node.FullName.Substring(before.Length);
                                indexed[path] = new LiveFrnNode(node.Frn, path, null, NodeMetadataSnapshot.From(node));
                            }
                        });
                    }
                    return Task.CompletedTask;
                }, _ => { }, _ => { });
                Assert.NotNull(watcher);
                watcher.Populate(initial);
                Directory.Move(before, after);
                await renameQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                File.Delete(newFile);
                Assert.True(await WaitFor(() => events.Any(e => e.ChangeType == WatcherChangeTypes.Deleted
                    && string.Equals(e.FullPath, newFile, StringComparison.OrdinalIgnoreCase))),
                    $"Missing exact child delete; events: {string.Join("; ", events)}");
            }
            finally
            {
                watcher?.Dispose();
                FSChangeProcessor.Lookup = lookup;
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
                //File.WriteAllBytes truncates and then extends. The journal can expose the
                //intermediate zero length as two exact topology snapshots, or coalesce it
                //into one final snapshot. Either delivery is correct when the parent deltas
                //net to two links * 4096 added bytes.
                long ResizeDelta() => updates.SelectMany(e => e.HardLinkParentDeltas)
                    .Where(d => d.CountDelta == 0
                        && string.Equals(d.ParentPath, dir,
                            StringComparison.OrdinalIgnoreCase))
                    .Sum(d => d.SizeDelta);
                Assert.True(await WaitFor(() => ResizeDelta() == 8192),
                    $"targeted multi-link resize did not net to 8192 (got {ResizeDelta()}); "
                    + $"events: {string.Join("; ", updates.SelectMany(e => e.HardLinkParentDeltas))}");

                while (updates.TryDequeue(out _)) { }
                File.Delete(link);
                Assert.True(await WaitFor(() => updates.Any(e =>
                        e.HardLinkParentDeltas.Any(d =>
                            d.SizeDelta == -8192 && d.CountDelta == -1))),
                    $"no targeted hard-link removal; got: {string.Join("; ", updates)}");

                await Task.Delay(1500);
                Assert.False(rescan.Task.IsCompleted,
                    "targeted hard-link repair unexpectedly requested a drive rescan");
            }
            finally
            {
                watcher.Dispose();
                FSChangeProcessor.Lookup = lookup;
                Directory.Delete(dir, recursive: true);
            }
        }

        /// <summary>
        /// The index keeps one row per file, under the name the scan or the create named
        /// (the canonical name). Deleting exactly that name of a multi-linked file is a
        /// HARD_LINK_CHANGE, not a delete - the file lives on under its other name, so the
        /// row must move there instead of staying a phantom of a path that is gone.
        /// </summary>
        [Fact]
        public async Task DeletingTheCanonicalNameOfAHardLinkedFileMovesItsRowToTheSurvivingName()
        {
            var root = Path.GetPathRoot(Path.GetTempPath());
            var events = new ConcurrentQueue<FsEvent>();
            var rescan = new TaskCompletionSource<DriveScanReason>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var indexed = new ConcurrentDictionary<string, INode>(
                StringComparer.OrdinalIgnoreCase);
            var lookup = FSChangeProcessor.Lookup;
            FSChangeProcessor.Lookup = path =>
                indexed.TryGetValue(path, out var node) ? node : null;
            var watcher = UsnDriveWatcher.TryStart(root, e =>
            {
                events.Enqueue(e);
                //Mirror the model handler for the events this test drives
                if (e.IsHardLinkUpdate)
                {
                    if (indexed.TryGetValue(e.FullPath, out var row)
                        && (e.MetadataNode == null ? row.Frn == e.Frn
                            : ReferenceEquals(row, e.MetadataNode)))
                        row.ApplyMetadata(e.MetadataSnapshot.Value);
                }
                else if (e.ChangeType == WatcherChangeTypes.Created && e.Frn != 0
                    && INode.TryReadMetadata(e.FullPath, out var snapshot))
                {
                    indexed.TryGetValue(Path.GetDirectoryName(e.FullPath) ?? "", out var parent);
                    indexed[e.FullPath] = new LiveFrnNode(e.Frn, e.FullPath, parent, snapshot);
                }
                else if (e.ChangeType == WatcherChangeTypes.Renamed)
                {
                    indexed.TryRemove(e.OldFullPath, out _);
                    if (INode.TryReadMetadata(e.FullPath, out var moved))
                    {
                        indexed.TryGetValue(Path.GetDirectoryName(e.FullPath) ?? "", out var parent);
                        indexed[e.FullPath] = new LiveFrnNode(e.Frn, e.FullPath, parent, moved);
                    }
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

            var dir = Path.Combine(Path.GetTempPath(), $"usn-canonical-{Guid.NewGuid():N}");
            var other = Path.Combine(dir, "other");
            Directory.CreateDirectory(other);
            try
            {
                var file = Path.Combine(dir, "canonical.bin");
                var link = Path.Combine(other, "link.bin");
                File.WriteAllBytes(file, new byte[4096]);
                Assert.True(await WaitFor(() => indexed.ContainsKey(file)),
                    $"the file was not indexed; events: {string.Join("; ", events)}");
                Assert.True(CreateHardLink(link, file, IntPtr.Zero),
                    $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
                Assert.True(await WaitFor(() => events.Any(e => e.IsHardLinkUpdate
                        && e.HardLinkParentDeltas.Any(d => d.CountDelta == 1
                            && string.Equals(d.ParentPath, other, StringComparison.OrdinalIgnoreCase)))),
                    $"the link was not accounted to its directory; events: {string.Join("; ", events)}");

                File.Delete(file); //The indexed name goes, the file stays under other\link.bin
                Assert.True(await WaitFor(() => events.Any(e =>
                        e.ChangeType == WatcherChangeTypes.Renamed
                        && string.Equals(e.OldFullPath, file, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.FullPath, link, StringComparison.OrdinalIgnoreCase))),
                    $"the row did not move to the surviving name; events: {string.Join("; ", events)}");
                Assert.True(await WaitFor(() => !indexed.ContainsKey(file) && indexed.ContainsKey(link)));
                //The rename handler moves the file's own contribution out of dir and into
                //other - which already counted the link. The topology delta that follows
                //the rename must therefore take exactly that one link back out of other
                //(net zero there) and touch dir only through the rename itself.
                Assert.True(await WaitFor(() => events.Any(e => e.IsHardLinkUpdate && e.MetadataNode == null)),
                    $"no topology delta followed the move; events: {string.Join("; ", events)}");
                var afterMove = events.Where(e => e.IsHardLinkUpdate && e.MetadataNode == null).ToArray();
                var delta = Assert.Single(Assert.Single(afterMove).HardLinkParentDeltas);
                Assert.Equal(other, delta.ParentPath, ignoreCase: true);
                Assert.Equal(-1, delta.CountDelta);
                Assert.Equal(-4096, delta.SizeDelta);
                Assert.False(rescan.Task.IsCompleted, "a hard-link change requested a drive rescan");
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
