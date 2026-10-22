using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    public class PinnedFilterAppearanceTests
    {
        [AvaloniaFact]
        public void InactivePinsKeepTheSameBackgroundBeforeAndAfterUse()
        {
            var window = new MainWindow(startFileSystem: false);
            var logs = new PinnedFilter { Name = "logs", Filter = ".log:|.txt" };
            var exe = new PinnedFilter { Name = "exe", Filter = ".exe:" };
            window.PinnedFilters.Clear();
            window.PinnedFilters.Add(logs);
            window.PinnedFilters.Add(exe);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var pins = window.FindControl<ItemsControl>("pinnedItems");
                Button ButtonFor(PinnedFilter pin) => pins.GetVisualDescendants().OfType<Button>()
                    .Single(b => ReferenceEquals(b.Tag, pin));

                var normal = Background(ButtonFor(logs));
                Assert.NotNull(normal);
                Assert.Equal(normal, Background(ButtonFor(exe)));
                Use(ButtonFor(logs));
                Assert.Equal(logs.Filter, window.FilterText);
                var active = Background(ButtonFor(logs));
                Assert.NotEqual(normal, active);
                Use(ButtonFor(exe));
                Assert.Equal(exe.Filter, window.FilterText);
                Assert.Equal(normal, Background(ButtonFor(logs)));
                Assert.Equal(active, Background(ButtonFor(exe)));

                var json = new PinnedFilter { Name = "json", Filter = ".json:" };
                window.PinnedFilters.Add(json);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(normal, Background(ButtonFor(json)));
                Use(ButtonFor(json));
                Assert.Equal(active, Background(ButtonFor(json)));
                Use(ButtonFor(logs));
                Assert.Equal(normal, Background(ButtonFor(json)));
                Assert.Equal(normal, Background(ButtonFor(exe)));
            }
            finally { window.Close(); }
        }

        static Color? Background(Button button) =>
            (button.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(p => p.Name == "PART_ContentPresenter").Background as ISolidColorBrush)?.Color;

        static void Use(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
    }
}
