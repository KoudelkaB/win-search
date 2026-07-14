using System.Globalization;
using Xunit;

namespace search.Tests
{
    public class LanguageTests
    {
        [Theory]
        [InlineData("cs-CZ", "cs")]
        [InlineData("pt-BR", "pt-BR")]
        [InlineData("zh-CN", "zh-Hans")]
        public void ChoosesMatchingSupportedSystemLanguage(string systemCulture, string expected)
        {
            Assert.Equal(expected, Languages.ForSystemCulture(CultureInfo.GetCultureInfo(systemCulture)));
        }

        [Theory]
        [InlineData("fi-FI")]  // No translation at all.
        [InlineData("zh-TW")]  // Traditional Chinese - only Simplified ships, so ask.
        public void ReturnsNoAutomaticChoiceForUnsupportedSystemLanguage(string systemCulture) =>
            Assert.Null(Languages.ForSystemCulture(CultureInfo.GetCultureInfo(systemCulture)));
    }
}
