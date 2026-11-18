#if WINDOWS_HOST
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace search.Smoke
{
    /// <summary>
    /// What a user changes is there again in a new process over the same data folder: the
    /// pinned filter, the targets, a drive taken out of the index
    /// </summary>
    public sealed class WindowsRestartSmokeTests
    {
        static readonly TimeSpan Exit = TimeSpan.FromSeconds(30);

        public WindowsRestartSmokeTests() => Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows desktop only");

        [Fact]
        public async Task PinnedFilterAndTargetComeBackInANewProcess()
        {
            var first = new WindowsApp { KeepFolders = true };
            WindowsApp second = null;
            try
            {
                await first.InitializeAsync();
                var before = Path.GetFileName(first.Before);
                await first.ShowOnly(first.Token, names => names.Contains(before));
                PinButton(first).Invoke();
                //The new pin opens with its suggested name in an editor: Enter keeps it
                await Task.Delay(500, cancellationToken: TestContext.Current.CancellationToken);
                first.Press(VirtualKeyShort.RETURN);
                await WindowsApp.WaitUntil(() => first.ItemsOf("pinnedItems").Any(n => n.Contains(first.Token)),
                    TimeSpan.FromSeconds(10), "the pinned filter");
                //A target is a folder: this run's work folder (a file gets "No new valid targets to add")
                await first.SelectRow(first.Token);
                first.Press(VirtualKeyShort.ALT, VirtualKeyShort.KEY_N); //Alt+N: the selection as a target
                await WindowsApp.WaitUntil(() => first.ItemsOf("basketItems").Any(n => n.Contains(first.Token)),
                    TimeSpan.FromSeconds(10), "the work folder as a target");
                var pinned = first.ItemsOf("pinnedItems");
                var targets = first.ItemsOf("basketItems");
                Assert.Equal(0, first.CloseMainWindow(Exit));
                await first.DisposeAsync();

                second = new WindowsApp(first);
                await second.InitializeAsync();
                Assert.Equal(pinned, second.ItemsOf("pinnedItems"));
                Assert.Equal(targets, second.ItemsOf("basketItems"));

                //The pinned filter still works: it brings its text and its rows back
                Assert.Equal("", second.Filter.Text);
                second.Main.FindFirstDescendant(cf => cf.ByAutomationId("pinnedItems"))
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .First(b => WindowsApp.TextOf(b).Contains(second.Token))
                    .AsButton().Invoke();
                await WindowsApp.WaitUntil(() => second.Filter.Text == second.Token && second.Row(before) != null,
                    TimeSpan.FromSeconds(30), "the pinned filter's rows");
            }
            finally
            {
                if (second != null) await second.DisposeAsync();
                else
                {
                    await first.DisposeAsync();
                    first.DeleteFolders();
                }
            }
        }

        /// <summary>
        /// A drive switched off in the drives dialog stays off in the next process: its file is
        /// no longer found, a file of the drive that stayed on still is
        /// </summary>
        [Fact]
        public async Task ADriveTakenOutOfTheIndexStaysOutInANewProcess()
        {
            var temp = Path.GetPathRoot(Path.GetTempPath());
            //NTFS only: a cloud drive (Google Drive shows as a fixed FAT32 G:) would upload the test file
            var spare = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS"
                && !string.Equals(d.Name, temp, StringComparison.OrdinalIgnoreCase))?.Name;
            Assert.SkipWhen(spare == null, "Needs a second local NTFS drive to take out of the index");
            string spareFile = null, spareFolder = null, kept = null;
            var first = new WindowsApp
            {
                KeepFolders = true,
                BeforeStart = s =>
                {
                    //The work folder may be on that drive already (a GitHub runner's D:)
                    spareFolder = string.Equals(Path.GetPathRoot(s.Work), spare, StringComparison.OrdinalIgnoreCase)
                        ? null : Path.Combine(spare, "win-search-smoke-" + s.Token);
                    var folder = spareFolder ?? s.Work;
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(spareFile = Path.Combine(folder, $"{s.Token}-spare.txt"), "spare");
                    File.WriteAllText(kept = Path.Combine(s.Root, $"{s.Token}-kept.txt"), "kept");
                    var selection = Path.Combine(s.Data, "drive-selection.json");
                    var json = JsonNode.Parse(File.ReadAllText(selection))!;
                    json["Drives"]![spare.TrimEnd('\\')] = true;
                    File.WriteAllText(selection, json.ToJsonString());
                }
            };
            WindowsApp second = null;
            try
            {
                await first.InitializeAsync();
                await first.ShowOnly(first.Token, names => names.Contains(Path.GetFileName(spareFile)) && names.Contains(Path.GetFileName(kept)));

                first.ButtonNamed("Drives").Invoke();
                var dialog = await first.Opened("DrivesWindow");
                var box = await WindowsApp.WaitFor(() => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox))
                    .FirstOrDefault(b => b.Name.StartsWith(spare, StringComparison.OrdinalIgnoreCase))?.AsCheckBox(),
                    TimeSpan.FromSeconds(10), $"the check box of {spare}");
                Assert.True(box.IsChecked);
                box.IsChecked = false;
                dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).First(b => b.Name == "OK").AsButton().Invoke();
                await WindowsApp.WaitUntil(() => first.OtherWindows().Length == 0, TimeSpan.FromSeconds(10), "the dialog to close");
                Assert.Equal(0, first.CloseMainWindow(Exit));
                await first.DisposeAsync();

                second = new WindowsApp(first);
                await second.InitializeAsync();
                var shown = await second.ShowOnly(second.Token, names => names.Contains(Path.GetFileName(kept)));
                Assert.DoesNotContain(Path.GetFileName(spareFile), shown);

                second.ButtonNamed("Drives").Invoke();
                dialog = await second.Opened("DrivesWindow");
                var boxes = await WindowsApp.WaitFor(() => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)) is { Length: > 0 } b ? b : null,
                    TimeSpan.FromSeconds(10), "the drives");
                Assert.False(boxes.Single(b => b.Name.StartsWith(spare, StringComparison.OrdinalIgnoreCase)).AsCheckBox().IsChecked);
                Assert.True(boxes.Single(b => b.Name.StartsWith(temp, StringComparison.OrdinalIgnoreCase)).AsCheckBox().IsChecked);
                dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button)).First(b => b.Name == "Cancel").AsButton().Invoke();
            }
            finally
            {
                if (second != null) await second.DisposeAsync();
                else
                {
                    await first.DisposeAsync();
                    first.DeleteFolders();
                }
                if (spareFolder != null) PublishedApp.Delete(spareFolder);
            }
        }

        /// <summary>"📌 Pin…" beside the filter box</summary>
        static Button PinButton(WindowsApp app) => app.Main.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .First(b => b.Name.TrimEnd('…', '.', ' ').EndsWith("Pin", StringComparison.Ordinal)).AsButton();
    }
}
#endif
