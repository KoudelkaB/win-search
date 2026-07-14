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
    }

    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension() { }
        public LocExtension(string key) => Key = key;
        [ConstructorArgument("key")]
        public string Key { get; set; }
        public override object ProvideValue(IServiceProvider serviceProvider) => L.Text(Key);
    }
}
