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
            Assert.Equal("Help", L.Text("Help", CultureInfo.GetCultureInfo("ja-JP")));
            Assert.Equal("FutureKey", L.Text("FutureKey", CultureInfo.GetCultureInfo("cs-CZ")));
        }
    }
}
