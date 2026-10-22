using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace search
{
    public partial class HelpWindow : Window
    {
        double zoom = 1;
        readonly List<(SelectableTextBlock Block, double BaseSize)> blocks = new();
        int findBlock = -1, findIndex = -1;

        public HelpWindow() => InitializeComponent();

        public HelpWindow(string helpFile) : this()
        {
            foreach (var block in MarkdownDocument.Load(helpFile))
                document.Children.Add(Render(block));
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.F3) { FindNext(); e.Handled = true; }
                else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { findBox.Focus(); e.Handled = true; }
            };
            viewer.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                Zoom(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }

        Control Render(MarkdownBlock block)
        {
            var size = block.Level switch { 1 => 24d, 2 => 19d, 3 => 16d, _ => 13d };
            var text = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = size,
                FontWeight = block.Level > 0 ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = block.Level switch
                {
                    1 => new Thickness(0, 8, 0, 5),
                    > 1 => new Thickness(0, 16, 0, 5),
                    _ => new Thickness(0, 3, 0, 3)
                },
                Inlines = new InlineCollection()
            };
            foreach (var part in block.Inlines)
            {
                var run = new Run(part.Text);
                if (part.Bold) run.FontWeight = FontWeight.Bold;
                if (part.Code)
                {
                    run.FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, Liberation Mono, monospace");
                    run.Background = Brushes.Gainsboro;
                }
                text.Inlines.Add(run);
            }
            blocks.Add((text, size));
            if (!block.ListItem) return text;
            var item = new DockPanel { Margin = new Thickness(18, 1, 0, 1) };
            var bullet = new TextBlock { Text = "•", Margin = new Thickness(0, 3, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(bullet, Dock.Left);
            item.Children.Add(bullet);
            item.Children.Add(text);
            return item;
        }

        void Zoom(double factor)
        {
            zoom = Math.Clamp(zoom * factor, 0.5, 3);
            foreach (var (block, baseSize) in blocks) block.FontSize = baseSize * zoom;
        }

        void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.1);
        void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1 / 1.1);
        void Close_Click(object sender, RoutedEventArgs e) => Close();
        void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

        void FindBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            FindNext();
            e.Handled = true;
        }

        /// <summary>Select the next occurrence of the find text and scroll it into view</summary>
        void FindNext()
        {
            var needle = findBox.Text;
            if (string.IsNullOrEmpty(needle) || blocks.Count == 0) return;
            var start = Math.Max(0, findBlock);
            for (var step = 0; step <= blocks.Count; step++)
            {
                //The last step returns to the starting block from its beginning (wrap around)
                var b = (start + step) % blocks.Count;
                var text = blocks[b].Block.Inlines?.Text ?? blocks[b].Block.Text ?? "";
                var from = step == 0 && findBlock >= 0 ? findIndex + 1 : 0;
                var at = from < text.Length ? text.IndexOf(needle, from, StringComparison.CurrentCultureIgnoreCase) : -1;
                if (at < 0) continue;
                findBlock = b;
                findIndex = at;
                var block = blocks[b].Block;
                block.SelectionStart = at;
                block.SelectionEnd = at + needle.Length;
                block.BringIntoView();
                return;
            }
        }
    }

    /// <summary>One paragraph of the help: heading level (0 = text), bullet flag, inline runs</summary>
    internal sealed record MarkdownBlock(int Level, bool ListItem, IReadOnlyList<MarkdownInline> Inlines)
    {
        public string Text => string.Concat(Inlines.Select(i => i.Text));
    }

    internal sealed record MarkdownInline(string Text, bool Bold, bool Code);

    /// <summary>
    /// The small Markdown subset the help files use: #/##/### headings, "- " bullets,
    /// **bold** and `code`. Parsed into plain records so the parser is testable without UI.
    /// </summary>
    internal static class MarkdownDocument
    {
        static readonly Regex InlineParts = new(@"(\*\*.+?\*\*|`.+?`)", RegexOptions.Compiled);

        internal static IReadOnlyList<MarkdownBlock> Load(string path)
            => Parse(File.ReadLines(path));

        internal static IReadOnlyList<MarkdownBlock> Parse(IEnumerable<string> lines)
        {
            var blocks = new List<MarkdownBlock>();
            foreach (var raw in lines)
            {
                if (raw.StartsWith("- "))
                {
                    blocks.Add(new MarkdownBlock(0, true, Inlines(raw[2..])));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var level = raw.StartsWith("### ") ? 3 : raw.StartsWith("## ") ? 2 : raw.StartsWith("# ") ? 1 : 0;
                blocks.Add(new MarkdownBlock(level, false, Inlines(level == 0 ? raw : raw[(level + 1)..])));
            }
            return blocks;
        }

        static IReadOnlyList<MarkdownInline> Inlines(string text)
        {
            var result = new List<MarkdownInline>();
            var last = 0;
            foreach (Match match in InlineParts.Matches(text))
            {
                if (match.Index > last) result.Add(new MarkdownInline(text[last..match.Index], false, false));
                var isBold = match.Value.StartsWith("**", StringComparison.Ordinal);
                var value = isBold ? match.Value[2..^2] : match.Value[1..^1];
                result.Add(new MarkdownInline(value, isBold, !isBold));
                last = match.Index + match.Length;
            }
            if (last < text.Length) result.Add(new MarkdownInline(text[last..], false, false));
            return result;
        }
    }
}
