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

        [Fact]
        public void ReturnsNoAutomaticChoiceForUnsupportedSystemLanguage() =>
            Assert.Null(Languages.ForSystemCulture(CultureInfo.GetCultureInfo("fi-FI")));
    }
}
