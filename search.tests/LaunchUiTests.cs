using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// What starts other programs - the file's own application, the file manager, a shell,
    /// a dropped-on program - and the built-in web apps. The starts are recorded, nothing runs.
    /// </summary>
    public class LaunchUiTests
    {
        #region The file's application and the file manager

        [AvaloniaFact]
        public async Task CtrlRightClickOpensTheFileInItsApplication()
        {
            using var scene = await UiScene.Open();

            scene.ClickRow(scene.FileNode, button: MouseButton.Right, held: RawInputModifiers.Control);

            await scene.Until(() => scene.Launched.Count > 0);
            Assert.True(UiScene.Mentions(Assert.Single(scene.Launched), scene.File));
            Assert.False(scene.Grid.ContextMenu.IsOpen);
        }

        [AvaloniaFact]
        public async Task EnterOnFilesOpensEachOfThem()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.Enter);

            await scene.Until(() => scene.Launched.Count == 2);
            Assert.Contains(scene.Launched, s => UiScene.Mentions(s, scene.File));
            Assert.Contains(scene.Launched, s => UiScene.Mentions(s, scene.Other));
        }

        [AvaloniaFact]
        public async Task TheMenuOpensTheSelection()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);
            scene.ClickRow(scene.OtherNode, button: MouseButton.Right);

            scene.Choose(scene.Grid.ContextMenu, L.Text("Open"));

            await scene.Until(() => scene.Launched.Count == 2);
        }

        [AvaloniaFact]
        public async Task TheMenuShowsTheFileInTheFileManager()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode, button: MouseButton.Right);

            scene.Choose(scene.Grid.ContextMenu, L.Text("OpenFolder"));

            await scene.Until(() => scene.Launched.Any(s => UiScene.Mentions(s, scene.File)));
        }

        [AvaloniaFact]
        public async Task DoubleClickOnTheFolderCellShowsTheFileInTheFileManager()
        {
            using var scene = await UiScene.Open();
            var at = scene.Center(scene.CellBorder(scene.FileNode, "Folder"));

            scene.Click(at);
            scene.Click(at);

            await scene.Until(() => scene.Launched.Any(s => UiScene.Mentions(s, scene.File)));
            //The filter stays: only the Name cell filters into a folder
            Assert.True(string.IsNullOrEmpty(scene.Find<AutoCompleteTextBox>("filterTextBox").Text));
        }

        [AvaloniaFact]
        public async Task OThenBShowsEverySelectedItemInTheFileManager()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.FolderNode, held: RawInputModifiers.Control);

            scene.Keys(Key.O, Key.B);

            await scene.Until(() => scene.Launched.Any(s => UiScene.Mentions(s, scene.File))
                && scene.Launched.Any(s => UiScene.Mentions(s, scene.Folder)));
        }

        [AvaloniaFact]
        public async Task F4EditsTheFileInATextEditor()
        {
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.F4);

            await scene.Until(() => scene.Launched.Count > 0);
            var start = Assert.Single(scene.Launched);
            Assert.True(UiScene.Mentions(start, scene.File));
            //An editor gets the file - the file is not run itself
            Assert.NotEqual(scene.File, start.FileName, StringComparer.OrdinalIgnoreCase);
        }

        [AvaloniaFact]
        public async Task OThenSOpensAShellInTheFolderOfTheFile()
        {
            using var scene = await UiScene.Open();
            var folder = Path.GetDirectoryName(scene.File);
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.O, Key.S);

            await scene.Until(() => scene.Launched.Count > 0
                || scene.Window.OwnedWindows.OfType<MessageDialog>().Any(d => d.IsVisible));
            if (scene.Launched.Count == 0)
            {
                //Linux without any terminal emulator says so
                Assert.False(OperatingSystem.IsWindows());
                scene.Window.OwnedWindows.OfType<MessageDialog>().Single().Close();
                return;
            }
            var start = Assert.Single(scene.Launched);
            Assert.True(UiScene.Mentions(start, folder));
            if (OperatingSystem.IsWindows()) Assert.Equal(folder, start.WorkingDirectory, ignoreCase: true);
        }

        #endregion

        #region Programs as drop targets

        [AvaloniaFact]
        public async Task AFileDroppedOnAProgramRowStartsItWithTheFile()
        {
            using var scene = await UiScene.Open();
            var tool = Program(scene);
            var row = scene.AddRow(tool);
            var at = scene.Center(scene.CellBorder(row, "Name"));
            var data = await scene.Files(scene.File, scene.Other);

            scene.Drag(Avalonia.Input.Raw.RawDragEventType.DragEnter, at, data);
            Assert.Equal(DragDropEffects.Copy, scene.Drag(Avalonia.Input.Raw.RawDragEventType.DragOver, at, data));
            scene.Drag(Avalonia.Input.Raw.RawDragEventType.Drop, at, data);

            var start = Assert.Single(scene.Launched);
            Assert.Equal(tool, start.FileName, ignoreCase: true);
            Assert.Equal(new[] { scene.File, scene.Other }, start.ArgumentList);
            Assert.Equal(Path.GetDirectoryName(tool), start.WorkingDirectory, ignoreCase: true);
            Assert.Equal(L.Format("ProgramStarted", tool, 2), scene.Status);
            //Nothing is transferred to a program
            Assert.Empty(scene.Window.OwnedWindows);
        }

        [AvaloniaTheory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task AProgramTargetRunsWithTheFilesAfterAsking(bool confirmed)
        {
            using var scene = await UiScene.Open();
            var tool = Program(scene);
            var target = await scene.AddTarget(tool);
            Assert.Equal(BasketTargetKind.Executable, target.Kind);

            scene.DropAt(scene.Center(scene.TargetButton(target)), await scene.Files(scene.File));
            var question = await scene.Dialog<MessageDialog>();
            scene.Click(question, confirmed ? "OK" : "Cancel");

            await scene.Until(() => !scene.Window.OwnedWindows.Any(w => w.IsVisible));
            scene.Settle();
            if (!confirmed)
            {
                Assert.Empty(scene.Launched);
                return;
            }
            var start = Assert.Single(scene.Launched);
            Assert.Equal(tool, start.FileName, ignoreCase: true);
            Assert.Equal(new[] { scene.File }, start.ArgumentList);
        }

        /// <summary>A program in the test's folder: a batch file on Windows, an executable script on Linux</summary>
        static string Program(UiScene scene)
        {
            var windows = OperatingSystem.IsWindows();
            var tool = Path.Combine(scene.Root, windows ? "tool.cmd" : "tool.sh");
            File.WriteAllText(tool, windows ? "@echo off\r\n" : "#!/bin/sh\n");
            if (!windows)
                File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return tool;
        }

        #endregion

        #region Built-in web apps

        [AvaloniaFact]
        public async Task HOpensTheHexEditorWithTheSelectedFiles()
        {
            using var scene = await UiScene.Open();
            Assert.SkipUnless(WebAppWindow.IsAvailable, "No web view on this machine");
            scene.ClickRow(scene.FileNode);
            scene.ClickRow(scene.OtherNode, held: RawInputModifiers.Control);

            scene.Keys(Key.H);

            var app = await WebApp(scene);
            Assert.Equal(Apps.HexEditor, app.Page);
            Assert.Equal(new[] { scene.File, scene.Other }, app.Files.OrderBy(f => f));
        }

        [AvaloniaFact]
        public async Task LOpensTheLogExplorerWithoutAFilter()
        {
            using var scene = await UiScene.Open();
            Assert.SkipUnless(WebAppWindow.IsAvailable, "No web view on this machine");
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.L);

            var app = await WebApp(scene);
            Assert.Equal(Apps.LogExplorer, app.Page);
            Assert.Equal(new[] { scene.File }, app.Files);
            Assert.Null(app.Load);
        }

        [AvaloniaFact]
        public async Task F3ViewsATextFileInTheLogExplorer()
        {
            using var scene = await UiScene.Open();
            Assert.SkipUnless(WebAppWindow.IsAvailable, "No web view on this machine");
            scene.ClickRow(scene.FileNode);

            scene.Keys(Key.F3);

            var app = await WebApp(scene);
            Assert.Equal(Apps.LogExplorer, app.Page);
            Assert.Equal(new[] { scene.File }, app.Files);
        }

        [AvaloniaTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShowInFilesOpensTheFoundFilesFilteredToTheSearchedText(bool button)
        {
            using var scene = await UiScene.Open();
            Assert.SkipUnless(WebAppWindow.IsAvailable, "No web view on this machine");
            var model = (SearchModel)scene.Window.DataContext;
            scene.Click(scene.Find<AutoCompleteTextBox>("findTextBox"));
            scene.Type("content");
            scene.Keys(Key.Enter);
            await scene.Until(() => !model.Searching && model.HasFound);

            if (button) scene.Click(scene.Find<Avalonia.Controls.Button>("showInFilesButton"));
            else scene.Keys(Key.G);

            var app = await WebApp(scene);
            Assert.Equal(Apps.LogExplorer, app.Page);
            Assert.Equal(new[] { scene.File }, app.Files);
            Assert.Equal(new LogExplorerLoad("content", CaseSensitive: true), app.Load);
        }

        [AvaloniaFact]
        public async Task AltKOpensTheCalculatorFromAnywhere()
        {
            using var scene = await UiScene.Open();
            Assert.SkipUnless(WebAppWindow.IsBrowserAvailable, "No web view on this machine");
            scene.Click(scene.Find<AutoCompleteTextBox>("filterTextBox"));

            scene.Keys(Key.LeftAlt, Key.K);

            Assert.Equal(Apps.Calculator, (await WebApp(scene)).Page);
            //Typing went on in the box: Alt+K typed nothing into it
            Assert.True(string.IsNullOrEmpty(scene.Find<AutoCompleteTextBox>("filterTextBox").Text));
        }

        static async Task<(string Page, string[] Files, LogExplorerLoad Load)> WebApp(UiScene scene)
        {
            await scene.Until(() => scene.WebApps.Count > 0);
            Assert.Empty(scene.Launched);
            return Assert.Single(scene.WebApps);
        }

        #endregion
    }
}
