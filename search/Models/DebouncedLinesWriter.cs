using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace search.Models
{
    /// <summary>
    /// One trailing-edge writer per small settings/history file. Schedule never performs
    /// filesystem I/O; a single worker serializes writes and retains only the newest pending
    /// snapshot when the caller changes it repeatedly.
    /// </summary>
    internal sealed class DebouncedLinesWriter : IDisposable
    {
        internal static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

        readonly object sync = new();
        readonly string path;
        readonly int delayMs;
        readonly Func<string, string[], Task> writeAsync;
        readonly SemaphoreSlim wake = new(0, int.MaxValue);

        string[] pending;
        long dueAt;
        Task worker = Task.CompletedTask;
        bool workerRunning;
        bool disposed;

        public DebouncedLinesWriter(string path, TimeSpan? delay = null,
            Func<string, string[], Task> writeAsync = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            this.path = path;
            var requestedDelay = (delay ?? DefaultDelay).TotalMilliseconds;
            delayMs = Math.Max(0, checked((int)Math.Min(int.MaxValue, requestedDelay)));
            this.writeAsync = writeAsync ?? WriteAllLinesAsync;
        }

        public void Schedule(string[] lines)
        {
            ArgumentNullException.ThrowIfNull(lines);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                pending = lines;
                dueAt = Environment.TickCount64 + delayMs;
                if (!workerRunning)
                {
                    workerRunning = true;
                    worker = Task.Run(DrainAsync);
                }
                wake.Release();
            }
        }

        async Task DrainAsync()
        {
            while (true)
            {
                string[] snapshot = null;
                var waitMs = 0;
                lock (sync)
                {
                    if (pending == null)
                    {
                        workerRunning = false;
                        return;
                    }
                    var remaining = dueAt - Environment.TickCount64;
                    if (remaining > 0)
                        waitMs = (int)Math.Min(int.MaxValue, remaining);
                    else
                    {
                        snapshot = pending;
                        pending = null;
                    }
                }

                if (snapshot == null)
                {
                    await wake.WaitAsync(waitMs).ConfigureAwait(false);
                    continue;
                }

                try { await writeAsync(path, snapshot).ConfigureAwait(false); }
                catch { } //History persistence must never become an application failure mode.
            }
        }

        public void Dispose()
        {
            Task flush;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (pending != null) dueAt = Environment.TickCount64;
                flush = worker;
                wake.Release();
            }
            try { flush.GetAwaiter().GetResult(); }
            catch { }
            wake.Dispose();
        }

        static Task WriteAllLinesAsync(string path, string[] lines)
            => File.WriteAllLinesAsync(path, lines);
    }
}
