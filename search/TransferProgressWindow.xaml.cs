using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace search
{
    public partial class TransferProgressWindow : Window
    {
        readonly CancellationTokenSource cancellation = new();
        readonly Stopwatch elapsed = new();
        readonly DispatcherTimer timer;
        TimeSpan? showDelay;
        long totalWork = 1;
        long displayedWork;
        Func<long> sampleObservedWork;
        Func<string> sampleObservedItem;
        long observationCompletedWork;
        long observationMaximumWork;
        string observationItem;
        bool started;
        bool completed;
        bool finalizing;
        bool cancellationAvailable = true;

        public TransferProgressWindow(string operation)
        {
            InitializeComponent();
            Operation.Text = operation;
            CurrentItem.Text = "Preparing…";
            Progress.IsIndeterminate = true;
            timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(500),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    SampleObservation();
                    UpdateTiming();
                    TryShow();
                },
                Dispatcher);
            timer.Start();
        }

        public CancellationToken Token => cancellation.Token;

        public void SetCancellationAvailable(bool available, string unavailableReason = null)
        {
            cancellationAvailable = available;
            CancelButton.IsEnabled = available;
            CancelButton.ToolTip = available ? null : unavailableReason;
        }

        public void ShowAfter(TimeSpan delay)
        {
            showDelay = delay;
            TryShow();
        }

        void TryShow()
        {
            if (!completed && !IsVisible && showDelay.HasValue
                && elapsed.Elapsed >= showDelay.Value)
            {
                showDelay = null;
                Show();
            }
        }

        public void Begin(long total)
        {
            totalWork = Math.Max(1, total);
            displayedWork = 0;
            finalizing = false;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            ProgressText.Text = "0%";
            BytesText.Text = $"Completed: 0 B of {FormatBytes(totalWork)}";
            RemainingText.Text = "Remaining: estimating…";
            started = true;
            elapsed.Restart();
            UpdateTiming();
        }

        public void Report(long completedCount, string currentItem)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(
                    () => Report(completedCount, currentItem),
                    DispatcherPriority.Background);
                return;
            }

            sampleObservedWork = null;
            sampleObservedItem = null;
            finalizing = false;
            Display(completedCount, currentItem);
            UpdateTiming();
        }

        public void Observe(
            Func<long> sampleWork,
            long completedWork,
            long maximumWork,
            string currentItem,
            Func<string> sampleItem = null)
        {
            sampleObservedWork = sampleWork;
            sampleObservedItem = sampleItem;
            observationCompletedWork = completedWork;
            observationMaximumWork = Math.Max(1, maximumWork);
            observationItem = currentItem;
            SampleObservation();
        }

        void SampleObservation()
        {
            if (sampleObservedWork == null)
                return;
            long sampled;
            string currentItem;
            try
            {
                sampled = Math.Clamp(sampleObservedWork(), 0, observationMaximumWork);
                currentItem = sampleObservedItem?.Invoke() ?? observationItem;
            }
            catch
            {
                return;
            }
            var combined = observationCompletedWork > long.MaxValue - sampled
                ? long.MaxValue
                : observationCompletedWork + sampled;
            finalizing = combined >= totalWork;
            combined = CapIncompleteProgress(combined, totalWork);
            Display(combined, currentItem);
        }

        internal static long CapIncompleteProgress(long observed, long total) =>
            observed >= total ? total > 1 ? total - 1 : 0 : Math.Max(0, observed);

        void Display(long completedCount, string currentItem)
        {
            // A queued worker-thread report can arrive after the UI thread has already
            // displayed the operation's final value. Never let the bar move backwards.
            if (completedCount < displayedWork)
                return;

            displayedWork = Math.Clamp(completedCount, 0, totalWork);
            var percentage = 100d * displayedWork / totalWork;
            Progress.Value = finalizing ? 99 : percentage;
            ProgressText.Text = finalizing ? "99%" : $"{Math.Floor(percentage):0}%";
            BytesText.Text = $"Completed: {FormatBytes(finalizing ? totalWork : displayedWork)}"
                + $" of {FormatBytes(totalWork)}";
            CurrentItem.Text = currentItem;
        }

        public void PauseTiming()
        {
            if (started)
                elapsed.Stop();
        }

        public void ResumeTiming()
        {
            if (started)
                elapsed.Start();
        }

        public void Complete()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(Complete);
                return;
            }
            if (completed)
                return;
            completed = true;
            showDelay = null;
            timer.Stop();
            elapsed.Stop();
            if (IsVisible)
                Close();
        }

        void UpdateTiming()
        {
            ElapsedText.Text = $"Elapsed: {FormatDuration(elapsed.Elapsed)}";
            var remainingBytes = Math.Max(0, totalWork - displayedWork);
            if (!started)
            {
                RemainingText.Text = $"Remaining: calculating… · {FormatBytes(remainingBytes)}";
                return;
            }
            if (displayedWork >= totalWork)
            {
                RemainingText.Text = "Remaining: 0:00 · 0 B";
                return;
            }
            if (finalizing)
            {
                RemainingText.Text = "Finalizing… · 0 B remaining";
                return;
            }
            if (displayedWork == 0 || elapsed.Elapsed < TimeSpan.FromSeconds(1))
            {
                RemainingText.Text = $"Remaining: estimating… · {FormatBytes(remainingBytes)}";
                return;
            }

            var remainingSeconds = elapsed.Elapsed.TotalSeconds
                * (totalWork - displayedWork) / displayedWork;
            RemainingText.Text = double.IsFinite(remainingSeconds)
                ? $"Remaining: about {FormatDuration(TimeSpan.FromSeconds(
                    Math.Min(remainingSeconds, TimeSpan.FromDays(999).TotalSeconds)))}"
                    + $" · {FormatBytes(remainingBytes)}"
                : $"Remaining: estimating… · {FormatBytes(remainingBytes)}";
        }

        internal static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1)
                return $"{(int)duration.TotalDays}.{duration:hh\\:mm\\:ss}";
            if (duration.TotalHours >= 1)
                return duration.ToString(@"h\:mm\:ss");
            return duration.ToString(@"m\:ss");
        }

        internal static string FormatBytes(long bytes)
        {
            var value = Math.Max(0, bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            var unit = 0;
            var display = (double)value;
            while (display >= 1024 && unit < units.Length - 1)
            {
                display /= 1024;
                unit++;
            }
            return unit == 0
                ? $"{value} {units[unit]}"
                : $"{display.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!cancellationAvailable)
                return;
            cancellation.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (completed)
                return;
            if (!cancellationAvailable)
            {
                e.Cancel = true;
                return;
            }
            cancellation.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
            e.Cancel = true;
        }
    }
}
