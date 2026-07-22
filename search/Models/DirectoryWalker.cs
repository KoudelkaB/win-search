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
    /// (junction cycles, OneDrive placeholders).
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };

            var result = new ConcurrentBag<INode>();
            var queue = new ConcurrentQueue<string>();
            var root = new FileNode(drive.RootDirectory); // The MFT path has a root node too
            result.Add(root);
            queue.Enqueue(drive.RootDirectory.FullName);
            var pending = 1;
            var lastProgress = Environment.TickCount64;

            // Not disposed: a worker abandoned inside an uninterruptible syscall may still
            // reference the token after Walk returns; let the GC reclaim it with that thread.
            var cts = new CancellationTokenSource();
            var token = cts.Token;

            // Dedicated below-normal threads, never the pool: the workers sit blocked in
            // (network) I/O for the whole walk, and a pool-thread version occupies the pool's
            // entire warm size - starving the grid publish and every other task in the app
            // for the minutes a network walk takes
            var workers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount)).Select(_ =>
            {
                var worker = new Thread(() =>
                {
                    while (Volatile.Read(ref pending) > 0 && !token.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                    {
                        if (!queue.TryDequeue(out var dir))
                        {
                            token.WaitHandle.WaitOne(1); // Brief blocking wait - not a busy spin - while another worker fills the queue
                            continue;
                        }
                        try
                        {
                            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                            {
                                if (token.IsCancellationRequested || cancellationToken.IsCancellationRequested) break;
                                var node = new FileNode(info);
                                result.Add(node);
                                Volatile.Write(ref lastProgress, Environment.TickCount64);
                                if (node.IsDirectory && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                {
                                    Interlocked.Increment(ref pending);
                                    queue.Enqueue(info.FullName);
                                }
                            }
                        }
                        catch { } // Whole directory unreadable => skip it
                        finally
                        {
                            Interlocked.Decrement(ref pending);
                        }
                    }
                })
                { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                worker.Start();
                return worker;
            }).ToArray();

            // Stall watchdog: cancel once the walk stops producing entries for stallTimeout.
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
                if (cancellationToken.WaitHandle.WaitOne(1000))
                {
                    cts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            AggregateFolderSizes(result, cancellationToken);
            return result;
        }

        /// <summary>
        /// The MFT path computes folder sizes - keep the walk consistent with it
        /// </summary>
        static void AggregateFolderSizes(IEnumerable<INode> nodes, CancellationToken cancellationToken)
        {
            var dirs = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
            var checkedNodes = 0;
            foreach (var n in nodes)
            {
                if ((checkedNodes++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (n.IsDirectory && n is FileNode f)
                    dirs.TryAdd(f.FullName, f);
            }

            checkedNodes = 0;
            foreach (var file in nodes.Where(n => !n.IsDirectory))
            {
                if ((checkedNodes++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                // Add the file size to every ancestor directory up to the drive root
                for (var dir = Path.GetDirectoryName(file.FullName); dir != null; dir = Path.GetDirectoryName(dir))
                    if (dirs.TryGetValue(dir, out var d))
                        d.AddSize(file.Size);
            }
        }
    }
}
