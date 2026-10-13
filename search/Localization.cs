using System;
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace search
{
    /// <summary>Standard RESX-backed localization with English as the neutral fallback.</summary>
    public static class L
    {
        static readonly ResourceManager Resources =
            new("search.Resources.Strings", typeof(L).Assembly);

        public static string Text(string key) => Text(key, CultureInfo.CurrentUICulture);

        internal static string Text(string key, CultureInfo culture)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return Resources.GetString(key, culture) ?? key;
        }

        public static string Format(string key, params object[] args)
            => string.Format(CultureInfo.CurrentCulture, Text(key), args);

        /// <summary>
        /// The text starting with a capital letter. Command hints are written in lower case
        /// ("add selected as targets"); a button, menu item or tooltip of the same command
        /// reuses that translation instead of carrying a second, English-only wording.
        /// </summary>
        public static string Caption(string key) => Caption(key, CultureInfo.CurrentUICulture);

        internal static string Caption(string key, CultureInfo culture)
        {
            var text = Text(key, culture);
            return text.Length > 0 && char.IsLower(text[0])
                ? char.ToUpper(text[0], culture) + text[1..]
                : text;
        }

        /// <summary>
        /// The user's Windows regional format. App.ApplyCulture replaces CurrentCulture with the
        /// UI language, so text typed or copied in the regional format ("13.08.2025 11:35") has
        /// to be parsed with this one - under an English UI it would swap day and month or fail.
        /// </summary>
        public static CultureInfo Regional { get; private set; } = CultureInfo.CurrentCulture;

        /// <summary>Remember the regional format before the UI language overrides CurrentCulture</summary>
        internal static void CaptureRegional()
        {
            if (!regionalCaptured) Regional = CultureInfo.CurrentCulture;
            regionalCaptured = true;
        }
        static bool regionalCaptured;

        /// <summary>Parse a date in the regional format, then the UI language, then invariant</summary>
        public static bool TryParseDate(string text, out DateTime time)
            => DateTime.TryParse(text, Regional, DateTimeStyles.None, out time)
            || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out time)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension() { }
        public LocExtension(string key) => Key = key;
        [ConstructorArgument("key")]
        public string Key { get; set; }
        /// <summary>Start with a capital letter - a command hint reused as a caption or tooltip</summary>
        public bool Capitalize { get; set; }
        /// <summary>Keyboard shortcut of the command, appended in brackets ("Alt+N")</summary>
        public string Gesture { get; set; }
        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var text = Capitalize ? L.Caption(Key) : L.Text(Key);
            return string.IsNullOrEmpty(Gesture) ? text : $"{text} ({Gesture})";
        }
    }
}
