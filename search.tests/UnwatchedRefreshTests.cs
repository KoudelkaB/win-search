using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>Drives without live change tracking (Linux) are refreshed by a rescan</summary>
    public class UnwatchedRefreshTests
    {
        static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        static readonly TimeSpan Age = TimeSpan.FromMinutes(2);

        static string[] Refresh(Dictionary<string, DateTime> scannedAt,
            Func<string, bool> unwatched = null, Dictionary<string, DateTime> running = null,
            Func<string, bool> waitingForRetry = null)
            => SearchModel.DrivesToRefresh(scannedAt, running ?? new Dictionary<string, DateTime>(),
                Now, Age, unwatched ?? (_ => true), waitingForRetry ?? (_ => false));

        [Fact]
        public void OnlyAnUnwatchedDriveOlderThanTheAgeIsRescanned()
        {
            var scannedAt = new Dictionary<string, DateTime>
            {
                ["/old"] = Now - Age,
                ["/fresh"] = Now - Age + TimeSpan.FromSeconds(1),
                ["/watched"] = Now - TimeSpan.FromHours(1),
                ["/offline"] = Now - TimeSpan.FromHours(1)
            };

            var refreshed = Refresh(scannedAt, unwatched: root => root != "/watched",
                waitingForRetry: root => root == "/offline");

            Assert.Equal(new[] { "/old" }, refreshed);
        }

        [Fact]
        public void ARecentlyStartedScanCoversTheRefresh()
        {
            var refreshed = Refresh(new Dictionary<string, DateTime> { ["/"] = Now - TimeSpan.FromHours(1) },
                running: new Dictionary<string, DateTime> { ["/"] = Now - TimeSpan.FromSeconds(5) });

            Assert.Empty(refreshed);
        }

        [Fact]
        public void AScanStartedTooLongAgoGetsARerun()
        {
            //A slow walk may have passed a folder before the folder changed - the request
            //makes the running scan rerun once it ends
            var refreshed = Refresh(new Dictionary<string, DateTime> { ["/"] = Now - TimeSpan.FromHours(1) },
                running: new Dictionary<string, DateTime> { ["/"] = Now - Age });

            Assert.Equal(new[] { "/" }, refreshed);
        }

        [Fact]
        public void AFirstScanIsJudgedByItsStartToo()
        {
            //No completed scan yet - a long first walk (a cold disk) gets its rerun, a fresh
            //one (the startup scans) does not
            var refreshed = Refresh(new Dictionary<string, DateTime>(),
                running: new Dictionary<string, DateTime>
                {
                    ["/slow"] = Now - Age,
                    ["/starting"] = Now - TimeSpan.FromSeconds(5)
                });

            Assert.Equal(new[] { "/slow" }, refreshed);
        }

        [Fact]
        public void AClockSetBackDoesNotHoldTheRefreshOff()
        {
            var refreshed = Refresh(new Dictionary<string, DateTime> { ["/"] = Now + TimeSpan.FromHours(1) });

            Assert.Equal(new[] { "/" }, refreshed);
        }

        [Fact]
        public void NothingUnwatchedHidesTheStatus()
        {
            var (status, tooltip) = SearchModel.UnwatchedStatusTexts(
                Array.Empty<KeyValuePair<string, DateTime>>(), DateTime.Now);

            Assert.Null(status);
            Assert.Null(tooltip);
        }

        [Fact]
        public void TheStatusShowsTheOldestIndexAndTheTooltipEveryDrive()
        {
            var localNow = DateTime.Now;
            //Halfway since midnight - earlier today whenever the test runs
            var older = localNow.Date.AddTicks((localNow - localNow.Date).Ticks / 2).ToUniversalTime();
            var newer = localNow.ToUniversalTime();
            var (status, tooltip) = SearchModel.UnwatchedStatusTexts(new Dictionary<string, DateTime>
            {
                ["/huge"] = newer,
                ["/"] = older
            }, localNow);

            Assert.Contains(older.ToLocalTime().ToString("t"), status);
            Assert.DoesNotContain(older.ToLocalTime().ToString("d"), status); //Today => the time alone
            Assert.Contains("/, /huge", tooltip);
            Assert.Contains(((int)SearchModel.UnwatchedRefreshAge.TotalMinutes).ToString(), tooltip);
        }

        [Fact]
        public void AnIndexFromAnotherDayShowsItsDate()
        {
            var localNow = DateTime.Now;
            var yesterday = localNow.AddDays(-1).ToUniversalTime();

            var (status, _) = SearchModel.UnwatchedStatusTexts(
                new Dictionary<string, DateTime> { ["/"] = yesterday }, localNow);

            Assert.Contains(yesterday.ToLocalTime().ToString("g"), status);
        }

        [Fact]
        public void ExtractedContentIsReportedParentsFirstWithoutFollowingLinks()
        {
            var root = Path.Combine(Path.GetTempPath(), "extracted-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "a", "b"));
                File.WriteAllText(Path.Combine(root, "a", "b", "f.txt"), "x");
                File.WriteAllText(Path.Combine(root, "top.txt"), "x");
                var expected = new List<string>
                {
                    Path.Combine(root, "a"),
                    Path.Combine(root, "a", "b"),
                    Path.Combine(root, "a", "b", "f.txt"),
                    Path.Combine(root, "top.txt")
                };
                if (!OperatingSystem.IsWindows()) //Creating links there needs extra rights
                {
                    //A link back to the top: the recursive managed enumeration would cycle
                    Directory.CreateSymbolicLink(Path.Combine(root, "a", "loop"), root);
                    expected.Add(Path.Combine(root, "a", "loop"));
                }

                var content = search.Models.Extensions.CreatedContent(root);

                Assert.Equal(expected.OrderBy(p => p, StringComparer.Ordinal), content);
                for (var i = 0; i < content.Length; i++)
                {
                    var parent = Path.GetDirectoryName(content[i]);
                    if (parent != root) Assert.InRange(Array.IndexOf(content, parent), 0, i - 1);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
