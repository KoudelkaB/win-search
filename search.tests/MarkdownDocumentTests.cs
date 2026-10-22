using System.Linq;
using Xunit;

namespace search.Tests
{
    public class MarkdownDocumentTests
    {
        [Fact]
        public void RendersSingleLetterInlineCodeWithoutThrowing()
        {
            var document = MarkdownDocument.Parse(new[] { "Use `O`, `A`, and **F1**." });
            var paragraph = Assert.Single(document);
            var code = paragraph.Inlines.ElementAt(1);
            Assert.Equal("O", code.Text);
            Assert.True(code.Code);
            Assert.True(paragraph.Inlines.Last(i => i.Bold).Text == "F1");
        }

        [Fact]
        public void HeadingsAndBulletsKeepTheirLevelAndText()
        {
            var document = MarkdownDocument.Parse(new[] { "# Title", "", "## Part", "- item **one**", "text" });
            Assert.Equal(new[] { 1, 2, 0, 0 }, document.Select(b => b.Level));
            Assert.True(document[2].ListItem);
            Assert.Equal("item one", document[2].Text);
            Assert.False(document[3].ListItem);
        }
    }
}
