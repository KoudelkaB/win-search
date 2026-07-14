using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace search
{
    internal sealed class LanguageSettings { public string Culture { get; set; } }

    internal static class LanguageSettingsStore
    {
        static readonly string Path = UserDataPaths.For("language-settings.json");
        public static string Load() { try { return JsonSerializer.Deserialize<LanguageSettings>(File.ReadAllText(Path))?.Culture; } catch { return null; } }
        public static void Save(string culture) => File.WriteAllText(Path, JsonSerializer.Serialize(new LanguageSettings { Culture = culture }));
    }

    public sealed class LanguageOption
    {
        public string Culture { get; init; }
        public string Name { get; init; }
        public override string ToString() => Name;
    }

    internal static class Languages
    {
        public static readonly IReadOnlyList<LanguageOption> All = new[]
        {
            new LanguageOption { Culture = "en", Name = "English" }, new LanguageOption { Culture = "cs", Name = "Čeština" },
            new LanguageOption { Culture = "de", Name = "Deutsch" }, new LanguageOption { Culture = "fr", Name = "Français" },
            new LanguageOption { Culture = "es", Name = "Español" }, new LanguageOption { Culture = "pl", Name = "Polski" },
            new LanguageOption { Culture = "it", Name = "Italiano" }, new LanguageOption { Culture = "pt-BR", Name = "Português (Brasil)" },
            new LanguageOption { Culture = "ja", Name = "日本語" }, new LanguageOption { Culture = "ko", Name = "한국어" }, new LanguageOption { Culture = "zh-Hans", Name = "简体中文" }
        };
        public static string ForSystemCulture(CultureInfo culture)
        {
            var match = All.FirstOrDefault(x => x.Culture.Equals(culture.Name, StringComparison.OrdinalIgnoreCase) || x.Culture.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Culture;
            return culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : null;
        }
    }
}
