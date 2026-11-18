using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>The filter the user asked for owns the results, whatever refresh runs meanwhile</summary>
    public class SearchModelUpdateTests
    {
        [AvaloniaFact]
        public async Task ARefreshAfterAScanKeepsTheFilterTypedJustBefore()
        {
            using var files = new IndexedFiles();
            var model = new SearchModel(startFileSystem: false);
            await model.Update("");

            //Typed: the filter waits out its debounce while a published scan refreshes at once
            var typed = model.Update(files.Filter);
            var refresh = model.RefreshFromFilesNow();
            await Task.WhenAll(typed, refresh);

            await files.AssertFiltered(model);
        }

        [AvaloniaFact]
        public async Task AFilterTypedWhileARefreshTakesOverIsKept()
        {
            using var files = new IndexedFiles();
            var model = new SearchModel(startFileSystem: false);
            await model.Update("");

            //The refresh stops between its check for a pending filter and its take-over...
            var checkedForFilter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resume = new ManualResetEventSlim();
            model.RefreshTakingOver = () =>
            {
                checkedForFilter.TrySetResult();
                resume.Wait(TimeSpan.FromSeconds(10));
            };
            var refresh = model.RefreshFromFilesNow();
            await checkedForFilter.Task;
            model.RefreshTakingOver = null;
            //...the filter is typed then, and the refresh goes on
            var typed = Task.Run(() => model.Update(files.Filter));
            await Task.Delay(200);
            resume.Set();
            await Task.WhenAll(refresh, typed);

            await files.AssertFiltered(model);
        }

        /// <summary>A needle and an unrelated file in the index (entries of an archive)</summary>
        sealed class IndexedFiles : IDisposable
        {
            readonly string token = Guid.NewGuid().ToString("N");
            readonly string root;

            public IndexedFiles()
            {
                root = Path.Combine(Path.GetTempPath(), "win-search-update-" + token);
                Directory.CreateDirectory(root);
                var archive = Path.Combine(root, "files.zip");
                using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntry($"needle{token}.txt");
                    zip.CreateEntry($"unrelated{token}.txt");
                }
                Assert.True(SearchModel.AddArchive(new FileNode(archive)));
            }

            public string Filter => $"needle{token}";

            public async Task AssertFiltered(SearchModel model)
            {
                await model.UserUpdatesSettled();
                Dispatcher.UIThread.RunJobs();
                var names = model.Items.Select(n => n.Name).ToArray();
                Assert.Contains($"needle{token}.txt", names);
                Assert.All(names, name => Assert.Contains(Filter, name));
            }

            public void Dispose() => Directory.Delete(root, true);
        }
    }
}
