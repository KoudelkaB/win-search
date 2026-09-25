using System;
using System.Threading.Tasks;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class LatestWorkTests
    {
        [Fact]
        public async Task SettlesOnlyAfterTheNewestWorkEvenWhenItSupersedesTheAwaitedOne()
        {
            var work = new LatestWork();
            Assert.True(work.Settled().IsCompleted);

            var first = new TaskCompletionSource();
            work.Track(first.Task);
            var settled = work.Settled();
            Assert.False(settled.IsCompleted);

            // A newer filter arrives while the search waits for the first one
            var second = new TaskCompletionSource();
            work.Track(second.Task);
            first.SetResult();
            await Task.Delay(50);
            Assert.False(settled.IsCompleted);

            second.SetException(new InvalidOperationException("update failed"));
            await settled.WaitAsync(TimeSpan.FromSeconds(5)); // A failed update does not fail the search
        }
    }
}
