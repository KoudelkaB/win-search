using System;
using System.Collections.Generic;
using System.Threading;

namespace search.Models
{
    internal readonly record struct MftNamePoolStats(
        long NamesSeen, long UniqueNames, long SavedBytes)
    {
        public long DuplicateNames => NamesSeen - UniqueNames;
    }

    /// <summary>
    /// Scan-local exact-case string canonicalizer for MFT leaf names. Sixty-four independent
    /// sets spread parallel parser writes across locks. Each worker also keeps a tiny
    /// direct-mapped cache, so common package/framework names stop touching the shared sets
    /// after their first occurrence on that worker.
    /// Names are looked up straight from the record bytes: a duplicate (about two of every
    /// three names on a Windows volume) never allocates a string at all.
    /// </summary>
    internal sealed class MftNamePool : IDisposable
    {
        const int StripeCount = 64;
        const int LocalCacheSize = 256;

        sealed class Stripe
        {
            public readonly object Gate = new();
            public HashSet<string> Names;
            public HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup;

            public Stripe(int capacity)
            {
                Names = capacity == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(capacity, StringComparer.Ordinal);
                Lookup = Names.GetAlternateLookup<ReadOnlySpan<char>>();
            }
        }

        sealed class LocalCache
        {
            readonly int[] hashes = new int[LocalCacheSize];
            readonly string[] names = new string[LocalCacheSize];

            public long NamesSeen;
            public long UniqueNames;
            public long SavedBytes;

            public bool TryGet(ReadOnlySpan<char> value, int hash, out string canonical)
            {
                var at = (hash ^ (hash >> 16)) & (LocalCacheSize - 1);
                canonical = names[at];
                return canonical != null && hashes[at] == hash
                    && value.SequenceEqual(canonical);
            }

            public void Store(int hash, string canonical)
            {
                var at = (hash ^ (hash >> 16)) & (LocalCacheSize - 1);
                hashes[at] = hash;
                names[at] = canonical;
            }
        }

        readonly Stripe[] stripes = new Stripe[StripeCount];
        readonly ThreadLocal<LocalCache> localCaches =
            new(() => new LocalCache(), trackAllValues: true);
        bool disposed;

        public MftNamePool(int estimatedNames)
        {
            //A normal MFT has roughly one selected name per live base record. Half the
            //record count is a conservative unique-name estimate on developer/Windows trees:
            //large enough to avoid most HashSet growth, without reserving for every duplicate.
            var perStripeCapacity = Math.Max(0, estimatedNames / (StripeCount * 2));
            for (var i = 0; i < stripes.Length; i++)
                stripes[i] = new Stripe(perStripeCapacity);
        }

        public string Canonicalize(string value)
            => value == null ? null : Canonicalize(value.AsSpan(), value);

        /// <summary>
        /// The canonical string for these characters - allocated only the first time the
        /// content is seen (a decoded $FILE_NAME can therefore be looked up in place).
        /// </summary>
        public string Canonicalize(ReadOnlySpan<char> value) => Canonicalize(value, null);

        string Canonicalize(ReadOnlySpan<char> value, string instance)
        {
            //Same hash as string.GetHashCode() over equal content
            var hash = string.GetHashCode(value);
            var local = localCaches.Value;
            local.NamesSeen++;
            if (local.TryGet(value, hash, out var canonical))
            {
                local.SavedBytes += StringBytes(value.Length);
                return canonical;
            }

            var stripe = stripes[(int)(unchecked((uint)hash) & (StripeCount - 1))];
            lock (stripe.Gate)
            {
                if (stripe.Lookup.TryGetValue(value, out canonical))
                    local.SavedBytes += StringBytes(value.Length);
                else
                {
                    canonical = instance ?? new string(value);
                    stripe.Names.Add(canonical);
                    local.UniqueNames++;
                }
            }
            local.Store(hash, canonical);
            return canonical;
        }

        public MftNamePoolStats Stats
        {
            get
            {
                long seen = 0, unique = 0, saved = 0;
                foreach (var local in localCaches.Values)
                {
                    seen += local.NamesSeen;
                    unique += local.UniqueNames;
                    saved += local.SavedBytes;
                }
                return new MftNamePoolStats(seen, unique, saved);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            //Drop references immediately after streaming parse; only canonical strings
            //referenced by live nodes/extension winners survive the rest of MFT finalization.
            foreach (var stripe in stripes)
                lock (stripe.Gate)
                {
                    stripe.Names.Clear();
                    stripe.Names = null;
                    stripe.Lookup = default;
                }
            localCaches.Dispose();
        }

        static long StringBytes(int length)
        {
            //64-bit CLR string: 20B object/header/length, UTF-16 payload and terminator,
            //then 8B object alignment.
            var bytes = 22L + (long)length * sizeof(char);
            return (bytes + 7) & ~7L;
        }
    }
}
