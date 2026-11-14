using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// A paste the file system refuses: the window says what failed, and the files are where
    /// and as they were - nothing half-copied in the target, nothing lost from the source.
    /// A move denied on Windows offers the elevated helper first: the scene records that
    /// start instead of a UAC prompt, and the move then fails as declined.
    /// </summary>
    public class FailedOperationUiTests
    {
        [AvaloniaTheory]
        [InlineData(Key.C, FileTransferAction.Copy)]
        [InlineData(Key.X, FileTransferAction.Move)]
        public async Task APasteIntoAFolderThatTakesNoFilesReportsItAndKeepsTheSource(Key key, FileTransferAction action)
        {
            using var scene = await UiScene.Open();
            using var _ = Unwritable(scene.Folder);
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(key);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            scene.ClickRow(scene.FolderNode);
            scene.Ctrl(Key.V);
            await scene.Confirm(action);
            await scene.Idle();

            AssertReported(scene, "a.txt");
            Assert.Equal("content", File.ReadAllText(scene.File));
            Assert.False(File.Exists(Path.Combine(scene.Folder, "a.txt")));
        }

        /// <summary>Windows: a file another program holds open without sharing cannot be moved</summary>
        [AvaloniaFact]
        public async Task MovingAFileHeldOpenByAnotherProgramReportsItAndKeepsTheFile()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux locks are advisory: an open file moves");
            using var scene = await UiScene.Open();
            scene.ClickRow(scene.FileNode);
            scene.Ctrl(Key.X);
            await scene.Until(() => FileClipboard.LastSetFiles.Count == 1);

            using (new FileStream(scene.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                scene.ClickRow(scene.FolderNode);
                scene.Ctrl(Key.V);
                await scene.Confirm(FileTransferAction.Move);
                await scene.Idle();
            }

            AssertReported(scene, "a.txt");
            Assert.Equal("content", File.ReadAllText(scene.File));
            Assert.False(File.Exists(Path.Combine(scene.Folder, "a.txt")));
        }

        /// <summary>The status reports the failure and names the file</summary>
        static void AssertReported(UiScene scene, string name)
        {
            //"Copy: Completed with errors (1): …" - the operation, then the errors
            var template = L.Text("CompletedWithErrors");
            var lead = template[..template.IndexOf('{')];
            Assert.True(scene.Status.Contains(lead, StringComparison.Ordinal) && scene.Status.Contains(name),
                $"Status: {scene.Status}; windows: {string.Join(", ", scene.Window.OwnedWindows.Where(w => w.IsVisible).Select(w => w.GetType().Name + " " + w.Title))}");
        }

        /// <summary>
        /// The folder takes no new entries until disposed: a deny rule for this user on Windows,
        /// no write permission on Linux (skipped as root, which writes anyway)
        /// </summary>
        static IDisposable Unwritable(string folder)
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(folder);
                var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData | FileSystemRights.AppendData,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
                var security = info.GetAccessControl();
                security.AddAccessRule(rule);
                info.SetAccessControl(security);
                return new Undo(() =>
                {
                    var current = info.GetAccessControl();
                    current.RemoveAccessRuleSpecific(rule);
                    info.SetAccessControl(current);
                });
            }
            Assert.SkipWhen(Environment.UserName == "root", "root writes into any folder");
            var mode = File.GetUnixFileMode(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            return new Undo(() => File.SetUnixFileMode(folder, mode));
        }

        sealed class Undo(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }
    }
}
