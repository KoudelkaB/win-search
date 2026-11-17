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
            _ = work.Track(first.Task);
            var settled = work.Settled();
            Assert.False(settled.IsCompleted);

            // A newer filter arrives while the search waits for the first one
            var second = new TaskCompletionSource();
            _ = work.Track(second.Task);
            first.SetResult();
            await Task.Delay(50, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(settled.IsCompleted);

            second.SetException(new InvalidOperationException("update failed"));
            await settled.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken); // A failed update does not fail the search
        }
    }
}
