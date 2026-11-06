using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace search.Models
{
    /// <summary>
    /// Zero-privilege fallback enumeration used when no MFT source is available
    /// (and for ready non-NTFS drives). Parallel BFS over plain file APIs; unreadable
    /// directories are skipped and reparse points are not descended into
    /// (junction cycles, OneDrive placeholders). On Linux each directory is listed with
    /// getdents64 + statx (LinuxDirectoryReader), elsewhere with the managed enumeration.
    /// </summary>
    internal static class DirectoryWalker
    {
        // Give up on a walk that has not produced a single new entry for this long. A live
        // drive keeps making progress so it walks to completion however large it is; a dead
        // or hung one (e.g. a disconnected network share whose EnumerateFileSystemInfos blocks
        // in an uninterruptible syscall) stalls and is abandoned so it can never wedge the load.
        static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

        // A background rescan (nobody waits for it) keeps to a few threads: on a warm cache 8
        // of them walk / almost as fast as 32 (0.56 vs 0.43 s) for half the CPU (2.7 vs 5.2 s)
        const int BackgroundThreads = 8;

        public static IEnumerable<INode> Walk(DriveInfo drive)
            => Walk(drive, StallTimeout, CancellationToken.None);

        public static IEnumerable<INode> Walk(DriveInfo drive, CancellationToken cancellationToken)
            => Walk(drive, StallTimeout, cancellationToken);

        /// <param name="background">A rescan nobody waits for - walk with fewer threads</param>
        public static IEnumerable<INode> Walk(DriveInfo drive, CancellationToken cancellationToken, bool background)
            => Walk(drive.RootDirectory, StallTimeout, cancellationToken,
                LinuxDirectoryReader.IsSupported ? () => new LinuxDirectoryReader() : null,
                background ? BackgroundThreads : int.MaxValue);

        public static IEnumerable<INode> Walk(DriveInfo drive, TimeSpan stallTimeout)
            => Walk(drive, stallTimeout, CancellationToken.None);

        public static IEnumerable<INode> Walk(DriveInfo drive, TimeSpan stallTimeout,
            CancellationToken cancellationToken)
            => Walk(drive.RootDirectory, stallTimeout, cancellationToken, LinuxDirectoryReader.IsSupported);

        /// <param name="rootDirectory">The drive's root - or any directory (tests)</param>
        /// <param name="nativeListing">
        /// List each directory with getdents64 + statx (LinuxDirectoryReader) instead of the
        /// managed enumeration. Both build the same nodes; the native one is faster.
        /// </param>
        internal static IEnumerable<INode> Walk(DirectoryInfo rootDirectory, TimeSpan stallTimeout,
            CancellationToken cancellationToken, bool nativeListing)
            => Walk(rootDirectory, stallTimeout, cancellationToken,
                nativeListing ? () => new LinuxDirectoryReader() : null);

        /// <param name="newReader">One reader per worker for the native listing; null = managed enumeration</param>
        /// <param name="maxThreads">Upper bound for the workers (one per CPU by default)</param>
        internal static IEnumerable<INode> Walk(DirectoryInfo rootDirectory, TimeSpan stallTimeout,
            CancellationToken cancellationToken, Func<LinuxDirectoryReader> newReader,
            int maxThreads = int.MaxValue)
        {
            var result = new ConcurrentBag<INode>();
            var directories = new ConcurrentBag<PendingDirectory>();
            Run(rootDirectory, stallTimeout, cancellationToken, newReader, maxThreads, result, null, 0, directories);
            cancellationToken.ThrowIfCancellationRequested();
            AggregateFolderSizes(directories.ToArray());
            return result;
        }

        /// <summary>
        /// Walk without keeping anything: the entries of each directory go to
        /// <paramref name="listed"/> with that directory's node as they are listed - up to
        /// <paramref name="batch"/> at a time, the rest when the listing ends; first the root's
        /// node alone, with no directory - from the walking threads, and are dropped. The
        /// directories are listed as soon as they are found, as by the walk that keeps its
        /// entries: a directory's entries may come before the directory itself. Folder sizes
        /// and counts stay unset - <see cref="StreamedTotals"/> adds them where the nodes come
        /// together. A consumer may block to slow the walk down - with the token it gets, which
        /// ends that wait when the walk ends - but one that takes nothing for the stall timeout
        /// ends the walk incomplete. Listings that hang for the stall timeout end it too, with
        /// everything they read: handed over then, as the walk that keeps its entries keeps it.
        /// </summary>
        /// <returns>False when the consumer failed or stopped taking: what it got is not all</returns>
        internal static bool Stream(DirectoryInfo rootDirectory, TimeSpan stallTimeout,
            CancellationToken cancellationToken, Func<LinuxDirectoryReader> newReader,
            Action<FileNode, IReadOnlyList<FileNode>, CancellationToken> listed, int batch = int.MaxValue,
            int maxThreads = int.MaxValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
            var complete = Run(rootDirectory, stallTimeout, cancellationToken, newReader, maxThreads, null, listed, batch, null);
            cancellationToken.ThrowIfCancellationRequested();
            return complete;
        }

        /// <param name="result">Gets every entry as it is read - a listing that hangs keeps what it read</param>
        /// <param name="listed">Or each directory with its entries, in batches as it is listed; called from the workers</param>
        /// <param name="directories">Gets every directory with the totals of its own entries; null keeps none</param>
        /// <returns>False when <paramref name="listed"/> failed or took nothing for the stall timeout</returns>
        static bool Run(DirectoryInfo rootDirectory, TimeSpan stallTimeout,
            CancellationToken cancellationToken, Func<LinuxDirectoryReader> newReader, int maxThreads,
            ConcurrentBag<INode> result, Action<FileNode, IReadOnlyList<FileNode>, CancellationToken> listed, int batch,
            ConcurrentBag<PendingDirectory> directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };

            var queue = new ConcurrentQueue<PendingDirectory>();
            var root = FileNode.Create(rootDirectory); // The MFT path has a root node too
            var rootPath = rootDirectory.FullName;
            var rootPending = new PendingDirectory(root, null);
            directories?.Add(rootPending);
            queue.Enqueue(rootPending);
            var pending = 1;
            var lastProgress = Environment.TickCount64;
            var failed = 0;
            var handing = 0; //Batches taken from listings and not taken by the consumer yet
            var ending = false; //Hung listings are being handed over: list nothing more

            // Not disposed: a worker abandoned inside an uninterruptible syscall may still
            // reference the token and the event after Walk returns; let the GC reclaim them
            // with that thread.
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            result?.Add(root);
            try { listed?.Invoke(null, new[] { root }, token); }
            catch (Exception) { return false; }
            var threadCount = Math.Max(2, Math.Min(maxThreads, Environment.ProcessorCount));
            var running = threadCount;
            var finished = new ManualResetEvent(false);
            var listings = new Listing[threadCount]; //Streaming: each worker's current listing

            // Dedicated below-normal threads, never the pool: the workers sit blocked in
            // (network) I/O for the whole walk, and a pool-thread version occupies the pool's
            // entire warm size - starving the grid publish and every other task in the app
            // for the minutes a network walk takes
            var workers = Enumerable.Range(0, threadCount).Select(index =>
            {
                var worker = new Thread(() =>
                {
                    try { Work(index); }
                    finally { if (Interlocked.Decrement(ref running) == 0) finished.Set(); }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                worker.Start();
                return worker;
            }).ToArray();

            void Work(int index)
            {
                var reader = newReader?.Invoke();
                PendingDirectory current = null;
                Listing listing = null; //Streaming: what the listing of current read and did not hand over yet
                Exception handOverFailed = null;
                // Streaming: a batch taken from the listing (counted in handing) to the consumer
                bool HandOver(List<FileNode> entries)
                {
                    try { listed(current.Node, entries, token); }
                    catch (Exception e)
                    {
                        handOverFailed = e;
                        return false;
                    }
                    finally { Interlocked.Decrement(ref handing); }
                    Volatile.Write(ref lastProgress, Environment.TickCount64); //Taken: progress too
                    return true;
                }
                // One listed entry of current; false stops listing it (cancelled)
                bool Add(FileNode node)
                {
                    if (token.IsCancellationRequested || cancellationToken.IsCancellationRequested
                        || Volatile.Read(ref ending)) return false;
                    //Another file system mounted here is its own root (Linux) - never part of this one
                    if (node.IsDirectory && FsRoots.IsForeignMountPoint(node.FullName, rootPath)) return true;
                    List<FileNode> full = null;
                    if (result != null) result.Add(node);
                    else lock (listing) //The watchdog takes what a hung listing read under it
                    {
                        if (listing.Taken) return false;
                        listing.Entries.Add(node);
                        if (listing.Entries.Count >= batch)
                        {
                            full = listing.Entries;
                            listing.Entries = new List<FileNode>();
                            Interlocked.Increment(ref handing);
                        }
                    }
                    //Every worker passes here per entry - write only when the tick changed so
                    //the threads do not fight over the cache line
                    var now = Environment.TickCount64;
                    if (Volatile.Read(ref lastProgress) != now) Volatile.Write(ref lastProgress, now);
                    //Only this worker lists current - plain adds are safe
                    current.Count++;
                    if (!node.IsDirectory) current.Size += node.Size;
                    if (node.IsDirectory && !node.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        var child = new PendingDirectory(node, current);
                        directories?.Add(child);
                        Interlocked.Increment(ref pending);
                        queue.Enqueue(child);
                    }
                    return full == null || HandOver(full);
                }
                Func<FileNode, bool> add = Add;

                while (Volatile.Read(ref pending) > 0 && !token.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested && !Volatile.Read(ref ending))
                {
                    if (!queue.TryDequeue(out current))
                    {
                        token.WaitHandle.WaitOne(1); // Brief blocking wait - not a busy spin - while another worker fills the queue
                        continue;
                    }
                    if (listed != null) Volatile.Write(ref listings[index], listing = new Listing(current.Node));
                    try
                    {
                        var path = current.Node.FullName;
                        if (reader != null) reader.List(path, followLink: current.Parent == null, add);
                        else ListManaged(path, options, add);
                    }
                    catch { } // Whole directory unreadable => skip it
                    try
                    {
                        //The rest handed over before this directory stops counting as pending:
                        //every entry is out by the time the walk ends - unless the watchdog
                        //took it from a listing that hung
                        if (listing != null)
                        {
                            List<FileNode> rest = null;
                            lock (listing)
                            {
                                if (!listing.Taken && handOverFailed == null && listing.Entries.Count > 0)
                                {
                                    rest = listing.Entries;
                                    Interlocked.Increment(ref handing);
                                }
                                listing.Taken = true;
                                listing.Entries = null;
                            }
                            if (rest != null) HandOver(rest);
                        }
                        if (handOverFailed is OperationCanceledException && token.IsCancellationRequested) { } //The walk ended
                        else if (handOverFailed != null)
                        {
                            Volatile.Write(ref failed, 1);
                            cts.Cancel();
                        }
                    }
                    finally
                    {
                        listing = null; //Handed over: not kept here while this worker waits
                        handOverFailed = null;
                        Interlocked.Decrement(ref pending);
                    }
                }
            }

            // Listings that hung read what nobody else will: hand it over as the walk ends, as
            // the walk that keeps its entries keeps it - with the batches workers took just
            // before. False when the consumer did not take it all within the stall timeout.
            bool HandOverHungListings()
            {
                Volatile.Write(ref ending, true);
                var hung = new List<(FileNode Directory, List<FileNode> Entries)>();
                for (var i = 0; i < listings.Length; i++)
                    if (Volatile.Read(ref listings[i]) is { } l) //A worker that never listed has none
                        lock (l)
                        {
                            if (l.Taken) continue;
                            l.Taken = true;
                            if (l.Entries.Count > 0) hung.Add((l.Directory, l.Entries));
                            l.Entries = null;
                        }
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(stallTimeout);
                try
                {
                    foreach (var (directory, entries) in hung) listed(directory, entries, limit.Token);
                    while (Volatile.Read(ref handing) > 0)
                        if (limit.Token.WaitHandle.WaitOne(1)) return false;
                    return true;
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception)
                {
                    Volatile.Write(ref failed, 1);
                    return false;
                }
            }

            // Stall watchdog: cancel once the walk stops producing entries for stallTimeout.
            // A batch waiting for the consumer all that time: it stopped taking, and what it
            // did not take is lost. Otherwise listings hung: what they read goes out first.
            var notTaken = false;
            var wakeUp = new[] { cancellationToken.WaitHandle, finished };
            while (workers.Any(w => w.IsAlive))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (Environment.TickCount64 - Volatile.Read(ref lastProgress) > stallTimeout.TotalMilliseconds)
                {
                    notTaken = Volatile.Read(ref handing) > 0 || listed != null && !HandOverHungListings();
                    cts.Cancel(); // Workers not stuck in a syscall exit at once
                    // Brief grace, then abandon any stuck in an uninterruptible read
                    var grace = Environment.TickCount64 + 3000;
                    foreach (var w in workers) w.Join((int)Math.Max(0, grace - Environment.TickCount64));
                    break;
                }
                // Wake at once when the last worker ends - not up to a second later
                var signaled = WaitHandle.WaitAny(wakeUp, 1000);
                if (signaled == 0)
                {
                    cts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (signaled == 1) break; // Every worker has left its loop
            }
            return !notTaken && Volatile.Read(ref failed) == 0;
        }

        /// <summary>
        /// What a streaming worker's listing read and did not hand over yet - the watchdog
        /// takes it from a listing that hangs (under its lock)
        /// </summary>
        sealed class Listing
        {
            public Listing(FileNode directory) => Directory = directory;

            public readonly FileNode Directory;
            public List<FileNode> Entries = new();
            /// <summary>Nothing more comes from it: the worker finished it or the watchdog took it</summary>
            public bool Taken;
        }

        /// <summary>
        /// A directory queued for listing, with the totals of the entries listed in it
        /// </summary>
        internal sealed class PendingDirectory
        {
            public PendingDirectory(FileNode node, PendingDirectory parent)
            {
                Node = node;
                Parent = parent;
                Depth = parent == null ? 0 : parent.Depth + 1;
            }

            public readonly FileNode Node;
            public readonly PendingDirectory Parent;
            public readonly int Depth;
            /// <summary>Entries listed here (files and directories), later plus all below</summary>
            public long Count;
            /// <summary>Bytes of the files listed here, later plus all below</summary>
            public ulong Size;
        }

        /// <summary>
        /// The MFT path computes folder aggregates - keep the walk consistent with it.
        /// Count includes every descendant file and directory, but not the directory itself;
        /// only files contribute bytes. Each listing already summed its own entries, so the
        /// directories just pass their totals up, deepest first.
        /// </summary>
        static void AggregateFolderSizes(PendingDirectory[] directories)
        {
            var depths = new int[directories.Length];
            for (var i = 0; i < directories.Length; i++) depths[i] = -directories[i].Depth;
            Array.Sort(depths, directories);
            foreach (var directory in directories)
                if (directory.Parent != null)
                {
                    directory.Parent.Count += directory.Count;
                    directory.Parent.Size += directory.Size;
                }
            foreach (var directory in directories)
            {
                directory.Node.AddSize(directory.Size);
                directory.Node.AddCountDelta(directory.Count);
            }
        }

        /// <summary>
        /// The folder totals of a streamed walk (<see cref="Stream"/>), summed up as its
        /// batches arrive - in any order: a folder's entries may come before the folder - and
        /// passed up at the end: the totals a walk adds (AggregateFolderSizes), for one lookup
        /// per batch and per folder
        /// </summary>
        internal sealed class StreamedTotals
        {
            internal sealed class Folder
            {
                internal FileNode Node; //Null until the folder itself came
                internal Folder Parent;
                internal long Count;
                internal ulong Size;
            }

            readonly Dictionary<string, Folder> byPath = new(StringComparer.Ordinal);

            /// <summary>The walk's root, its first node</summary>
            public void Root(FileNode root) => Came(root, null);

            /// <summary>The folder whose entries follow - whether it came already or not</summary>
            public Folder FolderAt(string path)
            {
                ref var folder = ref CollectionsMarshal.GetValueRefOrAddDefault(byPath, path, out _);
                return folder ??= new Folder();
            }

            /// <summary>One entry of a folder, as its listing sums it</summary>
            public void Add(Folder folder, FileNode entry)
            {
                folder.Count++;
                if (!entry.IsDirectory) folder.Size += entry.Size;
                else if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) Came(entry, folder);
            }

            void Came(FileNode node, Folder parent)
            {
                var folder = FolderAt(node.FullName);
                if (folder.Node != null) throw new InvalidDataException($"{node.FullName} came twice");
                folder.Node = node;
                folder.Parent = parent;
            }

            /// <summary>
            /// Pass the totals up, deepest first - a folder's path is longer than its parent's -
            /// onto the folders' nodes
            /// </summary>
            public void Complete()
            {
                var folders = byPath.ToArray();
                var lengths = new int[folders.Length];
                for (var i = 0; i < folders.Length; i++)
                {
                    if (folders[i].Value.Node == null) throw new InvalidDataException($"the items of {folders[i].Key} came without it");
                    lengths[i] = -folders[i].Key.Length;
                }
                Array.Sort(lengths, folders);
                foreach (var (_, folder) in folders)
                    if (folder.Parent != null)
                    {
                        folder.Parent.Count += folder.Count;
                        folder.Parent.Size += folder.Size;
                    }
                foreach (var (_, folder) in folders)
                {
                    folder.Node.AddSize(folder.Size);
                    folder.Node.AddCountDelta(folder.Count);
                }
            }
        }

        /// <summary>One directory through the managed enumeration (Windows, and Linux without getdents64/statx)</summary>
        static void ListManaged(string directory, EnumerationOptions options, Func<FileNode, bool> add)
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                if (!add(FileNode.Create(info))) break;
        }
    }
}
