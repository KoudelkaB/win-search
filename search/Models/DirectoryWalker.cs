using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public static IEnumerable<INode> Walk(DriveInfo drive)
            => Walk(drive, StallTimeout, CancellationToken.None);

        public static IEnumerable<INode> Walk(DriveInfo drive, CancellationToken cancellationToken)
            => Walk(drive, StallTimeout, cancellationToken);

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
        internal static IEnumerable<INode> Walk(DirectoryInfo rootDirectory, TimeSpan stallTimeout,
            CancellationToken cancellationToken, Func<LinuxDirectoryReader> newReader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };

            var result = new ConcurrentBag<INode>();
            var directories = new ConcurrentBag<PendingDirectory>();
            var queue = new ConcurrentQueue<PendingDirectory>();
            var root = FileNode.Create(rootDirectory); // The MFT path has a root node too
            var rootPath = rootDirectory.FullName;
            result.Add(root);
            var rootPending = new PendingDirectory(root, null);
            directories.Add(rootPending);
            queue.Enqueue(rootPending);
            var pending = 1;
            var lastProgress = Environment.TickCount64;

            // Not disposed: a worker abandoned inside an uninterruptible syscall may still
            // reference the token and the event after Walk returns; let the GC reclaim them
            // with that thread.
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            var threadCount = Math.Max(2, Environment.ProcessorCount);
            var running = threadCount;
            var finished = new ManualResetEvent(false);

            // Dedicated below-normal threads, never the pool: the workers sit blocked in
            // (network) I/O for the whole walk, and a pool-thread version occupies the pool's
            // entire warm size - starving the grid publish and every other task in the app
            // for the minutes a network walk takes
            var workers = Enumerable.Range(0, threadCount).Select(_ =>
            {
                var worker = new Thread(() =>
                {
                    try { Work(); }
                    finally { if (Interlocked.Decrement(ref running) == 0) finished.Set(); }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                worker.Start();
                return worker;
            }).ToArray();

            void Work()
            {
                var reader = newReader?.Invoke();
                PendingDirectory current = null;
                // One listed entry of current; false stops listing it (cancelled)
                bool Add(FileNode node)
                {
                    if (token.IsCancellationRequested || cancellationToken.IsCancellationRequested) return false;
                    //Another file system mounted here is its own root (Linux) - never part of this one
                    if (node.IsDirectory && FsRoots.IsForeignMountPoint(node.FullName, rootPath)) return true;
                    result.Add(node);
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
                        directories.Add(child);
                        Interlocked.Increment(ref pending);
                        queue.Enqueue(child);
                    }
                    return true;
                }
                Func<FileNode, bool> add = Add;

                while (Volatile.Read(ref pending) > 0 && !token.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    if (!queue.TryDequeue(out current))
                    {
                        token.WaitHandle.WaitOne(1); // Brief blocking wait - not a busy spin - while another worker fills the queue
                        continue;
                    }
                    try
                    {
                        var path = current.Node.FullName;
                        if (reader != null) reader.List(path, followLink: current.Parent == null, add);
                        else ListManaged(path, options, add);
                    }
                    catch { } // Whole directory unreadable => skip it
                    finally
                    {
                        Interlocked.Decrement(ref pending);
                    }
                }
            }

            // Stall watchdog: cancel once the walk stops producing entries for stallTimeout.
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

            cancellationToken.ThrowIfCancellationRequested();
            AggregateFolderSizes(directories.ToArray());
            return result;
        }

        /// <summary>
        /// A directory queued for listing, with the totals of the entries listed in it
        /// </summary>
        sealed class PendingDirectory
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

        /// <summary>One directory through the managed enumeration (Windows, and Linux without getdents64/statx)</summary>
        static void ListManaged(string directory, EnumerationOptions options, Func<FileNode, bool> add)
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                if (!add(FileNode.Create(info))) break;
        }
    }
}
