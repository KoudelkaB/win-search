using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace search.Models
{
    public class Filters : History
    {
        class Info
        {
            public int TimesUsed = 1;
            public DateTime LastUsed = DateTime.Now;
        }

        static readonly string path = UserDataPaths.For("Filters");
        Dictionary<string, Info> items;

        /// <summary>
        /// 100 most used filters
        /// </summary>
        public IEnumerable<string> MostUsed => items.OrderByDescending(x => x.Value.TimesUsed).Select(x => x.Key);

        /// <summary>
        /// 100 last used filters
        /// </summary>
        public IEnumerable<string> LastUsed => items.OrderByDescending(x => x.Value.LastUsed).Select(x => x.Key);

        /// <summary>
        /// Load filters from persistent storage
        /// </summary>
        /// <param name="filters"></param>
        public Filters()
        {
            MigrateLegacyStore();
            if (File.Exists(path))
            {
                try
                {
                    items = File.ReadAllLines(path).Select(l => l.Split(new char[] { '|' }, 3))
                        .ToDictionary(x => x[2], x => new Info { TimesUsed = int.Parse(x[0]), LastUsed = DateTime.Parse(x[1]) }, StringComparer.OrdinalIgnoreCase);
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
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "Filters");
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
            if (string.IsNullOrWhiteSpace(item)) return; // Don't save empty or whitespace items
            
            if (items.TryGetValue(item, out var i))
            {
                i.LastUsed = DateTime.Now;
                i.TimesUsed++;
            }
            else items[item] = new Info();
            Save();
        }

        /// <summary>
        /// Delete an item from the filters history
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
        /// Save filters to persistent storage
        /// </summary>
        /// <param name="filters"></param>
        public void Save() => File.WriteAllLines(path, items.OrderByDescending(x => x.Value.TimesUsed)
            .Select(x => $"{x.Value.TimesUsed}|{x.Value.LastUsed}|{x.Key}"));
    }

    public class History
    {
        List<string> history = new List<string>();
        int current = 0;

        /// <summary>
        /// Add the item at the end of the history
        /// </summary>
        /// <param name="item"></param>
        public void Add2History(string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return; // Don't add empty or whitespace items to history
            
            if (Current != item)
            {
                //Clear all after current
                for (int i = history.Count; --i > current;) history.RemoveAt(i);

                //Add item after current
                history.Add(item);
                current = history.Count - 1;
            }
        }

        public string Current => current >= 0 && current < history.Count ? history[current] : null;

        /// <summary>
        /// Get backward item from history
        /// </summary>
        /// <returns></returns>
        public string Backward => current > 0 ? history[--current] : null;

        /// <summary>
        /// Get forward item from history
        /// </summary>
        /// <returns></returns>
        public string Forward => current < history.Count - 1 ? history[++current] : null;

    }

}
