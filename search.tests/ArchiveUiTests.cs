using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace search.Tests
{
    /// <summary>Packing the selection into a zip, unpacking an archive, an archive as a target</summary>
    public class ArchiveUiTests
    {
        [AvaloniaFact]
        public async Task ZPacksTheSelectionIntoAZipNamedAfterTheFirstFile()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.Z);

            var zip = scene.File + ".zip";
            await scene.Until(() => scene.Status == L.Format("CreatedItem", zip));
            Assert.Equal(new[] { "a.txt", "b.txt" }, Entries(zip));
            Assert.Equal("content", Read(zip, "a.txt"));
            //Packing keeps the files
            Assert.True(File.Exists(scene.File) && File.Exists(scene.Other));
        }

        [AvaloniaFact]
        public async Task AFolderIsPackedWithItsContent()
        {
            using var scene = await UiScene.Open();
            File.WriteAllText(Path.Combine(scene.Folder, "inner.txt"), "inside");
            scene.ClickRow(scene.FolderNode);

            scene.Keys(Key.Z);

            var zip = scene.Folder + ".zip";
            await scene.Until(() => scene.Status == L.Format("CreatedItem", zip));
            Assert.Equal("inside", Read(zip, Entries(zip).Single(e => e.EndsWith("inner.txt"))));
        }

        [AvaloniaFact]
        public async Task ASecondZipOfTheSameFileGetsANewName()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.Z);
            await scene.Until(() => File.Exists(scene.File + ".zip"));
            await scene.Until(() => scene.Status == L.Format("CreatedItem", scene.File + ".zip"));

            scene.ClickRow(scene.FileNode);
            scene.Keys(Key.Z);

            await scene.Until(() => Directory.GetFiles(Path.GetDirectoryName(scene.File), "*.zip").Length == 2);
        }

        [AvaloniaFact]
        public async Task TheMenuZipsToo()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode, button: MouseButton.Right);

            scene.Choose(scene.Grid.ContextMenu, L.Text("Zip"));

            var zip = scene.File + ".zip";
            await scene.Until(() => scene.Status == L.Format("CreatedItem", zip));
            Assert.Equal(new[] { "a.txt" }, Entries(zip));
        }

        [AvaloniaFact]
        public async Task UUnpacksTheArchiveIntoAFolderBesideIt()
        {
            using var scene = await UiScene.Open();
            var zip = Pack(scene, "pack.zip");
            var row = scene.AddRow(zip);
            scene.ClickRow(row);

            scene.Keys(Key.U);

            var folder = zip + "_";
            await scene.Until(() => File.Exists(Path.Combine(folder, "sub", "two.txt")));
            Assert.Equal("1", File.ReadAllText(Path.Combine(folder, "one.txt")));
            Assert.Equal("2", File.ReadAllText(Path.Combine(folder, "sub", "two.txt")));
            Assert.True(File.Exists(zip));
        }

        [AvaloniaFact]
        public async Task TheMenuOffersUnzipForAnArchiveAndUnpacksIt()
        {
            using var scene = await UiScene.Open();
            var row = scene.AddRow(Pack(scene, "pack.zip"));
            scene.ClickRow(row, button: MouseButton.Right);

            scene.Choose(scene.Grid.ContextMenu, L.Text("Unzip"));

            await scene.Until(() => File.Exists(Path.Combine(row.FullName + "_", "one.txt")));
        }

        [AvaloniaFact]
        public async Task ADamagedArchiveLeavesNoHalfUnpackedFolder()
        {
            using var scene = await UiScene.Open();
            var broken = Path.Combine(scene.Root, "broken.zip");
            File.WriteAllText(broken, "this is no archive");
            scene.ClickRow(scene.AddRow(broken));

            scene.Keys(Key.U);
            //Unpacking runs on the thread pool; give it the time it takes for a real archive
            await Task.Delay(500);
            scene.Settle();

            Assert.False(Directory.Exists(broken + "_"));
        }

        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AnArchiveTargetTakesTheDroppedFileAfterAsking(bool confirmed)
        {
            using var scene = await UiScene.Open();
            var zip = Pack(scene, "pack.zip");
            var target = await scene.AddTarget(zip);
            Assert.Equal(BasketTargetKind.Archive, target.Kind);

            scene.DropAt(scene.Center(scene.TargetButton(target)), await scene.Files(scene.File));
            scene.Click(await scene.Dialog<MessageDialog>(), confirmed ? "OK" : "Cancel");

            if (confirmed)
                await scene.Until(() => Entries(zip).Contains("a.txt"));
            else
            {
                await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
                Assert.DoesNotContain("a.txt", Entries(zip));
            }
            Assert.True(File.Exists(scene.File));
        }

        /// <summary>A zip of one.txt and sub/two.txt in the test's folder</summary>
        static string Pack(UiScene scene, string name)
        {
            var zip = Path.Combine(scene.Root, name);
            using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
            void Add(string entry, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
                writer.Write(text);
            }
            Add("one.txt", "1");
            Add("sub/two.txt", "2");
            return zip;
        }

        static string[] Entries(string zip)
        {
            using var archive = ZipFile.OpenRead(zip);
            return archive.Entries.Where(e => e.Name.Length > 0)
                .Select(e => e.FullName.Replace('\\', '/')).OrderBy(e => e).ToArray();
        }

        static string Read(string zip, string entry)
        {
            using var archive = ZipFile.OpenRead(zip);
            using var reader = new StreamReader(archive.Entries.Single(e => e.FullName.Replace('\\', '/') == entry).Open());
            return reader.ReadToEnd();
        }
    }
}
