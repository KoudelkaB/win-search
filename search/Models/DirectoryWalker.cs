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
        public static IEnumerable<INode> Walk(DriveInfo drive)
        {
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

            var workers = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount)).Select(_ => Task.Run(() =>
            {
                while (Volatile.Read(ref pending) > 0)
                {
                    if (!queue.TryDequeue(out var dir))
                    {
                        Thread.Yield(); // Another worker still fills the queue
                        continue;
                    }
                    try
                    {
                        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                        {
                            var node = new FileNode(info);
                            result.Add(node);
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
            })).ToArray();
            Task.WaitAll(workers);

            AggregateFolderSizes(result);
            return result;
        }

        /// <summary>
        /// The MFT path computes folder sizes - keep the walk consistent with it
        /// </summary>
        static void AggregateFolderSizes(IEnumerable<INode> nodes)
        {
            var dirs = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in nodes)
                if (n.IsDirectory && n is FileNode f)
                    dirs.TryAdd(f.FullName, f);

            foreach (var file in nodes.Where(n => !n.IsDirectory))
            {
                // Add the file size to every ancestor directory up to the drive root
                for (var dir = Path.GetDirectoryName(file.FullName); dir != null; dir = Path.GetDirectoryName(dir))
                    if (dirs.TryGetValue(dir, out var d))
                        d.AddSize(file.Size);
            }
        }
    }
}
