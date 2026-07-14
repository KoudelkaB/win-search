using System;
using System.Linq;
using System.Threading;
using System.Windows.Documents;
using Xunit;

namespace search.Tests
{
    public class MarkdownDocumentTests
    {
        [Fact]
        public void RendersSingleLetterInlineCodeWithoutThrowing()
        {
            RunSta(() =>
            {
                var document = MarkdownDocument.Parse(new[] { "Use `O`, `A`, and **F1**." });
                var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
                var code = Assert.IsType<Run>(paragraph.Inlines.ElementAt(1));
                Assert.Equal("O", code.Text);
            });
        }

        static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }
    }
}
