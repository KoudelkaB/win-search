using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Texts put together from runs. White space between the run elements of the markup is
    /// text as well: runs written one per line read "Selected : 1  · 1/3".
    /// </summary>
    public class InlineTextTests
    {
        static string TextOf(TextBlock block)
            => string.Concat(block.Inlines.Select(inline => inline is Run run ? run.Text : "?"));

        [AvaloniaFact]
        public void TheSelectedCountReadsWithoutStraySpaces()
        {
            var window = new MainWindow(startFileSystem: false);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var text = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Inlines?.OfType<Run>().Any(run => run.Name == "selectedCountRun") == true);

                Assert.Equal($"{L.Text("Selected")}: 0", TextOf(text));
            }
            finally { window.Close(); }
        }

        [AvaloniaFact]
        public void ATargetShowsItsIconAndNameOneSpaceApart()
        {
            var window = new MainWindow(startFileSystem: false);
            var target = new BasketTarget { Path = System.IO.Path.GetTempPath(), Kind = BasketTargetKind.Folder };
            window.BasketTargets.Clear();
            window.BasketTargets.Add(target);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var button = window.FindControl<ItemsControl>("basketItems").GetVisualDescendants().OfType<Button>()
                    .Single(b => ReferenceEquals(b.Tag, target));
                var text = button.GetVisualDescendants().OfType<TextBlock>().Single();

                Assert.Equal($"{target.Icon} {target.Name}", TextOf(text));
            }
            finally { window.Close(); }
        }

        [Theory]
        [InlineData(1L << 40, "1024 GB")]
        [InlineData(5L << 30, "5 GB")]
        [InlineData((1L << 30) - 1, "1023 MB")] //A /boot partition: not "0 GB"
        [InlineData(600L << 20, "600 MB")]
        [InlineData(1000, "1 MB")]
        public void ASmallVolumeShowsItsSizeInMegabytes(long bytes, string expected)
            => Assert.Equal(expected, DrivesWindow.SizeText(bytes));
    }
}
