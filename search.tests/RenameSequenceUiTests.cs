using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The F2 sequences: F2 held, the change and its value typed, F2 released. They rename
    /// every selected item at once, or set its times.
    /// </summary>
    public class RenameSequenceUiTests
    {
        [AvaloniaTheory]
        [InlineData("e md", "a.md")]           //Change the extension
        [InlineData(". bak", "a.txt.bak")]     //Add one
        [InlineData("f x", "xa.txt")]          //Prefix
        [InlineData("l x", "ax.txt")]          //Postfix
        [InlineData("n zz", "zz.txt")]         //A new name, the extension kept
        public async Task TheSequenceRenamesTheFile(string sequence, string expected)
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            F2(scene, Parse(sequence));

            Assert.Equal(L.Text("RenameDone"), scene.Status);
            Assert.Equal(new[] { expected, "b.txt" }.OrderBy(n => n, StringComparer.Ordinal), Names(scene));
            Assert.Equal("content", File.ReadAllText(Path.Combine(Path.GetDirectoryName(scene.File), expected)));
        }

        [AvaloniaFact]
        public async Task EThenDeleteRemovesTheExtension()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            F2(scene, Key.E, Key.Delete);

            Assert.Equal(new[] { "a", "b.txt" }, Names(scene));
        }

        [AvaloniaFact]
        public async Task ReplaceAndDeleteWorkOnPartsOfTheName()
        {
            using var scene = await UiScene.Open();
            var abc = Path.Combine(Path.GetDirectoryName(scene.File), "abc.txt");
            File.WriteAllText(abc, "abc");
            var row = scene.AddRow(abc);

            scene.ClickRow(row);
            F2(scene, Parse("r b,y"));              //Replace "b" by "y"
            Assert.Contains("ayc.txt", Names(scene));

            scene.ClickRow(scene.AddRow(Path.Combine(Path.GetDirectoryName(abc), "ayc.txt")));
            F2(scene, new[] { Key.Delete }.Concat(UiScene.KeysOf("y")).ToArray()); //Delete "y"
            Assert.Contains("ac.txt", Names(scene));
        }

        [AvaloniaFact]
        public async Task InsertPutsTheTextAtTheTypedPosition()
        {
            using var scene = await UiScene.Open();
            var abc = Path.Combine(Path.GetDirectoryName(scene.File), "abc.txt");
            File.WriteAllText(abc, "abc");
            scene.ClickRow(scene.AddRow(abc));

            F2(scene, new[] { Key.Insert }.Concat(UiScene.KeysOf("1,x")).ToArray());

            Assert.Contains("axbc.txt", Names(scene));
        }

        [AvaloniaFact]
        public async Task EverySelectedFileIsRenamed()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            F2(scene, Parse("e md"));

            Assert.Equal(new[] { "a.md", "b.md" }, Names(scene));
        }

        [AvaloniaFact]
        public async Task AnExistingNameIsNotOverwrittenUnlessOComesFirst()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            F2(scene, Parse("n b"));
            Assert.NotEqual(L.Text("RenameDone"), scene.Status);
            Assert.Equal("content", File.ReadAllText(scene.File));
            Assert.Equal("other", File.ReadAllText(scene.Other));

            F2(scene, Parse("o n b"));
            Assert.Equal(L.Text("RenameDone"), scene.Status);
            Assert.False(File.Exists(scene.File));
            Assert.Equal("content", File.ReadAllText(scene.Other));
        }

        [AvaloniaTheory]
        [InlineData("fromclip.log", "fromclip.log")]
        [InlineData("plain", "plain.txt")]
        public async Task VTakesTheNameFromTheClipboard(string clipboard, string expected)
        {
            using var scene = await UiScene.Open();
            await FileClipboard.SetText(scene.Window, clipboard);
            scene.ClickRow(scene.FileNode);

            F2(scene, Key.V);

            await scene.Until(() => File.Exists(Path.Combine(Path.GetDirectoryName(scene.File), expected)));
            Assert.Equal(new[] { expected, "b.txt" }.OrderBy(n => n, StringComparer.Ordinal), Names(scene));
        }

        [AvaloniaTheory]
        [InlineData(Key.W)]
        [InlineData(Key.A)]
        public async Task TimeSequencesSetTheCurrentTime(Key which)
        {
            using var scene = await UiScene.Open();
            var old = new DateTime(2001, 2, 3, 4, 5, 6);
            File.SetLastWriteTime(scene.File, old);
            File.SetLastAccessTime(scene.File, old);
            scene.ClickRow(scene.FileNode);

            var before = DateTime.Now.AddSeconds(-2);
            F2(scene, which, Key.C);

            Assert.Equal(L.Text("TimeChanged"), scene.Status);
            Assert.True(Time(scene.File, which) >= before);
            //The other time stays
            Assert.Equal(old, Time(scene.File, which == Key.W ? Key.A : Key.W));
        }

        [AvaloniaFact]
        public async Task ATimeOnTheClipboardIsTaken()
        {
            using var scene = await UiScene.Open();
            var time = new DateTime(2020, 1, 2, 3, 4, 5);
            await FileClipboard.SetText(scene.Window, time.ToString("yyyy-MM-dd HH:mm:ss"));
            scene.ClickRow(scene.FileNode);

            F2(scene, Key.W, Key.V);

            await scene.Until(() => scene.Status == L.Text("TimeChanged"));
            Assert.Equal(time, File.GetLastWriteTime(scene.File));
        }

        [AvaloniaFact]
        public async Task ATextThatIsNoTimeIsReportedAndChangesNothing()
        {
            using var scene = await UiScene.Open();
            var old = new DateTime(2001, 2, 3, 4, 5, 6);
            File.SetLastWriteTime(scene.File, old);
            await FileClipboard.SetText(scene.Window, "no time");
            scene.ClickRow(scene.FileNode);

            F2(scene, Key.W, Key.V);

            var dialog = await scene.Dialog<MessageDialog>();
            Assert.Equal(L.Text("Error"), dialog.Title);
            scene.Click(dialog, "OK");
            await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
            Assert.Equal(old, File.GetLastWriteTime(scene.File));
        }

        [AvaloniaFact]
        public async Task ASequenceWithoutItsValueRenamesNothing()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            F2(scene, Key.E);

            Assert.Equal(L.Text("NoRenameValue"), scene.Status);
            Assert.True(File.Exists(scene.File));
        }

        /// <summary>F2 held, the keys tapped, F2 released - the rename runs on the release</summary>
        static void F2(UiScene scene, params Key[] keys) => scene.Hold(Key.F2, keys);

        /// <summary>"e md" - the command key, then the typed value</summary>
        static Key[] Parse(string sequence) => sequence.Split(' ')
            .SelectMany(part => part switch
            {
                "e" => new[] { Key.E },
                "f" => new[] { Key.F },
                "l" => new[] { Key.L },
                "n" => new[] { Key.N },
                "o" => new[] { Key.O },
                "r" => new[] { Key.R },
                "." => new[] { Key.OemPeriod },
                _ => UiScene.KeysOf(part)
            }).ToArray();

        static string[] Names(UiScene scene) => Directory.GetFileSystemEntries(Path.GetDirectoryName(scene.File))
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        static DateTime Time(string path, Key which) =>
            which == Key.W ? File.GetLastWriteTime(path) : File.GetLastAccessTime(path);
    }
}
