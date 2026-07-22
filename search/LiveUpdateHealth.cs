using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace search
{
    /// <summary>
    /// User-visible state of the live result grid. Delayed is only a warning; Suspended keeps
    /// automatic grid mutations paused while filesystem ingestion and index mutation continue.
    /// Recovering resumes delivery while one authoritative catch-up refresh is in flight.
    /// </summary>
    public enum LiveUpdateHealthState
    {
        Healthy,
        Delayed,
        Suspended,
        Recovering
    }

    internal readonly record struct FsPipelineHealth(
        int QueueDepth,
        long OldestEventAgeMs,
        long ReflectionLatencyMs,
        long BatchDurationMs,
        int BatchEventCount,
        string SlowestRoot = "",
        int SlowestQueueDepth = 0,
        int WaitingQueueDepth = 0,
        int ActiveBatchCount = 0,
        long ActiveBatchMs = 0,
        string ActiveStage = "",
        long ActiveStageMs = 0,
        string ActivePath = "");

    internal readonly record struct HealthWorkState(bool Loading, bool Filtering, bool Searching);

    internal readonly record struct LiveUpdateObservation(
        long DispatcherDelayMs,
        long ReflectionLatencyMs,
        int QueueDepth,
        long OldestEventAgeMs,
        long RefreshDurationMs,
        bool ThreadPoolStarved,
        bool Loading = false);

    /// <summary>
    /// Pure hysteresis/state logic kept separate from timers and WPF so the safety behavior
    /// can be regression tested without starting the application or touching the filesystem.
    /// </summary>
    internal sealed class LiveUpdateHealthStateMachine
    {
        internal const int DispatcherWarningMs = 500;
        internal const int DispatcherCriticalMs = 2000;
        internal const int ReflectionWarningMs = 750;
        internal const int ReflectionCriticalMs = 2500;
        internal const int QueueWarningMs = 1000;
        internal const int QueueCriticalMs = 5000;
        internal const int RefreshWarningMs = 2000;
        internal const int RefreshCriticalMs = 5000;
        internal const int WarningSamplesBeforeSuspend = 3;
        internal const int HealthySamplesBeforeClear = 3;
        internal const int HealthySamplesBeforeRecovery = 5;

        int warningSamples;
        int healthySamples;
        int recoverySamples;
        bool lastWasWarning;

        public LiveUpdateHealthState State { get; private set; } = LiveUpdateHealthState.Healthy;
        public string LastReason { get; private set; } = "healthy";

        public LiveUpdateHealthState Observe(LiveUpdateObservation value)
        {
            var critical = CriticalReason(value);
            var suspendableWarning = critical != null || IsSuspendableWarning(value);
            var warning = suspendableWarning || IsQueueWarning(value);
            lastWasWarning = warning;

            if (suspendableWarning)
            {
                warningSamples++;
                recoverySamples = 0;
            }
            else
            {
                warningSamples = 0;
                recoverySamples++;
            }
            healthySamples = warning ? 0 : healthySamples + 1;

            switch (State)
            {
                case LiveUpdateHealthState.Healthy:
                    if (critical != null) Suspend(critical);
                    else if (warning)
                    {
                        State = LiveUpdateHealthState.Delayed;
                        LastReason = "elevated-latency";
                    }
                    break;

                case LiveUpdateHealthState.Delayed:
                    if (critical != null) Suspend(critical);
                    else if (warningSamples >= WarningSamplesBeforeSuspend)
                        Suspend("sustained-latency");
                    else if (healthySamples >= HealthySamplesBeforeClear)
                    {
                        State = LiveUpdateHealthState.Healthy;
                        LastReason = "healthy";
                    }
                    break;

                case LiveUpdateHealthState.Suspended:
                    if (critical != null) LastReason = critical;
                    if (recoverySamples >= HealthySamplesBeforeRecovery)
                    {
                        State = LiveUpdateHealthState.Recovering;
                        LastReason = "catching-up";
                    }
                    break;

                case LiveUpdateHealthState.Recovering:
                    //The one authoritative catch-up is allowed to finish. A new critical
                    //condition keeps presentation suspended and requires another quiet window.
                    if (critical != null) Suspend(critical);
                    break;
            }
            return State;
        }

        public LiveUpdateHealthState CompleteRecovery(bool succeeded)
        {
            if (State != LiveUpdateHealthState.Recovering) return State;
            if (!succeeded)
            {
                Suspend("catch-up-failed");
                return State;
            }

            State = lastWasWarning ? LiveUpdateHealthState.Delayed : LiveUpdateHealthState.Healthy;
            LastReason = lastWasWarning ? "elevated-latency" : "healthy";
            warningSamples = 0;
            healthySamples = 0;
            recoverySamples = 0;
            return State;
        }

        void Suspend(string reason)
        {
            State = LiveUpdateHealthState.Suspended;
            LastReason = reason;
            healthySamples = 0;
            recoverySamples = 0;
        }

        static string CriticalReason(LiveUpdateObservation value)
        {
            if (value.DispatcherDelayMs >= DispatcherCriticalMs) return "dispatcher-delay";
            if (!value.Loading && value.ReflectionLatencyMs >= ReflectionCriticalMs) return "reflection-delay";
            if (!value.Loading && value.RefreshDurationMs >= RefreshCriticalMs) return "refresh-delay";
            if (value.ThreadPoolStarved) return "thread-pool-starvation";
            return null;
        }

        static bool IsSuspendableWarning(LiveUpdateObservation value)
            => value.DispatcherDelayMs >= DispatcherWarningMs
            || !value.Loading && value.ReflectionLatencyMs >= ReflectionWarningMs
            || !value.Loading && value.RefreshDurationMs >= RefreshWarningMs;

        static bool IsQueueWarning(LiveUpdateObservation value)
            => value.OldestEventAgeMs >= QueueWarningMs;
    }

    /// <summary>Allocation-free fixed history during normal operation.</summary>
    internal sealed class FixedRing<T>
    {
        readonly T[] values;
        readonly object sync = new();
        int next;
        int count;

        public FixedRing(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            values = new T[capacity];
        }

        public int Capacity => values.Length;
        public int Count { get { lock (sync) return count; } }

        public void Add(T value)
        {
            lock (sync)
            {
                values[next] = value;
                next = (next + 1) % values.Length;
                if (count < values.Length) count++;
            }
        }

        public T[] Snapshot(int maxItems = int.MaxValue)
        {
            lock (sync)
            {
                var take = Math.Min(count, Math.Max(0, maxItems));
                var result = new T[take];
                var start = (next - take + values.Length) % values.Length;
                for (var i = 0; i < take; i++) result[i] = values[(start + i) % values.Length];
                return result;
            }
        }
    }

    internal sealed record LiveUpdateHealthSnapshot(
        LiveUpdateHealthState State,
        string Reason,
        long DisplayLatencyMs,
        long DispatcherDelayMs,
        long ReflectionLatencyMs,
        int QueueDepth,
        long OldestEventAgeMs,
        long RefreshDurationMs,
        double CpuCores,
        long PrivateMemoryBytes,
        string QueueRoot,
        int QueueRootDepth);

    internal readonly record struct HealthSample
    {
        public DateTime Utc { get; init; }
        public long UiMs { get; init; }
        public long UiOperationMs { get; init; }
        public string UiOperationName { get; init; }
        public string UiOperationPriority { get; init; }
        public bool UiOperationActive { get; init; }
        public int UiPending { get; init; }
        public int UiPendingMax { get; init; }
        public double UiThreadCpuPercent { get; init; }
        public string UiThreadState { get; init; }
        public string UiThreadWaitReason { get; init; }
        public long FsMs { get; init; }
        public int Queue { get; init; }
        public long QueueOldMs { get; init; }
        public string QueueRoot { get; init; }
        public int QueueRootDepth { get; init; }
        public int QueueWaiting { get; init; }
        public int QueueActive { get; init; }
        public long QueueActiveMs { get; init; }
        public string QueueStage { get; init; }
        public long QueueStageMs { get; init; }
        public string QueuePath { get; init; }
        public long BatchMs { get; init; }
        public int BatchEvents { get; init; }
        public long RefreshMs { get; init; }
        public long RefreshDebounceMs { get; init; }
        public long RefreshQueueMs { get; init; }
        public long QueryMs { get; init; }
        public long UiWaitMs { get; init; }
        public long ApplyMs { get; init; }
        public long UiSettleMs { get; init; }
        public int RefreshRows { get; init; }
        public string Cache { get; init; }
        public string GridMode { get; init; }
        public int GridChanged { get; init; }
        public int GridRows { get; init; }
        public long GridUiWaitMs { get; init; }
        public long GridApplyMs { get; init; }
        public double CpuCores { get; init; }
        public long PrivateMb { get; init; }
        public long PrivateDeltaMb { get; init; }
        public long HeapMb { get; init; }
        public double AllocMbSec { get; init; }
        public double GcPausePercent { get; init; }
        public double GcMaxPauseMs { get; init; }
        public int Threads { get; init; }
        public int Handles { get; init; }
        public int ThreadPoolThreads { get; init; }
        public int ThreadPoolAvailable { get; init; }
        public long ThreadPoolPending { get; init; }
        public bool Loading { get; init; }
        public bool Filtering { get; init; }
        public bool Searching { get; init; }
        public LiveUpdateHealthState State { get; init; }
    }

    internal sealed class HealthEpisodeRecord
    {
        public int Version { get; init; } = 6;
        public string Kind { get; init; }
        public DateTime Utc { get; init; }
        public int ProcessId { get; init; }
        public string AppVersion { get; init; }
        public string Reason { get; init; }
        public double DurationSeconds { get; init; }
        public string[] RecentEvents { get; init; }
        public HealthSample[] Samples { get; init; }
    }

    internal readonly record struct DispatcherActivitySnapshot(
        long OperationMs, string Name, string Priority, bool Active, int Pending, int MaxPending);

    /// <summary>
    /// Low-allocation dispatcher flight recorder. It deliberately retains no completed
    /// DispatcherOperation/delegate instances, which could otherwise keep arbitrary UI object
    /// graphs alive and turn diagnostics into a source of memory pressure.
    /// </summary>
    internal sealed class DispatcherActivityProbe : IDisposable
    {
        readonly DispatcherHooks hooks;
        readonly object sync = new();
        static readonly FieldInfo operationMethod = FindOperationMethod();
        DispatcherOperation activeOperation;
        long activeStarted;
        int activePriority = (int)DispatcherPriority.Inactive;
        long longestCompleted;
        int longestPriority = (int)DispatcherPriority.Inactive;
        string longestName = "";
        int pending;
        int maxPending;

        static FieldInfo FindOperationMethod()
        {
            //WPF does not expose the callback identity publicly. Resolve its private
            //Delegate field by type rather than a framework-version-specific field name;
            //failure is harmless and simply leaves the diagnostic name empty.
            try
            {
                foreach (var field in typeof(DispatcherOperation).GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic))
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType)) return field;
            }
            catch { }
            return null;
        }

        static string OperationName(DispatcherOperation operation)
        {
            if (operation == null || operationMethod == null) return "";
            try
            {
                if (operationMethod.GetValue(operation) is not Delegate callback) return "";
                var method = callback.Method;
                var owner = method.DeclaringType?.FullName;
                return string.IsNullOrEmpty(owner) ? method.Name : owner + "." + method.Name;
            }
            catch { return ""; }
        }

        public DispatcherActivityProbe(Dispatcher dispatcher)
        {
            hooks = (dispatcher ?? throw new ArgumentNullException(nameof(dispatcher))).Hooks;
            hooks.OperationPosted += OperationPosted;
            hooks.OperationStarted += OperationStarted;
            hooks.OperationCompleted += OperationCompleted;
            hooks.OperationAborted += OperationAborted;
        }

        void OperationPosted(object sender, DispatcherHookEventArgs e)
        {
            var count = Interlocked.Increment(ref pending);
            var observed = Volatile.Read(ref maxPending);
            while (count > observed)
            {
                var prior = Interlocked.CompareExchange(ref maxPending, count, observed);
                if (prior == observed) break;
                observed = prior;
            }
        }

        void OperationStarted(object sender, DispatcherHookEventArgs e)
        {
            Volatile.Write(ref activeOperation, e.Operation);
            Volatile.Write(ref activePriority, (int)e.Operation.Priority);
            Interlocked.Exchange(ref activeStarted, Environment.TickCount64);
        }

        void OperationCompleted(object sender, DispatcherHookEventArgs e)
        {
            Complete(e.Operation);
            DecrementPending();
        }

        void OperationAborted(object sender, DispatcherHookEventArgs e)
        {
            //An operation normally aborts before it starts. Do not let an unrelated queued
            //operation with the same priority clear the currently executing operation.
            if (ReferenceEquals(Volatile.Read(ref activeOperation), e.Operation))
            {
                Interlocked.Exchange(ref activeOperation, null);
                Interlocked.Exchange(ref activeStarted, 0);
            }
            DecrementPending();
        }

        void Complete(DispatcherOperation operation)
        {
            if (!ReferenceEquals(Interlocked.Exchange(ref activeOperation, null), operation))
                return;
            var started = Interlocked.Exchange(ref activeStarted, 0);
            if (started == 0) return;
            var duration = Math.Max(0, Environment.TickCount64 - started);
            lock (sync)
            {
                if (duration <= longestCompleted) return;
                longestCompleted = duration;
                longestPriority = (int)operation.Priority;
                longestName = OperationName(operation);
            }
        }

        void DecrementPending()
        {
            if (Interlocked.Decrement(ref pending) >= 0) return;
            //The probe can attach while startup operations are already queued, so their
            //completion has no matching Posted notification in this probe's lifetime.
            Interlocked.Exchange(ref pending, 0);
        }

        public DispatcherActivitySnapshot Snapshot(long now)
        {
            var activeAt = Interlocked.Read(ref activeStarted);
            var activeMs = activeAt == 0 ? 0 : Math.Max(0, now - activeAt);
            var priority = Volatile.Read(ref activePriority);
            long completedMs;
            int completedPriority;
            string completedName;
            lock (sync)
            {
                completedMs = longestCompleted;
                completedPriority = longestPriority;
                completedName = longestName;
                longestCompleted = 0;
                longestPriority = (int)DispatcherPriority.Inactive;
                longestName = "";
            }
            var active = activeMs >= completedMs;
            var operationMs = active ? activeMs : completedMs;
            var operationPriority = active ? priority : completedPriority;
            var operationName = active
                ? OperationName(Volatile.Read(ref activeOperation)) : completedName;
            var currentPending = Math.Max(0, Volatile.Read(ref pending));
            var peakPending = Math.Max(currentPending,
                Interlocked.Exchange(ref maxPending, currentPending));
            return new DispatcherActivitySnapshot(operationMs, operationName,
                ((DispatcherPriority)operationPriority).ToString(), activeAt != 0 && active,
                currentPending, peakPending);
        }

        public void Dispose()
        {
            hooks.OperationPosted -= OperationPosted;
            hooks.OperationStarted -= OperationStarted;
            hooks.OperationCompleted -= OperationCompleted;
            hooks.OperationAborted -= OperationAborted;
        }
    }

    internal readonly record struct UiThreadActivitySnapshot(
        double CpuPercent, string State, string WaitReason);

    /// <summary>Distinguishes a CPU-busy UI thread from one blocked in a native wait.</summary>
    internal sealed class UiThreadActivityProbe : IDisposable
    {
        readonly ProcessThread thread;
        long lastCpuTicks;

        public UiThreadActivityProbe(Process process, int nativeThreadId)
        {
            try
            {
                foreach (ProcessThread candidate in process.Threads)
                    if (candidate.Id == nativeThreadId)
                    {
                        thread = candidate;
                        lastCpuTicks = candidate.TotalProcessorTime.Ticks;
                        break;
                    }
            }
            catch { }
        }

        public UiThreadActivitySnapshot Snapshot(long elapsedMs)
        {
            if (thread == null) return default;
            try
            {
                var ticks = thread.TotalProcessorTime.Ticks;
                var cpu = Math.Max(0, ticks - lastCpuTicks) * 100d
                    / TimeSpan.TicksPerMillisecond / Math.Max(1, elapsedMs);
                lastCpuTicks = ticks;
                var state = thread.ThreadState;
                var wait = state == System.Diagnostics.ThreadState.Wait
                    ? thread.WaitReason.ToString() : "";
                return new UiThreadActivitySnapshot(Math.Round(cpu, 1), state.ToString(), wait);
            }
            catch { return default; }
        }

        public void Dispose() => thread?.Dispose();
    }

    internal enum UiLagEpisodeAction { None, Write }

    /// <summary>
    /// Records orange-only UI stalls after they recover. A stall escalating to red is already
    /// covered by the richer critical episode, and a cooldown bounds repeated disk writes.
    /// </summary>
    internal sealed class UiLagEpisodeGate
    {
        internal const int HealthySamplesBeforeWrite = 3;
        internal const int CooldownMs = 60000;
        bool active;
        int healthySamples;
        long started;
        long cooldownUntil;

        public long LastDurationMs { get; private set; }

        public UiLagEpisodeAction Observe(long now, long uiLagMs, bool coveredByCriticalEpisode)
        {
            if (coveredByCriticalEpisode)
            {
                active = false;
                healthySamples = 0;
                cooldownUntil = now + CooldownMs;
                return UiLagEpisodeAction.None;
            }
            if (!active)
            {
                if (uiLagMs >= LiveUpdateHealthStateMachine.DispatcherWarningMs
                    && now >= cooldownUntil)
                {
                    active = true;
                    started = now;
                    healthySamples = 0;
                }
                return UiLagEpisodeAction.None;
            }
            if (uiLagMs >= LiveUpdateHealthStateMachine.DispatcherWarningMs)
            {
                healthySamples = 0;
                return UiLagEpisodeAction.None;
            }
            if (++healthySamples < HealthySamplesBeforeWrite) return UiLagEpisodeAction.None;

            LastDurationMs = Math.Max(0, now - started);
            active = false;
            healthySamples = 0;
            cooldownUntil = now + CooldownMs;
            return UiLagEpisodeAction.Write;
        }
    }

    /// <summary>
    /// One low-priority watchdog thread samples a fixed in-memory flight recorder. Healthy
    /// operation performs no disk writes. Crossing into Suspended or a critical per-drive
    /// backlog flushes pre-history once; recovery writes one bounded summary. No periodic
    /// logging can exhaust the disk.
    /// </summary>
    internal sealed class LiveUpdateHealthMonitor : IDisposable
    {
        internal const int SampleIntervalMs = 1000;
        internal const int HistorySamples = 120;
        internal const int ExpectedCoalescingMs = 200;
        internal const int RecentOperationMs = 5000;
        static readonly JsonSerializerOptions HealthJson = new()
        {
            Converters = { new JsonStringEnumConverter() }
        };

        readonly Dispatcher dispatcher;
        readonly Func<FsPipelineHealth> filesystem;
        readonly Func<string[]> recentEvents;
        readonly Func<HealthWorkState> workState;
        readonly Action<LiveUpdateHealthSnapshot> publish;
        readonly Func<Task<bool>> catchUp;
        readonly FixedRing<HealthSample> history = new(HistorySamples);
        readonly LiveUpdateHealthStateMachine machine = new();
        readonly ManualResetEventSlim stop = new(false);
        readonly Process process = Process.GetCurrentProcess();
        readonly object stateLock = new();
        readonly object gridMetricLock = new();
        readonly Thread thread;
        readonly DispatcherActivityProbe dispatcherActivity;
        readonly UiThreadActivityProbe uiThreadActivity;
        readonly UiLagEpisodeGate uiLagEpisode = new();

        long refreshStarted;
        long lastRefreshDuration;
        long lastRefreshCompleted;
        long refreshDebounceMs;
        long refreshQueueMs;
        long refreshQueryMs;
        long refreshUiWaitMs;
        long refreshApplyMs;
        long refreshUiSettleMs;
        int refreshRows;
        int refreshGeneration;
        string refreshCache = "";
        long lastGridMutationCompleted;
        long gridUiWaitMs;
        long gridApplyMs;
        int gridChanged;
        int gridRows;
        string gridMode = "";
        long gridUpdatePendingSince;
        long lastGridReflectionLatency;
        long lastGridReflectionCompleted;
        long heartbeatQueued;
        long lastHeartbeatDelay;
        long lastSampleTick;
        long lastCpuTicks;
        long lastAllocated;
        long baselinePrivateBytes;
        int baselineSamples;
        int heartbeatPending;
        int heartbeatGeneration;
        int publishPending;
        int disposed;
        int suspendAutomaticUpdates;
        LiveUpdateHealthSnapshot latestSnapshot;
        bool episodeActive;
        bool pipelineEpisodeActive;
        long smoothedDisplayLatency;
        long episodeStarted;
        long pipelineEpisodeStarted;
        int episodePostSamplesRemaining;
        int pipelineHealthySamples;

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        public LiveUpdateHealthMonitor(Dispatcher dispatcher,
            Func<FsPipelineHealth> filesystem,
            Func<string[]> recentEvents,
            Func<HealthWorkState> workState,
            Action<LiveUpdateHealthSnapshot> publish,
            Func<Task<bool>> catchUp)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.filesystem = filesystem ?? (() => default);
            this.recentEvents = recentEvents ?? (() => Array.Empty<string>());
            this.workState = workState ?? (() => default);
            this.publish = publish ?? (_ => { });
            this.catchUp = catchUp ?? (() => Task.FromResult(true));

            //SearchModel constructs this monitor on the WPF thread. Keep the fallback for
            //tests/future callers so the native id always belongs to the dispatcher owner.
            var uiThreadId = dispatcher.CheckAccess()
                ? unchecked((int)GetCurrentThreadId())
                : dispatcher.Invoke(() => unchecked((int)GetCurrentThreadId()));
            dispatcherActivity = new DispatcherActivityProbe(dispatcher);
            uiThreadActivity = new UiThreadActivityProbe(process, uiThreadId);

            lastSampleTick = Environment.TickCount64;
            lastCpuTicks = process.TotalProcessorTime.Ticks;
            lastAllocated = GC.GetTotalAllocatedBytes(false);
            thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "live-update health",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
        }

        public bool SuspendAutomaticGridUpdates => Volatile.Read(ref suspendAutomaticUpdates) != 0;

        public int RefreshStarted(long debounceMs, long queueMs)
        {
            var generation = Interlocked.Increment(ref refreshGeneration);
            Interlocked.Exchange(ref refreshDebounceMs, Math.Max(0, debounceMs));
            Interlocked.Exchange(ref refreshQueueMs, Math.Max(0, queueMs));
            Interlocked.Exchange(ref refreshQueryMs, 0);
            Interlocked.Exchange(ref refreshUiWaitMs, 0);
            Interlocked.Exchange(ref refreshApplyMs, 0);
            Interlocked.Exchange(ref refreshUiSettleMs, 0);
            Volatile.Write(ref refreshRows, 0);
            Volatile.Write(ref refreshCache, "");
            Interlocked.CompareExchange(ref refreshStarted, Environment.TickCount64, 0);
            return generation;
        }

        public void RefreshQueryCompleted(long queryMs, int rows, string cache)
        {
            Interlocked.Exchange(ref refreshQueryMs, Math.Max(0, queryMs));
            Volatile.Write(ref refreshRows, Math.Max(0, rows));
            Volatile.Write(ref refreshCache, cache ?? "");
        }

        public void RefreshApplied(long dispatcherWaitMs, long applyDurationMs)
        {
            Interlocked.Exchange(ref refreshUiWaitMs, Math.Max(0, dispatcherWaitMs));
            Interlocked.Exchange(ref refreshApplyMs, Math.Max(0, applyDurationMs));
        }

        public void RefreshUiSettled(int generation, long elapsedMs)
        {
            if (generation != Volatile.Read(ref refreshGeneration)) return;
            Interlocked.Exchange(ref refreshUiSettleMs, Math.Max(0, elapsedMs));
        }

        public void RefreshCompleted()
        {
            var started = Interlocked.Exchange(ref refreshStarted, 0);
            if (started == 0) return;
            var now = Environment.TickCount64;
            Interlocked.Exchange(ref lastRefreshDuration, Math.Max(0, now - started));
            Interlocked.Exchange(ref lastRefreshCompleted, now);
        }

        public void GridMutationCompleted(string mode, int changed, int rows,
            long dispatcherWaitMs, long applyDurationMs)
        {
            var now = Environment.TickCount64;
            dispatcherWaitMs = Math.Max(0, dispatcherWaitMs);
            applyDurationMs = Math.Max(0, applyDurationMs);
            lock (gridMetricLock)
            {
                //Several small changes can complete between one-second samples. Preserve the
                //slowest recent mutation instead of letting the last trivial repaint hide it.
                if (now - lastGridMutationCompleted <= RecentOperationMs
                    && dispatcherWaitMs + applyDurationMs < gridUiWaitMs + gridApplyMs) return;
                gridMode = mode ?? "";
                gridChanged = Math.Max(0, changed);
                gridRows = Math.Max(0, rows);
                gridUiWaitMs = dispatcherWaitMs;
                gridApplyMs = applyDurationMs;
                lastGridMutationCompleted = now;
            }
        }

        internal static long SmoothDisplayLatency(long previous, long raw)
        {
            previous = Math.Max(0, previous);
            raw = Math.Max(0, raw);
            if (previous == 0 && raw <= 25) return 0;
            //React quickly to a slowdown, decay more gently so consecutive one-second
            //samples read as one trend rather than unrelated flashes.
            var weight = raw >= previous ? 0.65 : 0.25;
            var next = (long)Math.Round(previous + (raw - previous) * weight);
            return raw == 0 && next < 25 ? 0 : next;
        }

        internal static long VisualPipelineLatency(long filesystemReflectionMs,
            long gridReflectionMs, long oldestQueueItemMs)
            => Math.Max(Math.Max(0, gridReflectionMs),
                Math.Max(Math.Max(0, filesystemReflectionMs - ExpectedCoalescingMs),
                    Math.Max(0, oldestQueueItemMs - ExpectedCoalescingMs)));

        public void GridUpdatePending()
            => Interlocked.CompareExchange(ref gridUpdatePendingSince, Environment.TickCount64, 0);

        public void GridUpdateReflected()
        {
            var started = Interlocked.Exchange(ref gridUpdatePendingSince, 0);
            if (started == 0) return;
            Interlocked.Exchange(ref lastGridReflectionLatency,
                Math.Max(0, Environment.TickCount64 - started));
            Interlocked.Exchange(ref lastGridReflectionCompleted, Environment.TickCount64);
        }

        void Loop()
        {
            while (!stop.Wait(SampleIntervalMs))
            {
                try { Sample(); }
                catch { } //Diagnostics must never become an application failure mode.
            }
        }

        void Sample()
        {
            var now = Environment.TickCount64;
            var elapsed = Math.Max(1, now - lastSampleTick);
            if (elapsed > SampleIntervalMs * 5L)
                ResetHeartbeatAfterSleep(); //Sleep/debugger pause is not UI starvation.
            lastSampleTick = now;

            var fs = filesystem();
            var work = workState();
            var dispatcherDelay = Volatile.Read(ref heartbeatPending) != 0
                ? Math.Max(0, now - Interlocked.Read(ref heartbeatQueued))
                : Math.Max(0, Interlocked.Read(ref lastHeartbeatDelay));
            //Read the completed heartbeat before queueing its successor; otherwise every
            //sub-second delay would be replaced by the new heartbeat's zero-millisecond age.
            QueueHeartbeat(now);
            var refreshAt = Interlocked.Read(ref refreshStarted);
            var activeRefreshMs = refreshAt == 0 ? 0 : Math.Max(0, now - refreshAt);
            var refreshRecent = refreshAt != 0
                || now - Interlocked.Read(ref lastRefreshCompleted) <= RecentOperationMs;
            var refreshMs = refreshAt != 0 ? activeRefreshMs
                : refreshRecent ? Interlocked.Read(ref lastRefreshDuration) : 0;
            var gridPendingAt = Interlocked.Read(ref gridUpdatePendingSince);
            var gridPendingAge = gridPendingAt == 0 ? 0 : Math.Max(0, now - gridPendingAt);
            var gridReflection = gridPendingAt != 0
                ? gridPendingAge
                : now - Interlocked.Read(ref lastGridReflectionCompleted) <= 3000
                    ? Interlocked.Read(ref lastGridReflectionLatency) : 0;
            var reflectionLatency = Math.Max(fs.ReflectionLatencyMs, gridReflection);

            process.Refresh();
            var cpuTicks = process.TotalProcessorTime.Ticks;
            var cpuCores = Math.Max(0, cpuTicks - lastCpuTicks) / (double)TimeSpan.TicksPerMillisecond / elapsed;
            lastCpuTicks = cpuTicks;
            var privateBytes = process.PrivateMemorySize64;
            UpdateMemoryBaseline(privateBytes, work.Loading);

            var allocated = GC.GetTotalAllocatedBytes(false);
            var allocationRate = Math.Max(0, allocated - lastAllocated) * 1000d / elapsed / (1024 * 1024);
            lastAllocated = allocated;
            var gc = GC.GetGCMemoryInfo();
            var gcMaxPauseMs = 0d;
            foreach (var pause in gc.PauseDurations)
                gcMaxPauseMs = Math.Max(gcMaxPauseMs, pause.TotalMilliseconds);
            ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
            var pendingWork = ThreadPool.PendingWorkItemCount;
            var threadPoolStarved = availableWorkers <= 1 && pendingWork > 0;
            var dispatcherSample = dispatcherActivity.Snapshot(now);
            var uiThreadSample = uiThreadActivity.Snapshot(elapsed);

            string sampledGridMode;
            int sampledGridChanged;
            int sampledGridRows;
            long sampledGridUiWait;
            long sampledGridApply;
            lock (gridMetricLock)
            {
                var recentGridMutation = now - lastGridMutationCompleted <= RecentOperationMs;
                sampledGridMode = recentGridMutation ? gridMode : "";
                sampledGridChanged = recentGridMutation ? gridChanged : 0;
                sampledGridRows = recentGridMutation ? gridRows : 0;
                sampledGridUiWait = recentGridMutation ? gridUiWaitMs : 0;
                sampledGridApply = recentGridMutation ? gridApplyMs : 0;
            }

            LiveUpdateHealthState previous;
            LiveUpdateHealthState current;
            string reason;
            lock (stateLock)
            {
                previous = machine.State;
                //Once presentation is intentionally suspended its age is expected to grow;
                //recovery is therefore based on dispatcher/queue/refresh health. Delivery
                //resumes in Recovering while the forced catch-up clears the suspended gap.
                var stateReflection = previous is LiveUpdateHealthState.Suspended or LiveUpdateHealthState.Recovering
                    ? 0 : gridPendingAge;
                var observation = new LiveUpdateObservation(dispatcherDelay, stateReflection,
                    fs.QueueDepth, fs.OldestEventAgeMs, activeRefreshMs, threadPoolStarved, work.Loading);
                current = machine.Observe(observation);
                reason = machine.LastReason;
                Volatile.Write(ref suspendAutomaticUpdates,
                    current == LiveUpdateHealthState.Suspended ? 1 : 0);
            }

            var sample = new HealthSample
            {
                Utc = DateTime.UtcNow,
                UiMs = dispatcherDelay,
                UiOperationMs = dispatcherSample.OperationMs,
                UiOperationName = dispatcherSample.Name,
                UiOperationPriority = dispatcherSample.Priority,
                UiOperationActive = dispatcherSample.Active,
                UiPending = dispatcherSample.Pending,
                UiPendingMax = dispatcherSample.MaxPending,
                UiThreadCpuPercent = uiThreadSample.CpuPercent,
                UiThreadState = uiThreadSample.State,
                UiThreadWaitReason = uiThreadSample.WaitReason,
                FsMs = reflectionLatency,
                Queue = fs.QueueDepth,
                QueueOldMs = fs.OldestEventAgeMs,
                QueueRoot = fs.SlowestRoot,
                QueueRootDepth = fs.SlowestQueueDepth,
                QueueWaiting = fs.WaitingQueueDepth,
                QueueActive = fs.ActiveBatchCount,
                QueueActiveMs = fs.ActiveBatchMs,
                QueueStage = fs.ActiveStage,
                QueueStageMs = fs.ActiveStageMs,
                QueuePath = fs.ActivePath,
                BatchMs = fs.BatchDurationMs,
                BatchEvents = fs.BatchEventCount,
                RefreshMs = refreshMs,
                RefreshDebounceMs = refreshRecent ? Interlocked.Read(ref refreshDebounceMs) : 0,
                RefreshQueueMs = refreshRecent ? Interlocked.Read(ref refreshQueueMs) : 0,
                QueryMs = refreshRecent ? Interlocked.Read(ref refreshQueryMs) : 0,
                UiWaitMs = refreshRecent ? Interlocked.Read(ref refreshUiWaitMs) : 0,
                ApplyMs = refreshRecent ? Interlocked.Read(ref refreshApplyMs) : 0,
                UiSettleMs = refreshRecent ? Interlocked.Read(ref refreshUiSettleMs) : 0,
                RefreshRows = refreshRecent ? Volatile.Read(ref refreshRows) : 0,
                Cache = refreshRecent ? Volatile.Read(ref refreshCache) : "",
                GridMode = sampledGridMode,
                GridChanged = sampledGridChanged,
                GridRows = sampledGridRows,
                GridUiWaitMs = sampledGridUiWait,
                GridApplyMs = sampledGridApply,
                CpuCores = Math.Round(cpuCores, 3),
                PrivateMb = privateBytes / (1024 * 1024),
                PrivateDeltaMb = baselinePrivateBytes == 0 ? 0 : (privateBytes - baselinePrivateBytes) / (1024 * 1024),
                HeapMb = GC.GetTotalMemory(false) / (1024 * 1024),
                AllocMbSec = Math.Round(allocationRate, 3),
                GcPausePercent = gc.PauseTimePercentage,
                GcMaxPauseMs = Math.Round(gcMaxPauseMs, 3),
                Threads = process.Threads.Count,
                Handles = process.HandleCount,
                ThreadPoolThreads = ThreadPool.ThreadCount,
                ThreadPoolAvailable = availableWorkers,
                ThreadPoolPending = pendingWork,
                Loading = work.Loading,
                Filtering = work.Filtering,
                Searching = work.Searching,
                State = current
            };
            history.Add(sample);

            var measuredUiLag = Math.Max(dispatcherDelay, dispatcherSample.OperationMs);
            if (uiLagEpisode.Observe(now, measuredUiLag,
                current == LiveUpdateHealthState.Suspended || episodeActive)
                == UiLagEpisodeAction.Write)
            {
                WriteEpisode("ui-lag", "dispatcher-delay",
                    uiLagEpisode.LastDurationMs / 1000d,
                    history.Snapshot(30), recentEvents());
            }

            //Drive queues are isolated. A stalled network share is important evidence, but
            //must not suspend presentation of unrelated healthy drives such as C:. Log one
            //bounded flight-recorder episode for the slow drive and keep delivery running.
            if (!pipelineEpisodeActive
                && fs.OldestEventAgeMs >= LiveUpdateHealthStateMachine.QueueCriticalMs)
            {
                pipelineEpisodeActive = true;
                pipelineEpisodeStarted = now;
                pipelineHealthySamples = 0;
                WriteEpisode("pipeline-stall", "event-backlog", 0,
                    history.Snapshot(), recentEvents());
            }
            else if (pipelineEpisodeActive)
            {
                pipelineHealthySamples = fs.OldestEventAgeMs < LiveUpdateHealthStateMachine.QueueWarningMs
                    ? pipelineHealthySamples + 1 : 0;
                if (pipelineHealthySamples >= LiveUpdateHealthStateMachine.HealthySamplesBeforeClear)
                {
                    WriteEpisode("pipeline-recovery", "event-backlog",
                        Math.Max(0, now - pipelineEpisodeStarted) / 1000d,
                        history.Snapshot(30), recentEvents());
                    pipelineEpisodeActive = false;
                    pipelineHealthySamples = 0;
                }
            }

            //The normal watcher path deliberately holds events for 200 ms to collapse save
            //bursts. It is throughput control, not lag, so do not make an otherwise healthy
            //border flicker yellow. State transitions still use the raw safety measurements.
            var visualGridLatency = work.Loading
                || current is LiveUpdateHealthState.Suspended or LiveUpdateHealthState.Recovering
                ? 0 : gridReflection;
            var visualPipelineLatency = VisualPipelineLatency(fs.ReflectionLatencyMs,
                visualGridLatency, fs.OldestEventAgeMs);
            var rawDisplayLatency = Math.Max(Math.Max(dispatcherDelay, visualPipelineLatency),
                activeRefreshMs);
            smoothedDisplayLatency = SmoothDisplayLatency(smoothedDisplayLatency, rawDisplayLatency);
            var displayLatency = smoothedDisplayLatency;
            QueuePublish(new LiveUpdateHealthSnapshot(current, reason, displayLatency,
                dispatcherDelay, reflectionLatency, fs.QueueDepth, fs.OldestEventAgeMs,
                refreshMs, cpuCores, privateBytes, fs.SlowestRoot, fs.SlowestQueueDepth));

            if (current == LiveUpdateHealthState.Suspended && !episodeActive)
            {
                episodeActive = true;
                episodeStarted = now;
                episodePostSamplesRemaining = 30;
                WriteEpisode("trigger", reason, 0, history.Snapshot(), recentEvents());
            }
            else if (episodeActive && episodePostSamplesRemaining > 0
                && --episodePostSamplesRemaining == 0)
            {
                //One bounded post-trigger snapshot supplies the aftermath without turning
                //an hours-long incident into continuous disk logging.
                WriteEpisode("post-trigger", reason,
                    Math.Max(0, now - episodeStarted) / 1000d,
                    history.Snapshot(30), recentEvents());
            }

            if (previous != LiveUpdateHealthState.Recovering && current == LiveUpdateHealthState.Recovering)
                _ = RunCatchUp();
        }

        async Task RunCatchUp()
        {
            var succeeded = false;
            try { succeeded = await catchUp(); }
            catch { }

            LiveUpdateHealthState current;
            string reason;
            lock (stateLock)
            {
                current = machine.CompleteRecovery(succeeded);
                reason = machine.LastReason;
                Volatile.Write(ref suspendAutomaticUpdates,
                    current == LiveUpdateHealthState.Suspended ? 1 : 0);
            }

            if (episodeActive && current != LiveUpdateHealthState.Suspended)
            {
                var duration = Math.Max(0, Environment.TickCount64 - episodeStarted) / 1000d;
                WriteEpisode("recovery", reason, duration, history.Snapshot(30), recentEvents());
                episodePostSamplesRemaining = 0;
                episodeActive = false;
            }
        }

        void QueueHeartbeat(long now)
        {
            if (dispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref heartbeatPending, 1, 0) != 0)
                return;
            Interlocked.Exchange(ref heartbeatQueued, now);
            var generation = Volatile.Read(ref heartbeatGeneration);
            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    if (generation != Volatile.Read(ref heartbeatGeneration)) return;
                    Interlocked.Exchange(ref lastHeartbeatDelay,
                        Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref heartbeatQueued)));
                    Interlocked.Exchange(ref heartbeatPending, 0);
                }, DispatcherPriority.Input);
            }
            catch { Interlocked.Exchange(ref heartbeatPending, 0); }
        }

        void ResetHeartbeatAfterSleep()
        {
            Interlocked.Increment(ref heartbeatGeneration);
            Interlocked.Exchange(ref heartbeatPending, 0);
            Interlocked.Exchange(ref lastHeartbeatDelay, 0);
        }

        void QueuePublish(LiveUpdateHealthSnapshot snapshot)
        {
            Volatile.Write(ref latestSnapshot, snapshot);
            if (dispatcher.HasShutdownStarted || Interlocked.CompareExchange(ref publishPending, 1, 0) != 0)
                return;
            try
            {
                dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref publishPending, 0);
                    publish(Volatile.Read(ref latestSnapshot));
                }, DispatcherPriority.DataBind);
            }
            catch { Interlocked.Exchange(ref publishPending, 0); }
        }

        void UpdateMemoryBaseline(long privateBytes, bool loading)
        {
            if (loading)
            {
                baselineSamples = 0;
                return;
            }
            if (baselinePrivateBytes == 0)
            {
                if (++baselineSamples >= 30) baselinePrivateBytes = privateBytes;
                return;
            }
            //A later compacting collection can establish a better steady-state floor.
            if (privateBytes < baselinePrivateBytes) baselinePrivateBytes = privateBytes;
        }

        void WriteEpisode(string kind, string reason, double duration, HealthSample[] samples, string[] events)
        {
            try
            {
                var record = new HealthEpisodeRecord
                {
                    Kind = kind,
                    Utc = DateTime.UtcNow,
                    ProcessId = process.Id,
                    AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    Reason = reason,
                    DurationSeconds = Math.Round(duration, 1),
                    RecentEvents = events ?? Array.Empty<string>(),
                    Samples = samples ?? Array.Empty<HealthSample>()
                };
                StorageMaintenance.AppendHealthLog(JsonSerializer.Serialize(record, HealthJson) + Environment.NewLine);
            }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Set();
            if (Thread.CurrentThread != thread) thread.Join(2000);
            dispatcherActivity.Dispose();
            uiThreadActivity.Dispose();
            stop.Dispose();
            process.Dispose();
        }
    }

    /// <summary>
    /// Continuous green-yellow-orange gradient for measured latency. Red is intentionally
    /// reserved for Suspended, so the border never implies data delivery stopped merely
    /// because an otherwise successful update was slow.
    /// </summary>
    public sealed class LiveUpdateHealthBrushConverter : IMultiValueConverter
    {
        readonly Dictionary<(LiveUpdateHealthState State, int LatencyBucket), SolidColorBrush> brushes = new();

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var state = values.Length > 0 && values[0] is LiveUpdateHealthState s
                ? s : LiveUpdateHealthState.Healthy;
            var latency = values.Length > 1 && values[1] is IConvertible value
                ? value.ToDouble(System.Globalization.CultureInfo.InvariantCulture) : 0;
            //Twenty-five milliseconds still gives 81 visibly smooth levels while bounding
            //brush allocations over multi-day runs. Frozen brushes are safe to reuse.
            var bucket = state is LiveUpdateHealthState.Suspended or LiveUpdateHealthState.Recovering
                ? 0 : (int)(Math.Clamp(latency, 0, 2000) / 25);
            var key = (state, bucket);
            lock (brushes)
            {
                if (brushes.TryGetValue(key, out var existing)) return existing;
                var brush = new SolidColorBrush(ColorFor(state, bucket * 25d));
                brush.Freeze();
                return brushes[key] = brush;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();

        internal static Color ColorFor(LiveUpdateHealthState state, double latencyMs)
        {
            if (state == LiveUpdateHealthState.Suspended) return Colors.Red;
            if (state == LiveUpdateHealthState.Recovering) return Colors.DarkOrange;

            var stops = new[]
            {
                (At: 0d, Color: Color.FromRgb(50, 205, 50)),       //LimeGreen
                (At: 250d, Color: Color.FromRgb(124, 205, 50)),
                (At: 500d, Color: Color.FromRgb(255, 215, 0)),     //Gold
                (At: 1000d, Color: Color.FromRgb(255, 140, 0)),    //DarkOrange
                (At: 2000d, Color: Color.FromRgb(255, 69, 0))      //OrangeRed, not red
            };
            var latency = Math.Clamp(latencyMs, stops[0].At, stops[^1].At);
            for (var i = 1; i < stops.Length; i++)
            {
                if (latency > stops[i].At) continue;
                var from = stops[i - 1];
                var to = stops[i];
                var amount = (latency - from.At) / (to.At - from.At);
                byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
                return Color.FromRgb(Mix(from.Color.R, to.Color.R),
                    Mix(from.Color.G, to.Color.G), Mix(from.Color.B, to.Color.B));
            }
            return stops[^1].Color;
        }
    }
}
