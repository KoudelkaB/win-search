using System.Linq;
using Avalonia;
using search.Models;
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
        {
            Assert.SkipWhen(!System.OperatingSystem.IsWindows() && !string.IsNullOrEmpty(runtimeVersion),
                "comparing WebView2 versions needs the WebView2 loader, which exists on Windows only");
            Assert.Equal(supported, WebAppWindow.SupportsFileHandoff(runtimeVersion));
        }

        [Fact]
        public void LayoutIsKeptPerAppAndDefaultsToTheCompactZoom()
        {
            var page = $"layout-test-{System.Guid.NewGuid():N}.html";
            var other = $"layout-test-{System.Guid.NewGuid():N}.html";
            Assert.Equal(WebAppWindow.DefaultZoom, WebAppLayoutStore.Load(page).Zoom);
            Assert.Equal(0, WebAppLayoutStore.Load(page).Width);

            WebAppLayoutStore.Update(page, l => l.Zoom = 1.1);
            WebAppLayoutStore.Update(page, l => { l.Width = 900; l.Height = 600; l.Maximized = true; });

            var saved = WebAppLayoutStore.Load(page);
            Assert.Equal((1.1, 900d, 600d, true), (saved.Zoom, saved.Width, saved.Height, saved.Maximized));
            Assert.Equal(WebAppWindow.DefaultZoom, WebAppLayoutStore.Load(other).Zoom);
        }

        [Fact]
        public void CalculatorGrowsForExtendedModesAndShrinksBackWithoutKeepingTheOldHeight()
        {
            var screen = new Rect(0, 0, 1920, 1400);
            var frame = new Size(16, 39);
            var previous = new Rect(300, 300, 1280, 860);
            var standard = WebAppWindow.CalculatorBoundsFor(new Size(924, 1011), .8, frame, previous, screen);
            var scientific = WebAppWindow.CalculatorBoundsFor(new Size(924, 1296), .8, frame, standard, screen);
            var programmer = WebAppWindow.CalculatorBoundsFor(new Size(924, 1409), .8, frame, scientific, screen);
            var restored = WebAppWindow.CalculatorBoundsFor(new Size(924, 1011), .8, frame, programmer, screen);

            Assert.InRange(standard.Width, 750, 760);
            Assert.True(scientific.Height > standard.Height);
            Assert.True(programmer.Height > scientific.Height);
            Assert.Equal(standard.Size, restored.Size);
            Assert.True(screen.Contains(programmer));
        }

        [Fact]
        public void CalculatorStaysInsideTheCurrentMonitorEvenWhenZoomedOrNearItsEdge()
        {
            var screen = new Rect(-1280, -100, 1280, 984);
            var result = WebAppWindow.CalculatorBoundsFor(new Size(924, 1409), 1.5, new Size(16, 39),
                new Rect(-300, 700, 760, 860), screen);

            Assert.Equal(screen, result);
        }

        [Fact]
        public void CalculatorContentUsesBrowserZoomAndAddsTheWindowFrameOnlyOnce()
        {
            var content = new Size(924, 1011);
            var screen = new Rect(0, 0, 3840, 2160);
            var result = WebAppWindow.CalculatorBoundsFor(content, 1.25, new Size(16, 39),
                new Rect(50, 70, 760, 860), screen);

            Assert.Equal(new Point(50, 70), result.TopLeft);
            Assert.Equal(new Size(1171, 1303), result.Size);
        }

        [Fact]
        public void LinuxDialogSizesTheCalculatorAtTheZoomTheSizeWasMeasuredWith()
        {
            static (int, int)? Size(string json, double zoom)
                => WebAppWindow.DialogCalculatorSize(System.Text.Json.JsonDocument.Parse(json).RootElement, zoom);

            // A zoom message the host has not handled yet must not change the result
            Assert.Equal((1107, 1196), Size("""{"calculatorSize":{"width":924,"height":1000,"zoom":1.18}}""", 1.07));
            Assert.Equal((1005, 1086), Size("""{"calculatorSize":{"width":924,"height":1000}}""", 1.07));
            Assert.Null(Size("""{"zoom":1.18}""", 1.07));
            Assert.Null(Size("""{"calculatorSize":{"width":0,"height":1000,"zoom":1.18}}""", 1.07));
        }

        [Fact]
        public void OnlyTheHexEditorAndTheCalculatorArePages()
        {
            Assert.True(WebAppWindow.IsWebApp(Apps.HexEditor));
            Assert.True(WebAppWindow.IsWebApp(Apps.Calculator));
            //Log explorer is a program of its own (NativeLogExplorer)
            Assert.False(WebAppWindow.IsWebApp(Apps.LogExplorer));
        }

        [Fact]
        public void ThePageLearnsWhereToFetchTheFilesItsHandlesCannotRead()
        {
            var plain = System.Text.Json.Nodes.JsonNode.Parse(WebAppWindow.MessageFor(Apps.HexEditor))!.AsObject();
            var message = System.Text.Json.Nodes.JsonNode.Parse(
                WebAppWindow.WithFallback(WebAppWindow.MessageFor(Apps.HexEditor), "https://host/files/x/"))!.AsObject();

            Assert.Equal("https://host/files/x/", (string)message["fallback"]);
            message.Remove("fallback");
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(plain, message), "the rest of the message is kept");
        }

        [Theory]
        [InlineData("0", 2, 0)]
        [InlineData("1", 2, 1)]
        [InlineData("2", 2, null)]
        [InlineData("-1", 2, null)]
        [InlineData("+1", 2, null)]
        [InlineData("1?x", 2, null)]
        [InlineData("../0", 2, null)]
        [InlineData("", 2, null)]
        [InlineData("99999999999", 2, null)]
        public void OnlyTheWindowsOwnFilesAreServed(string rest, int count, int? index)
            => Assert.Equal(index, WebAppWindow.FileIndex(rest, count));

        static INode File(string name, ulong size)
            => new FileNode(@"C:\" + name, new NodeMetadataSnapshot(false, size, System.DateTime.MinValue));


        [Fact]
        public void ShowInFilesTakesMatchesBeyondTheResultWindowToo()
        {
            INode shownMatch = File("z.log", 1), shownMiss = File("y.log", 1), beyondB = File("b.log", 1), beyondA = File("a.log", 1);
            var results = new System.Collections.Generic.Dictionary<INode, bool?>
            {
                [beyondB] = true, [shownMiss] = false, [shownMatch] = true, [beyondA] = true
            };

            var found = SearchModel.FoundFiles(new[] { shownMiss, shownMatch }, results);

            // Shown rows keep the list order, the rest follow by path
            Assert.Equal(new[] { shownMatch, beyondA, beyondB }, found);
            Assert.Empty(SearchModel.FoundFiles(new[] { shownMatch }, new System.Collections.Generic.Dictionary<INode, bool?>()));
        }

        [Fact]
        public void PagesOpenInTheApplicationLanguage()
        {
            var previous = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-Hans");
                Assert.EndsWith("/calculator.html?lang=zh-Hans", WebAppWindow.UrlFor(Apps.Calculator));
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
        }
    }
}
