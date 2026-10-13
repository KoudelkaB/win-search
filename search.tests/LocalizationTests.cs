using System.Globalization;
using System.Linq;
using Xunit;

namespace search.Tests
{
    public class LocalizationTests
    {
        [Theory]
        [InlineData("cs-CZ", "Nápověda")]
        [InlineData("de-DE", "Hilfe")]
        [InlineData("fr-FR", "Aide")]
        [InlineData("es-ES", "Ayuda")]
        [InlineData("pl-PL", "Pomoc")]
        [InlineData("en-US", "Help")]
        public void UsesWindowsUiLanguageAndItsNeutralTranslation(string cultureName, string expected)
        {
            Assert.Equal(expected, L.Text("Help", CultureInfo.GetCultureInfo(cultureName)));
        }

        [Fact]
        public void UnsupportedLanguageAndMissingKeyHaveSafeFallbacks()
        {
            // Finnish has no satellite assembly => falls back to the neutral English text.
            Assert.Equal("Help", L.Text("Help", CultureInfo.GetCultureInfo("fi-FI")));
            Assert.Equal("FutureKey", L.Text("FutureKey", CultureInfo.GetCultureInfo("cs-CZ")));
        }

        [Fact]
        public void CountColumnHasALocalizedCzechHeader()
            => Assert.Equal("Počet",
                L.Text("Count", CultureInfo.GetCultureInfo("cs-CZ")));

        [Theory]
        [InlineData("en-US", "Add selected as targets")]
        [InlineData("cs-CZ", "Přidat vybrané jako cíle")]
        public void ButtonsAndMenusReuseTheHintTranslationCapitalized(string cultureName, string expected)
            => Assert.Equal(expected,
                L.Caption("add selected as targets", CultureInfo.GetCultureInfo(cultureName)));

        [Fact]
        public void CaptionLeavesTextWithoutALowerCaseStartUntouched()
        {
            Assert.Equal("＋ Add as target", L.Caption("AddTarget", CultureInfo.GetCultureInfo("en-US")));
            var japanese = CultureInfo.GetCultureInfo("ja-JP");
            Assert.Equal(L.Text("add selected as targets", japanese), L.Caption("add selected as targets", japanese));
        }

        [Theory]
        [InlineData("cs")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        [InlineData("it")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("pl")]
        [InlineData("pt-BR")]
        [InlineData("zh-Hans")]
        public void EveryTextHasATranslationWithTheSamePlaceholders(string cultureName)
        {
            //A format item missing from a translation silently drops the path or count it stands
            //for; one that the neutral text does not have throws when the status is formatted.
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var neutral = new System.Resources.ResourceManager("search.Resources.Strings", typeof(L).Assembly)
                .GetResourceSet(CultureInfo.InvariantCulture, true, false);
            foreach (System.Collections.DictionaryEntry entry in neutral)
            {
                var key = (string)entry.Key;
                Assert.Equal(
                    (key, FormatItems((string)entry.Value)),
                    (key, FormatItems(L.Text(key, culture))));
            }

            static string FormatItems(string text) => string.Join(",",
                System.Text.RegularExpressions.Regex.Matches(text, @"\{(\d)[^}]*\}")
                    .Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
        }

        [Theory]
        [InlineData("cs")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        [InlineData("it")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("pl")]
        [InlineData("pt-BR")]
        [InlineData("zh-Hans")]
        public void StatusAndDialogTextsAreTranslated(string cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            foreach (var key in new[] { "ConfirmRecycle", "Recycled", "CompletedWithErrors", "NothingSelected",
                         "ProgressRemainingAbout", "Cancelling", "ImportReplaceConfirm", "OriginWalk" })
                Assert.NotEqual(L.Text(key, CultureInfo.InvariantCulture), L.Text(key, culture));
        }

        [Theory]
        [InlineData("cs")]
        [InlineData("de")]
        [InlineData("es")]
        [InlineData("fr")]
        [InlineData("it")]
        [InlineData("ja")]
        [InlineData("ko")]
        [InlineData("pl")]
        [InlineData("pt-BR")]
        [InlineData("zh-Hans")]
        public void RenameHintsAreTranslatedInEveryLanguage(string cultureName)
        {
            //F2 N takes the typed text as the new name - its hint used to ask for a "string to delete"
            var culture = CultureInfo.GetCultureInfo(cultureName);
            Assert.NotEqual("new name", L.Text("new name", culture));
            Assert.NotEqual(L.Text("string to delete", culture), L.Text("new name", culture));
            //F2 A stayed English in every translated menu - its text had no resource at all
            Assert.NotEqual("Change last access time", L.Text("Change last access time", culture));
        }
    }
}
