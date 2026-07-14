using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace search
{
    public partial class HelpWindow : Window
    {
        public HelpWindow(string helpFile)
        {
            InitializeComponent();
            viewer.Document = MarkdownDocument.Load(helpFile);
        }
    }

    internal static class MarkdownDocument
    {
        static readonly Regex InlineParts = new(@"(\*\*.+?\*\*|`.+?`)", RegexOptions.Compiled);

        internal static FlowDocument Load(string path)
            => Parse(File.ReadLines(path));

        internal static FlowDocument Parse(System.Collections.Generic.IEnumerable<string> lines)
        {
            var document = new FlowDocument { PagePadding = new Thickness(8), FontFamily = SystemFonts.MessageFontFamily, FontSize = SystemFonts.MessageFontSize };
            List list = null;
            foreach (var raw in lines)
            {
                if (raw.StartsWith("- "))
                {
                    list ??= new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 2, 0, 6) };
                    var item = new ListItem();
                    item.Blocks.Add(Paragraph(raw[2..]));
                    list.ListItems.Add(item);
                    continue;
                }
                if (list != null) { document.Blocks.Add(list); list = null; }
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var level = raw.StartsWith("### ") ? 3 : raw.StartsWith("## ") ? 2 : raw.StartsWith("# ") ? 1 : 0;
                var paragraph = Paragraph(level == 0 ? raw : raw[(level + 1)..]);
                if (level > 0)
                {
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.FontSize = level == 1 ? 24 : level == 2 ? 19 : 16;
                    paragraph.Margin = new Thickness(0, level == 1 ? 8 : 16, 0, 5);
                }
                document.Blocks.Add(paragraph);
            }
            if (list != null) document.Blocks.Add(list);
            return document;
        }

        static Paragraph Paragraph(string text)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 3), TextAlignment = TextAlignment.Left };
            var last = 0;
            foreach (Match match in InlineParts.Matches(text))
            {
                if (match.Index > last) paragraph.Inlines.Add(new Run(text[last..match.Index]));
                var isBold = match.Value.StartsWith("**", StringComparison.Ordinal);
                var value = isBold ? match.Value[2..^2] : match.Value[1..^1];
                var run = new Run(value);
                if (isBold) run.FontWeight = FontWeights.Bold;
                else { run.FontFamily = new FontFamily("Consolas"); run.Background = Brushes.Gainsboro; }
                paragraph.Inlines.Add(run);
                last = match.Index + match.Length;
            }
            if (last < text.Length) paragraph.Inlines.Add(new Run(text[last..]));
            return paragraph;
        }
    }
}
