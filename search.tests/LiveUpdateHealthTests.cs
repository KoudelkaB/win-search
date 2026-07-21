using System;
using System.Linq;
using System.Windows.Media;
using Xunit;

namespace search.Tests
{
    public class LiveUpdateHealthTests
    {
        static readonly LiveUpdateObservation Healthy = new(10, 200, 0, 0, 0, false);

        [Fact]
        public void SustainedLatencySuspendsAndAQuietWindowRequestsOneCatchUp()
        {
            var health = new LiveUpdateHealthStateMachine();
            var delayed = new LiveUpdateObservation(600, 200, 0, 0, 0, false);

            Assert.Equal(LiveUpdateHealthState.Delayed, health.Observe(delayed));
            Assert.Equal(LiveUpdateHealthState.Delayed, health.Observe(delayed));
            Assert.Equal(LiveUpdateHealthState.Suspended, health.Observe(delayed));
            Assert.Equal("sustained-latency", health.LastReason);

            for (var i = 1; i < LiveUpdateHealthStateMachine.HealthySamplesBeforeRecovery; i++)
                Assert.Equal(LiveUpdateHealthState.Suspended, health.Observe(Healthy));
            Assert.Equal(LiveUpdateHealthState.Recovering, health.Observe(Healthy));
            Assert.Equal(LiveUpdateHealthState.Healthy, health.CompleteRecovery(succeeded: true));
        }

        [Fact]
        public void OneCriticalDelaySuspendsImmediatelyAndFailedCatchUpStaysSuspended()
        {
            var health = new LiveUpdateHealthStateMachine();
            var blocked = Healthy with
            {
                DispatcherDelayMs = LiveUpdateHealthStateMachine.DispatcherCriticalMs
            };

            Assert.Equal(LiveUpdateHealthState.Suspended, health.Observe(blocked));
            Assert.Equal("dispatcher-delay", health.LastReason);
            for (var i = 0; i < LiveUpdateHealthStateMachine.HealthySamplesBeforeRecovery; i++)
                health.Observe(Healthy);
            Assert.Equal(LiveUpdateHealthState.Recovering, health.State);
            Assert.Equal(LiveUpdateHealthState.Suspended, health.CompleteRecovery(succeeded: false));
            Assert.Equal("catch-up-failed", health.LastReason);
        }

        [Fact]
        public void ShortLatencySpikeReturnsToHealthyWithoutSuspending()
        {
            var health = new LiveUpdateHealthStateMachine();
            var delayed = Healthy with { ReflectionLatencyMs = LiveUpdateHealthStateMachine.ReflectionWarningMs };

            Assert.Equal(LiveUpdateHealthState.Delayed, health.Observe(delayed));
            for (var i = 0; i < LiveUpdateHealthStateMachine.HealthySamplesBeforeClear; i++)
                health.Observe(Healthy);

            Assert.Equal(LiveUpdateHealthState.Healthy, health.State);
        }

        [Fact]
        public void OneSlowDriveStaysDelayedWithoutSuspendingOtherDrives()
        {
            var health = new LiveUpdateHealthStateMachine();
            var slowDrive = Healthy with
            {
                QueueDepth = 25,
                OldestEventAgeMs = LiveUpdateHealthStateMachine.QueueCriticalMs * 2
            };

            for (var i = 0; i < 20; i++)
                Assert.Equal(LiveUpdateHealthState.Delayed, health.Observe(slowDrive));
            Assert.Equal("elevated-latency", health.LastReason);

            for (var i = 0; i < LiveUpdateHealthStateMachine.HealthySamplesBeforeClear; i++)
                health.Observe(Healthy);
            Assert.Equal(LiveUpdateHealthState.Healthy, health.State);
        }

        [Fact]
        public void ExpectedDriveLoadingDoesNotCreateFalseReflectionEpisodes()
        {
            var health = new LiveUpdateHealthStateMachine();
            var expectedLoading = new LiveUpdateObservation(10,
                LiveUpdateHealthStateMachine.ReflectionCriticalMs * 2,
                0, 0, LiveUpdateHealthStateMachine.RefreshCriticalMs * 2,
                false, Loading: true);

            for (var i = 0; i < 5; i++)
                Assert.Equal(LiveUpdateHealthState.Healthy, health.Observe(expectedLoading));

            //Actual UI starvation is unhealthy even while a drive is loading.
            Assert.Equal(LiveUpdateHealthState.Suspended, health.Observe(expectedLoading with
            {
                DispatcherDelayMs = LiveUpdateHealthStateMachine.DispatcherCriticalMs
            }));
        }

        [Fact]
        public void FlightRecorderKeepsOnlyTheNewestBoundedHistory()
        {
            var history = new FixedRing<int>(3);
            foreach (var value in Enumerable.Range(1, 5)) history.Add(value);

            Assert.Equal(3, history.Capacity);
            Assert.Equal(3, history.Count);
            Assert.Equal(new[] { 3, 4, 5 }, history.Snapshot());
            Assert.Equal(new[] { 4, 5 }, history.Snapshot(2));
        }

        [Fact]
        public void BorderUsesAContinuousGradientButReservesRedForSuspension()
        {
            var green = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Healthy, 0);
            var between = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Delayed, 375);
            var gold = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Delayed, 500);
            var orange = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Delayed, 1000);
            var slow = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Delayed, 5000);
            var red = LiveUpdateHealthBrushConverter.ColorFor(LiveUpdateHealthState.Suspended, 0);

            Assert.Equal(Color.FromRgb(50, 205, 50), green);
            Assert.NotEqual(green, between);
            Assert.NotEqual(gold, between); //375 ms is interpolated, not a discrete band
            Assert.Equal(Color.FromRgb(255, 215, 0), gold);
            Assert.Equal(Color.FromRgb(255, 140, 0), orange);
            Assert.NotEqual(Colors.Red, slow);
            Assert.Equal(Colors.Red, red);
        }

        [Fact]
        public void NormalWatcherCoalescingDoesNotMakeTheBorderLookDelayed()
        {
            Assert.Equal(3, LiveUpdateHealthMonitor.VisualPipelineLatency(203, 0, 188));
            Assert.Equal(600, LiveUpdateHealthMonitor.VisualPipelineLatency(203, 600, 188));
            Assert.Equal(400, LiveUpdateHealthMonitor.VisualPipelineLatency(600, 0, 188));
        }

        [Fact]
        public void DisplayLatencyRisesQuicklyAndDecaysWithoutFlashing()
        {
            var first = LiveUpdateHealthMonitor.SmoothDisplayLatency(0, 1000);
            var second = LiveUpdateHealthMonitor.SmoothDisplayLatency(first, 1000);
            var falling = LiveUpdateHealthMonitor.SmoothDisplayLatency(second, 0);

            Assert.InRange(first, 600, 700);
            Assert.True(second > first);
            Assert.InRange(falling, 1, second - 1);
            Assert.Equal(0, LiveUpdateHealthMonitor.SmoothDisplayLatency(0, 20));
        }

        [Fact]
        public void HealthLogHasASeparateSmallDiskBudget()
        {
            Assert.True(StorageMaintenance.MaxHealthLogBytes < StorageMaintenance.MaxLogBytes);
            Assert.True(StorageMaintenance.HealthLogBackupCount < StorageMaintenance.LogBackupCount);
            Assert.Equal(120, LiveUpdateHealthMonitor.HistorySamples);
            Assert.Equal(4, new HealthEpisodeRecord().Version);
        }
    }
}
