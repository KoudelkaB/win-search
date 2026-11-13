using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace search.Tests
{
    /// <summary>The filter box and the pinned filters under it</summary>
    public class FilterAndPinsUiTests
    {
        [AvaloniaFact]
        public async Task TypedTextIsTheFilterAtOnce()
        {
            using var scene = await UiScene.Open();
            var filter = Filter(scene);

            scene.Click(filter);
            scene.Type("logs");

            Assert.Equal("logs", filter.Text);
            Assert.Equal("logs", scene.Window.FilterText);
        }

        [AvaloniaFact]
        public async Task PinOffersANameFromTheFilterAndEnterKeepsTheTypedOne()
        {
            using var scene = await UiScene.Open();
            scene.Click(Filter(scene));
            scene.Type("logs");

            scene.Click(scene.ButtonOf(L.Text("Pin")));
            var editor = await Editor(scene);
            Assert.Equal("logs", editor.Text);
            Assert.True(editor.IsKeyboardFocusWithin);

            scene.Retype("My logs");
            scene.Keys(Key.Enter);

            var pinned = Assert.Single(scene.Window.PinnedFilters);
            Assert.Equal("My logs", pinned.Name);
            Assert.Equal("logs", pinned.Filter);
            Assert.False(pinned.IsEditing);
            Assert.True(Filter(scene).IsKeyboardFocusWithin);
            Assert.Equal("My logs", PinButton(scene, pinned).Content);
        }

        [AvaloniaFact]
        public async Task AnEmptyFilterPinsAsAllFiles()
        {
            using var scene = await UiScene.Open();

            scene.Click(scene.ButtonOf(L.Text("Pin")));

            Assert.Equal(L.Text("AllFiles"), (await Editor(scene)).Text);
            scene.Keys(Key.Escape);
        }

        [AvaloniaFact]
        public async Task EscapeDropsAPinThatWasNeverNamed()
        {
            using var scene = await UiScene.Open();
            scene.Click(scene.ButtonOf(L.Text("Pin")));
            await Editor(scene);

            scene.Keys(Key.Escape);

            Assert.Empty(scene.Window.PinnedFilters);
            Assert.True(Filter(scene).IsKeyboardFocusWithin);
        }

        [AvaloniaFact]
        public async Task ANameAlreadyUsedKeepsTheEditorOpenAndMarked()
        {
            using var scene = await UiScene.Open();
            await Pin(scene, "logs", "Work");

            scene.Click(Filter(scene));
            scene.Retype("temp");
            scene.Click(scene.ButtonOf(L.Text("Pin")));
            var editor = await Editor(scene);
            scene.Retype("work");
            Assert.Equal(Brushes.IndianRed, editor.BorderBrush);
            scene.Keys(Key.Enter);

            Assert.Equal(L.Text("PinnedNameExists"), scene.Status);
            Assert.True(editor.IsVisible);
            Assert.Equal(2, scene.Window.PinnedFilters.Count);
            Assert.True(scene.Window.PinnedFilters[1].IsEditing);

            //Fixed, the same Enter keeps it
            scene.Retype("Temp");
            scene.Keys(Key.Enter);
            Assert.False(scene.Window.PinnedFilters[1].IsEditing);
        }

        [AvaloniaFact]
        public async Task AClickOnAPinAppliesItsFilter()
        {
            using var scene = await UiScene.Open();
            var pinned = await Pin(scene, "logs", "Work");
            scene.Click(Filter(scene));
            scene.Retype("something else");

            scene.Click(PinButton(scene, pinned));

            Assert.Equal("logs", Filter(scene).Text);
            Assert.True(Filter(scene).IsKeyboardFocusWithin);
        }

        [AvaloniaFact]
        public async Task ThePinMenuUpdatesRenamesAndUnpins()
        {
            using var scene = await UiScene.Open();
            var pinned = await Pin(scene, "logs", "Work");
            scene.Click(Filter(scene));
            scene.Retype("newer");

            Menu(scene, scene.Window.PinnedFilters[0], "UpdateFilter");
            pinned = Assert.Single(scene.Window.PinnedFilters);
            Assert.Equal("newer", pinned.Filter);
            Assert.Equal("Work", pinned.Name);

            Menu(scene, pinned, "Rename");
            var editor = await Editor(scene);
            Assert.Equal("Work", editor.Text);
            scene.Retype("Play");
            scene.Keys(Key.Enter);
            Assert.Equal("Play", pinned.Name);

            //A rename given up keeps the old name - only a draft disappears
            Menu(scene, pinned, "Rename");
            await Editor(scene);
            scene.Retype("Never");
            scene.Keys(Key.Escape);
            Assert.Equal("Play", Assert.Single(scene.Window.PinnedFilters).Name);

            Menu(scene, pinned, "Unpin");
            Assert.Empty(scene.Window.PinnedFilters);
        }

        [AvaloniaFact]
        public async Task AltPPinsFromTheFilterBoxAndFromTheResults()
        {
            using var scene = await UiScene.Open();

            scene.Click(Filter(scene));
            scene.Keys(Key.LeftAlt, Key.P);
            await Editor(scene);
            scene.Retype("From filter");
            scene.Keys(Key.Enter);
            Assert.Single(scene.Window.PinnedFilters);

            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.LeftAlt, Key.P);
            await Editor(scene);
            scene.Retype("From results");
            scene.Keys(Key.Enter);
            Assert.Equal(new[] { "From filter", "From results" }, scene.Window.PinnedFilters.Select(p => p.Name));
        }

        [AvaloniaFact]
        public async Task PinsSurviveIntoTheNextWindow()
        {
            using var scene = await UiScene.Open();
            await Pin(scene, "logs", "Work");

            var next = new MainWindow(startFileSystem: false);
            try
            {
                var pinned = Assert.Single(next.PinnedFilters);
                Assert.Equal("Work", pinned.Name);
                Assert.Equal("logs", pinned.Filter);
            }
            finally { next.Close(); }
        }

        static AutoCompleteTextBox Filter(UiScene scene) => scene.Find<AutoCompleteTextBox>("filterTextBox");

        static async Task<PinnedFilter> Pin(UiScene scene, string filter, string name)
        {
            scene.Click(Filter(scene));
            scene.Retype(filter);
            scene.Click(scene.ButtonOf(L.Text("Pin")));
            await Editor(scene);
            scene.Retype(name);
            scene.Keys(Key.Enter);
            return scene.Window.PinnedFilters.Last();
        }

        static async Task<TextBox> Editor(UiScene scene)
        {
            TextBox Focused() => scene.Find<ItemsControl>("pinnedItems").GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.IsEffectivelyVisible && t.IsKeyboardFocusWithin);
            await scene.Until(() => Focused() != null);
            scene.Settle();
            return Focused();
        }

        static Button PinButton(UiScene scene, PinnedFilter pinned)
        {
            //Filters used before are suggested in a list that drops over the pins - the
            //first click would only close it, as it does for a user
            if (Filter(scene).IsListOpen) scene.Keys(Key.Escape);
            Assert.False(Filter(scene).IsListOpen);
            scene.Settle();
            return scene.Find<ItemsControl>("pinnedItems").GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.Tag, pinned));
        }

        static void Menu(UiScene scene, PinnedFilter pinned, string item)
        {
            var button = PinButton(scene, pinned);
            scene.Click(button, MouseButton.Right);
            scene.Choose(button.ContextMenu, L.Text(item));
        }
    }
}
