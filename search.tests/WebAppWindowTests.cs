using Xunit;

namespace search.Tests
{
    public class WebAppWindowTests
    {
        [Theory]
        [InlineData("127.0.2651.64", true)]
        [InlineData("127.0.2651.74", true)]
        [InlineData("140.0.3485.44", true)]
        [InlineData("127.0.2651.63", false)]
        [InlineData("126.0.2592.113", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void FileHandoffNeedsARuntimeWithFileSystemHandleApis(string runtimeVersion, bool supported)
            => Assert.Equal(supported, WebAppWindow.SupportsFileHandoff(runtimeVersion));

        [Fact]
        public void ZoomIsKeptPerAppAndDefaultsToTheCompactOne()
        {
            var page = $"zoom-test-{System.Guid.NewGuid():N}.html";
            var other = $"zoom-test-{System.Guid.NewGuid():N}.html";
            Assert.Equal(WebAppWindow.DefaultZoom, ZoomStore.Load(page));

            ZoomStore.Save(page, 1.1);

            Assert.Equal(1.1, ZoomStore.Load(page));
            Assert.Equal(WebAppWindow.DefaultZoom, ZoomStore.Load(other));
        }
    }
}
