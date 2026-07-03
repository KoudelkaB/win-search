using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace search.Models
{
    public class SearchTerms : History
    {
        class Info
        {
            public int TimesUsed = 1;
            public DateTime LastUsed = DateTime.Now;
        }

        static string path = "SearchTerms";
        Dictionary<string, Info> items;

        /// <summary>
        /// 100 most used search terms
        /// </summary>
        public IEnumerable<string> MostUsed => items.OrderByDescending(x => x.Value.TimesUsed).Select(x => x.Key);

        /// <summary>
        /// 100 last used search terms
        /// </summary>
        public IEnumerable<string> LastUsed => items.OrderByDescending(x => x.Value.LastUsed).Select(x => x.Key);

        /// <summary>
        /// Load search terms from persistent storage
        /// </summary>
        public SearchTerms()
        {
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
                $"New '{Path.GetFullPath(path)}' will be created".Debug();
                items = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
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
        public void Save() => File.WriteAllLines(path, items.OrderByDescending(x => x.Value.TimesUsed)
            .Select(x => $"{x.Value.TimesUsed}|{x.Value.LastUsed}|{x.Key}"));
    }
}
