using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using search.Core;

namespace search.Models
{
    internal readonly record struct MetadataReadRequest(string Path, ulong Frn);

    /// <summary>
    /// Resolves live metadata by NTFS file reference first, then through the same
    /// privileged provider that supplied the drive scan, with path stat as the
    /// portability/failure fallback. Remote requests are batched per volume.
    /// </summary>
    internal sealed class NtfsMetadataSource : IDisposable
    {
        readonly ConcurrentDictionary<string, Lazy<NtfsFileMetadataReader>> readers =
            new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, long> serviceRetryAfter =
            new(StringComparer.OrdinalIgnoreCase);

        public NodeMetadataSnapshot?[] Read(IReadOnlyList<MetadataReadRequest> requests,
            Func<string, MftOrigin?> getOrigin)
        {
            var results = new NodeMetadataSnapshot?[requests.Count];
            var unresolved = new List<(int Index, string Root)>();
            var authoritativeMisses = new HashSet<int>();

            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                if (request.Frn == 0 || !TryGetRoot(request.Path, out var root))
                    continue;

                var reader = readers.GetOrAdd(root,
                    r => new Lazy<NtfsFileMetadataReader>(
                        () => NtfsFileMetadataReader.TryOpen(r),
                        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                if (reader?.TryRead(request.Frn, out var metadata) == true)
                {
                    if (TrySnapshot(metadata, out var snapshot)) results[i] = snapshot;
                    continue;
                }

                //An elevated in-process reader has backup privilege. Its exact FRN miss
                //means deleted/reused, not "try the path and perhaps stat a replacement".
                if (reader != null && Program.IsProcessElevated)
                {
                    authoritativeMisses.Add(i);
                    continue;
                }
                unresolved.Add((i, root));
            }

            foreach (var group in unresolved.GroupBy(x => x.Root,
                StringComparer.OrdinalIgnoreCase))
            {
                var root = group.Key;
                var items = group.ToArray();
                var frns = items.Select(x => requests[x.Index].Frn).ToArray();
                NtfsFileMetadata?[] remote = null;
                var origin = getOrigin?.Invoke(root);

                if (origin == MftOrigin.Service)
                    remote = TryService(root, frns);

                if (remote == null && Broker.Available
                    && (origin == MftOrigin.Broker || origin == MftOrigin.Service
                        || !origin.HasValue))
                {
                    try { remote = Broker.ReadNtfsMetadata(root, frns); }
                    catch { remote = null; }
                }

                //MftSource prefers an already connected broker over the service. If that
                //helper later exits, the installed service is still a valid fallback.
                if (remote == null && origin == MftOrigin.Broker)
                    remote = TryService(root, frns);
                //During startup the watcher can deliver an FRN before the drive scan has
                //published its origin. Probe the service only for these unresolved IDs;
                //a short cooldown prevents repeated connects when it is not installed.
                if (remote == null && !origin.HasValue)
                    remote = TryService(root, frns);

                if (remote == null) continue; //Path fallback below
                for (var i = 0; i < items.Length; i++)
                {
                    authoritativeMisses.Add(items[i].Index);
                    if (remote[i].HasValue
                        && TrySnapshot(remote[i].Value, out var snapshot))
                        results[items[i].Index] = snapshot;
                }
            }

            //No FRN/provider (FileSystemWatcher, non-NTFS/network drive, helper
            //unavailable) retains the old general-filesystem behavior.
            for (var i = 0; i < requests.Count; i++)
                if (!results[i].HasValue && !authoritativeMisses.Contains(i)
                    && INode.TryReadMetadata(requests[i].Path, out var snapshot))
                    results[i] = snapshot;
            return results;
        }

        bool CanTryService(string root)
            => !serviceRetryAfter.TryGetValue(root, out var retry)
            || Environment.TickCount64 >= retry;

        NtfsFileMetadata?[] TryService(string root, IReadOnlyList<ulong> frns)
        {
            if (!CanTryService(root)) return null;
            var result = MftSource.TryReadMetadataFromService(root, frns);
            if (result == null)
                serviceRetryAfter[root] = Environment.TickCount64 + 30_000;
            else
                serviceRetryAfter.TryRemove(root, out _);
            return result;
        }

        static bool TryGetRoot(string path, out string root)
        {
            root = null;
            try
            {
                root = Path.GetPathRoot(path);
                return !string.IsNullOrWhiteSpace(root);
            }
            catch
            {
                return false;
            }
        }

        static bool TrySnapshot(NtfsFileMetadata metadata,
            out NodeMetadataSnapshot snapshot)
        {
            snapshot = default;
            try
            {
                var time = metadata.LastWriteFileTimeUtc == 0
                    ? DateTime.MinValue
                    : DateTime.FromFileTime(metadata.LastWriteFileTimeUtc);
                snapshot = new NodeMetadataSnapshot(
                    (FileAttributes)metadata.Attributes,
                    metadata.Size,
                    time);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var lazy in readers.Values)
                if (lazy.IsValueCreated) lazy.Value?.Dispose();
            readers.Clear();
        }
    }
}
