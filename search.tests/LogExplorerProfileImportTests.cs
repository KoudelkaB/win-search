using System;
using System.IO;
using System.Linq;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The profiles of the HTML Log explorer (v0.9.6, localStorage in WebView2) are written
    /// once where the program of logexplorer/ keeps its own. Reading them needs WebView2
    /// (Windows); what is written, and when, is tested here.
    /// </summary>
    public class LogExplorerProfileImportTests
    {
        const string Profiles = "{\"named\":{\"errors\":{\"operators\":[],\"savedAt\":1}},\"order\":[\"errors\"]}";
        const string WorkingSet = "{\"operators\":[],\"selectedOperatorId\":null,\"nextOperatorId\":1}";

        [Fact]
        public void TheValuesOfThePageBecomeTheFilesOfTheProgramOnce()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-profile-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = Path.Combine(root, "LogExplorer");
                var legacy = Path.Combine(root, "WebView2");
                Assert.False(LogExplorerProfileImport.Needed(data, legacy)); //The old page never ran
                Directory.CreateDirectory(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "leveldb"));
                Assert.True(LogExplorerProfileImport.Needed(data, legacy));

                LogExplorerProfileImport.Write(data, Profiles, WorkingSet);

                Assert.Equal(Profiles, File.ReadAllText(Path.Combine(data, "profiles.json")));
                Assert.Equal(WorkingSet, File.ReadAllText(Path.Combine(data, "working-set.json")));
                Assert.False(LogExplorerProfileImport.Needed(data, legacy));
                Assert.Equal(new[] { "profiles.json", LogExplorerProfileImport.DoneMarker, "working-set.json" },
                    Array.ConvertAll(Directory.GetFiles(data), Path.GetFileName).Order(StringComparer.Ordinal));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void WhatTheProgramSavedStaysAndNothingFoundIsImportedOnlyOnce()
        {
            var data = Path.Combine(Path.GetTempPath(), "win-search-profile-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "working-set.json"), "{}");
                LogExplorerProfileImport.Write(data, "not json", WorkingSet);
                Assert.False(File.Exists(Path.Combine(data, "profiles.json")));
                Assert.Equal("{}", File.ReadAllText(Path.Combine(data, "working-set.json")));
                //The page kept nothing: done all the same
                Assert.True(File.Exists(Path.Combine(data, LogExplorerProfileImport.DoneMarker)));
                Assert.False(LogExplorerProfileImport.Needed(data, data));
            }
            finally { if (Directory.Exists(data)) Directory.Delete(data, true); }
        }

        [Fact]
        public void TheScriptResultIsAJsonStringOfTheTwoValues()
        {
            var result = System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonSerializer.Serialize(new[] { Profiles, null }));
            Assert.Equal((Profiles, null), LogExplorerProfileImport.ParseScriptResult(result));
            Assert.Equal((null, null), LogExplorerProfileImport.ParseScriptResult("null"));
        }
    }
}
