using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;

namespace search.Models
{
    public class SearchTerms : History
    {
        class Info
        {
            public int TimesUsed = 1;
            public DateTime LastUsed = DateTime.Now;
        }

        static readonly string path = UserDataPaths.For("SearchTerms");
        Dictionary<string, Info> items;

        /// <summary>
        /// 1000 most used search terms
        /// </summary>
        public IEnumerable<string> MostUsed => items.OrderByDescending(x => x.Value.TimesUsed).Take(1000).Select(x => x.Key);

        /// <summary>
        /// 1000 last used search terms
        /// </summary>
        public IEnumerable<string> LastUsed => items.OrderByDescending(x => x.Value.LastUsed).Take(1000).Select(x => x.Key);

        /// <summary>
        /// Load search terms from persistent storage
        /// </summary>
        public SearchTerms()
        {
            MigrateLegacyStore();
            if (File.Exists(path))
            {
                try
                {
                    items = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadLines(path))
                    {
                        var values = line.Split(new char[] { '|' }, 3);
                        if (values.Length != 3 ||
                            !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var times) ||
                            !(DateTime.TryParseExact(values[1], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastUsed) ||
                              DateTime.TryParse(values[1], out lastUsed))) continue;
                        items[values[2]] = new Info { TimesUsed = times, LastUsed = lastUsed };
                    }
                }
                catch { }
            }
            if (items == null)
            {
                $"New '{path}' will be created".Debug();
                items = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void MigrateLegacyStore()
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "SearchTerms");
            if (File.Exists(path) || !File.Exists(legacyPath))
                return;

            try
            {
                File.Copy(legacyPath, path, overwrite: false);
            }
            catch
            {
                // Ignore migration failures and fall back to a fresh store.
            }
        }

        public void Used(string item)
        {
            Add2History(item);
            item = item.Trim();
            if (string.IsNullOrWhiteSpace(item)) return; // Don't save empty search terms
            
            if (items.TryGetValue(item, out var i))
            {
                i.LastUsed = DateTime.Now;
                i.TimesUsed++;
            }
            else items[item] = new Info();
            Save();
        }

        /// <summary>
        /// Delete an item from the search terms history
        /// </summary>
        /// <param name="item">The item to delete</param>
        public void Delete(string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            
            item = item.Trim();
            if (items.Remove(item))
            {
                Save();
            }
        }

        /// <summary>
        /// Save search terms to persistent storage
        /// </summary>
        public void Save()
        {
            try
            {
                File.WriteAllLines(path, items.OrderByDescending(x => x.Value.TimesUsed)
                    .Select(x => $"{x.Value.TimesUsed.ToString(CultureInfo.InvariantCulture)}|{x.Value.LastUsed:O}|{x.Key}"));
            }
            catch { }
        }
    }
}
