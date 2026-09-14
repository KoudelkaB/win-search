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
    }
}
