using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        /// The old page may have kept something and the import has not finished yet. Only the
        /// marker tells: an import cut short after profiles.json (working-set.json failed) runs
        /// again, and so does one that failed before the program saved profiles of its own.
        /// </summary>
        internal static bool Needed(string dataFolder, string legacyUserData)
            => !File.Exists(Path.Combine(dataFolder, DoneMarker))
                && Directory.Exists(Path.Combine(legacyUserData, "EBWebView", "Default", "Local Storage", "leveldb"));

        /// <summary>
        /// Write the values as the page kept them (null: it kept none), then the marker. The
        /// profiles are added to those the program has (profiles.json) - a name it has keeps
        /// its profile - so running again after an interruption adds nothing twice; the last
        /// pipeline is written only when the program has none. A value that is not a JSON
        /// object is left out.
        /// </summary>
        internal static void Write(string dataFolder, string profiles, string workingSet)
        {
            Directory.CreateDirectory(dataFolder);
            AddProfiles(dataFolder, profiles);
            WriteNew(Path.Combine(dataFolder, "working-set.json"), workingSet);
            File.WriteAllText(Path.Combine(dataFolder, DoneMarker), "");
        }

        /// <summary>
        /// The named profiles of the page that profiles.json lacks, after its own in the order
        /// of the page. Under the lock of the program (profiles.lock, see its storage.rs): a
        /// window of it may be saving.
        /// </summary>
        static void AddProfiles(string dataFolder, string profiles)
        {
            if (Store(profiles) is not { } page) return;
            using var lease = LockProfiles(dataFolder);
            var path = Path.Combine(dataFolder, "profiles.json");
            var own = File.Exists(path) ? Store(File.ReadAllText(path)) : null;
            if (own == null)
            {
                Replace(path, page.ToJsonString());
                return;
            }
            var named = (JsonObject)own["named"];
            var order = (JsonArray)own["order"];
            var added = new List<string>();
            foreach (var (name, profile) in (JsonObject)page["named"])
                if (!named.ContainsKey(name))
                {
                    named[name] = profile?.DeepClone();
                    added.Add(name);
                }
            if (added.Count == 0) return;
            var pageOrder = ((JsonArray)page["order"]).Select(entry => entry?.GetValue<string>()).ToList();
            foreach (var name in added.OrderBy(name => pageOrder.IndexOf(name) is var i and >= 0 ? i : int.MaxValue))
                order.Add(name);
            Replace(path, own.ToJsonString());
        }

        /// <summary>
        /// The profiles as {"named": {...}, "order": [...]}; an older store was the map of the
        /// profiles itself. Null when it is not a JSON object.
        /// </summary>
        internal static JsonObject Store(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            JsonNode node;
            try { node = JsonNode.Parse(json); }
            catch (JsonException) { return null; }
            if (node is not JsonObject store) return null;
            var named = store["named"] as JsonObject ?? store;
            var order = new JsonArray(((store["named"] is JsonObject ? store["order"] as JsonArray : null) ?? new JsonArray())
                .Where(entry => entry is JsonValue value && value.TryGetValue<string>(out _))
                .Select(entry => entry.DeepClone()).ToArray());
            return new JsonObject { ["named"] = named.DeepClone(), ["order"] = order };
        }

        /// <summary>
        /// The lock the program holds while it changes profiles.json. Its lock covers the whole
        /// file and the one taken here fails at once while it is held: tried for a few seconds.
        /// </summary>
        static FileStream LockProfiles(string dataFolder)
        {
            var file = new FileStream(Path.Combine(dataFolder, "profiles.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    file.Lock(0, long.MaxValue);
                    return file;
                }
                catch (IOException) when (attempt < 50)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
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

        /// <summary>Written beside and renamed, as the program writes it: never half a file</summary>
        static void Replace(string path, string json)
        {
            var staged = path + ".import.tmp";
            File.WriteAllText(staged, json, new UTF8Encoding(false));
            File.Move(staged, path, overwrite: true);
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
