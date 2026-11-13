using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>The search box: text looked for inside the files of the results</summary>
    public class ContentSearchUiTests
    {
        [AvaloniaFact]
        public async Task EnterSearchesTheFilesAndHandsTheKeyboardToTheResults()
        {
            using var scene = await UiScene.Open();
            var model = (SearchModel)scene.Window.DataContext;
            var show = scene.Find<Button>("showInFilesButton");
            Assert.False(show.IsEnabled);

            await Search(scene, "content");

            Assert.True(scene.Grid.IsKeyboardFocusWithin);
            Assert.True(model.FoundIn(scene.FileNode));
            Assert.False(model.FoundIn(scene.OtherNode));
            Assert.Null(model.FoundIn(scene.FolderNode));
            scene.Settle();
            Assert.True(show.IsEnabled);
        }

        [AvaloniaFact]
        public async Task SelectSequencesPickTheRowsByTheirSearchResult()
        {
            using var scene = await UiScene.Open();
            await Search(scene, "content");

            scene.Keys(Key.S, Key.G);
            Assert.Same(scene.FileNode, Assert.Single(scene.Selected));

            scene.Keys(Key.S, Key.R);
            Assert.Same(scene.OtherNode, Assert.Single(scene.Selected));

            scene.Keys(Key.S, Key.B);
            Assert.Same(scene.FolderNode, Assert.Single(scene.Selected));
        }

        [AvaloniaFact]
        public async Task TheMenuOffersTheFoundFilesOnlyAfterASearch()
        {
            using var scene = await UiScene.Open();
            await Search(scene, "content");

            scene.ClickRow(scene.OtherNode, button: MouseButton.Right);
            scene.Choose(scene.Grid.ContextMenu, L.Text("SelectFound"));

            Assert.Same(scene.FileNode, Assert.Single(scene.Selected));
        }

        [AvaloniaFact]
        public async Task CaseMattersUnlessTheBoxIsChecked()
        {
            using var scene = await UiScene.Open();
            var model = (SearchModel)scene.Window.DataContext;

            await Search(scene, "CONTENT", found: false);
            Assert.False(model.FoundIn(scene.FileNode));

            scene.Click(scene.Find<CheckBox>("caseInsensitive"));
            Assert.True(scene.Find<CheckBox>("caseInsensitive").IsChecked);
            await Search(scene, "CONTENT");
            Assert.True(model.FoundIn(scene.FileNode));
        }

        [AvaloniaFact]
        public async Task TypingOnInTheSearchBoxDropsTheOldResult()
        {
            using var scene = await UiScene.Open();
            var model = (SearchModel)scene.Window.DataContext;
            await Search(scene, "content");

            scene.Click(scene.Find<AutoCompleteTextBox>("findTextBox"));
            scene.Keys(Key.Back);

            await scene.Until(() => !model.HasFound && model.FoundIn(scene.FileNode) == null);
            scene.Settle();
            Assert.False(scene.Find<Button>("showInFilesButton").IsEnabled);
        }

        [AvaloniaFact]
        public async Task SwitchingTheEncodingRewritesTheTypedTextAndBack()
        {
            using var scene = await UiScene.Open();
            var find = scene.Find<AutoCompleteTextBox>("findTextBox");
            var encoding = scene.Find<ComboBox>("encodingComboBox");
            scene.Click(find);
            scene.Type("AB");

            encoding.SelectedIndex = 2;
            scene.Settle();
            Assert.Equal("4142", new string(find.Text.Where(char.IsLetterOrDigit).ToArray()));

            encoding.SelectedIndex = 0;
            scene.Settle();
            Assert.Equal("AB", find.Text);
        }

        [AvaloniaFact]
        public async Task HexBytesFindTheSameFile()
        {
            using var scene = await UiScene.Open();
            var model = (SearchModel)scene.Window.DataContext;
            var find = scene.Find<AutoCompleteTextBox>("findTextBox");
            scene.Click(find);
            scene.Type("content");
            scene.Find<ComboBox>("encodingComboBox").SelectedIndex = 2;
            scene.Settle();
            Assert.NotEqual("content", find.Text);

            scene.Click(find);
            scene.Keys(Key.Enter);

            await scene.Until(() => model.HasFound);
            Assert.True(model.FoundIn(scene.FileNode));
            Assert.False(model.FoundIn(scene.OtherNode));
        }

        static async Task Search(UiScene scene, string text, bool found = true)
        {
            var model = (SearchModel)scene.Window.DataContext;
            scene.Click(scene.Find<AutoCompleteTextBox>("findTextBox"));
            scene.Retype(text);
            scene.Keys(Key.Enter);
            //Finished - its last refresh of the rows would close a menu opened too soon - and
            //every file has its answer; the folder never gets one
            await scene.Until(() => model.FoundIn(scene.FileNode) != null && model.FoundIn(scene.OtherNode) != null
                && !model.Searching && model.HasFound == found);
        }
    }
}
