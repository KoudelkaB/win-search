using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Web.WebView2.Core;

namespace search
{
    /// <summary>
    /// Log explorer up to v0.9.6 was the HTML page in WebView2 and kept its profiles and last
    /// pipeline in the localStorage of that page. The program of logexplorer/ keeps the same
    /// JSON in files of its data folder (profiles.json, working-set.json). On Windows, before
    /// the program opens for the first time, the values are read once through WebView2 - the
    /// browser profile of the old window, its origin - and written there.
    /// </summary>
    internal static class LogExplorerProfileImport
    {
        internal const string ProfilesKey = "logExplorer.profiles.v1";
        internal const string WorkingSetKey = "logExplorer.workingSet.v1";

        /// <summary>In the data folder once the import ran: never again, even with nothing found</summary>
        internal const string DoneMarker = "webview2-import.done";

        /// <summary>The browser profile of the old WebView2 window (WPF, v0.9.6)</summary>
        static string LegacyUserData => UserDataPaths.For("WebView2");

        static Task running;

        /// <summary>
        /// The old page may have kept something, and nothing was imported yet: the program
        /// has no profiles of its own and the import never ran
        /// </summary>
        internal static bool Needed(string dataFolder, string legacyUserData)
            => !File.Exists(Path.Combine(dataFolder, "profiles.json"))
                && !File.Exists(Path.Combine(dataFolder, DoneMarker))
                && Directory.Exists(Path.Combine(legacyUserData, "EBWebView", "Default", "Local Storage", "leveldb"));

        /// <summary>
        /// Write the values as the page kept them (null: it kept none). A file the program
        /// wrote meanwhile stays; a value that is not a JSON object is left out.
        /// </summary>
        internal static void Write(string dataFolder, string profiles, string workingSet)
        {
            Directory.CreateDirectory(dataFolder);
            WriteNew(Path.Combine(dataFolder, "profiles.json"), profiles);
            WriteNew(Path.Combine(dataFolder, "working-set.json"), workingSet);
            File.WriteAllText(Path.Combine(dataFolder, DoneMarker), "");
        }

        static void WriteNew(string path, string json)
        {
            if (string.IsNullOrWhiteSpace(json) || File.Exists(path)) return;
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            }
            catch (JsonException) { return; }
            var staged = path + ".import.tmp";
            File.WriteAllText(staged, json, new UTF8Encoding(false));
            try { File.Move(staged, path, overwrite: false); }
            catch (IOException) { File.Delete(staged); } //The program saved its own first
        }

        /// <summary>
        /// Import before the program opens (UI thread). Done in a moment when there is nothing
        /// to import; a failure is logged and never stops the program from opening - it is
        /// tried again at the next start of the app.
        /// </summary>
        public static Task EnsureAsync(TopLevel owner)
        {
            if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
            if (running != null) return running;
            var dataFolder = UserDataPaths.For(NativeLogExplorer.Folder);
            if (!Needed(dataFolder, LegacyUserData)) return Task.CompletedTask;
            return running = Run(owner, dataFolder);
        }

        static async Task Run(TopLevel owner, string dataFolder)
        {
            try
            {
                var read = Read(owner, LegacyUserData);
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(15))) != read)
                    throw new TimeoutException("WebView2 did not answer.");
                var (profiles, workingSet) = await read;
                Write(dataFolder, profiles, workingSet);
            }
            catch (Exception e)
            {
                StorageMaintenance.AppendDiagnostic($"Log explorer: profiles of the old window were not imported ({e.Message}).");
            }
        }

        /// <summary>
        /// The values in localStorage of the old page: a hidden WebView2 on the old browser
        /// profile opens an empty page at the origin the old page had and reads them
        /// </summary>
        static async Task<(string Profiles, string WorkingSet)> Read(TopLevel owner, string userData)
        {
            var window = owner?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (window == IntPtr.Zero) throw new InvalidOperationException("No window to host WebView2.");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            var controller = await environment.CreateCoreWebView2ControllerAsync(window);
            try
            {
                controller.IsVisible = false;
                var core = controller.CoreWebView2;
                //Served here: the folder of the old pages is gone, and the page must run nothing
                core.AddWebResourceRequestedFilter(WebAppWindow.Origin + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) => e.Response = environment.CreateWebResourceResponse(
                    new MemoryStream(Encoding.UTF8.GetBytes("<!doctype html><title></title>")), 200, "OK",
                    "Content-Type: text/html; charset=utf-8");
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                core.Navigate(WebAppWindow.Origin + "log-explorer-profiles.html");
                if (!await loaded.Task) throw new IOException("The page of the old origin did not load.");
                var result = await core.ExecuteScriptAsync(
                    $"JSON.stringify([localStorage.getItem('{ProfilesKey}'), localStorage.getItem('{WorkingSetKey}')])");
                return ParseScriptResult(result);
            }
            finally
            {
                controller.Close();
            }
        }

        /// <summary>
        /// ExecuteScriptAsync gives the result as JSON: here a JSON string holding the JSON
        /// array of the two values (each null or the text the page stored)
        /// </summary>
        internal static (string Profiles, string WorkingSet) ParseScriptResult(string result)
        {
            var values = JsonSerializer.Deserialize<string[]>(JsonSerializer.Deserialize<string>(result) ?? "[]") ?? Array.Empty<string>();
            return (values.Length > 0 ? values[0] : null, values.Length > 1 ? values[1] : null);
        }
    }
}
