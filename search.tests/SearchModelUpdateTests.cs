using System;
using System.IO;
using System.Linq;
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
            var token = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), "win-search-update-" + token);
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "files.zip");
            try
            {
                using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
                {
                    zip.CreateEntry($"needle{token}.txt");
                    zip.CreateEntry($"unrelated{token}.txt");
                }
                Assert.True(SearchModel.AddArchive(new FileNode(archive)));
                var model = new SearchModel(startFileSystem: false);
                await model.Update("");

                //Typed: the filter waits out its debounce while a published scan refreshes at once
                var typed = model.Update($"needle{token}");
                var refresh = model.RefreshFromFilesNow();
                await Task.WhenAll(typed, refresh);
                await model.UserUpdatesSettled();
                Dispatcher.UIThread.RunJobs();

                var names = model.Items.Select(n => n.Name).ToArray();
                Assert.Contains($"needle{token}.txt", names);
                Assert.All(names, name => Assert.Contains($"needle{token}", name));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
