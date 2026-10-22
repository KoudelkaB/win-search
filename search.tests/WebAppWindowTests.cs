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
            => Assert.Equal(supported, WebAppWindow.SupportsFileHandoff(runtimeVersion));

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

        static System.Text.Json.JsonElement LogExplorerOptions(string[] files, LogExplorerLoad load)
            => System.Text.Json.JsonDocument.Parse(WebAppWindow.MessageFor(Apps.LogExplorer, files, load))
                .RootElement.GetProperty("logExplorer");

        [Fact]
        public void LogExplorerGetsTheFullPathsAndAsksHowToCombineWithoutALoad()
        {
            var options = LogExplorerOptions(new[] { @"C:\logs\a.log", @"D:\b.log" }, null);

            Assert.Equal(new[] { @"C:\logs\a.log", @"D:\b.log" },
                options.GetProperty("paths").EnumerateArray().Select(x => x.GetString()));
            Assert.Equal(System.Text.Json.JsonValueKind.Null, options.GetProperty("combine").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, options.GetProperty("filter").ValueKind);
        }

        [Fact]
        public void LogExplorerGetsTheFilesAppendedWithTheFilter()
        {
            var options = LogExplorerOptions(new[] { @"C:\a.log" }, new LogExplorerLoad("a\"b", true));
            Assert.Equal("append", options.GetProperty("combine").GetString());
            Assert.Equal("a\"b", options.GetProperty("filter").GetProperty("text").GetString());
            Assert.True(options.GetProperty("filter").GetProperty("caseSensitive").GetBoolean());

            var hex = LogExplorerOptions(new[] { @"C:\a.log" }, new LogExplorerLoad(null, false));
            Assert.Equal("append", hex.GetProperty("combine").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, hex.GetProperty("filter").ValueKind);
        }

        static INode File(string name, ulong size)
            => new FileNode(@"C:\" + name, new NodeMetadataSnapshot(false, size, System.DateTime.MinValue));

        [Fact]
        public void LogExplorerAsksByTotalSizeAndFileCount()
        {
            var half = MainWindow.LogExplorerConfirmBytes / 2;
            Assert.False(MainWindow.NeedsLogExplorerConfirmation(new[] { File("a.log", half), File("b.log", half) }, out _, out var bytes));
            Assert.Equal(MainWindow.LogExplorerConfirmBytes, bytes);

            Assert.True(MainWindow.NeedsLogExplorerConfirmation(
                new[] { File("a.log", half), File("b.log", half), File("c.log", 1) }, out var count, out _));
            Assert.Equal(3, count);

            var many = Enumerable.Range(0, MainWindow.LogExplorerConfirmFiles + 1).Select(i => File($"{i}.log", 10)).ToArray();
            Assert.True(MainWindow.NeedsLogExplorerConfirmation(many, out _, out _));
        }

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
                Assert.EndsWith("/LogExplorer.html?lang=zh-Hans", WebAppWindow.UrlFor(Apps.LogExplorer));
            }
            finally { System.Globalization.CultureInfo.CurrentUICulture = previous; }
        }
    }
}
