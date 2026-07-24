using System.Globalization;
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
    }
}
