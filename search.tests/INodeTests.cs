using search.Models;
using Xunit;

namespace search.Tests
{
    public class INodeTests
    {
        [Fact]
        public void AddSizeDeltaAccumulates()
        {
            var n = new FileNode();
            n.AddSizeDelta(100);
            n.AddSizeDelta(23);
            Assert.Equal(123ul, n.Size);
            n.AddSizeDelta(-23);
            Assert.Equal(100ul, n.Size);
            n.AddSizeDelta(0);
            Assert.Equal(100ul, n.Size);
        }

        [Fact]
        public void AddSizeDeltaSaturatesAtZeroInsteadOfWrapping()
        {
            var n = new FileNode();
            n.AddSizeDelta(100);
            // A double-subtraction (e.g. a delete event replayed) must not wrap to exabytes
            n.AddSizeDelta(-101);
            Assert.Equal(0ul, n.Size);
            n.AddSizeDelta(long.MinValue + 1);
            Assert.Equal(0ul, n.Size);
        }

        [Fact]
        public void AddSizeDeltaHandlesLargeSizes()
        {
            var n = new FileNode();
            n.AddSizeDelta(long.MaxValue);
            Assert.Equal((ulong)long.MaxValue, n.Size);
            n.AddSizeDelta(-long.MaxValue);
            Assert.Equal(0ul, n.Size);
        }
    }
}
