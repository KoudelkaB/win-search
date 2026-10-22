using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace search
{
    public enum MessageDialogButtons { Ok, OkCancel, YesNo }
    public enum MessageDialogIcon { None, Information, Question, Warning, Error }
    public enum MessageDialogResult { None, Ok, Cancel, Yes, No }

    /// <summary>
    /// The message box Avalonia does not have: text, an icon and one of three button sets.
    /// Modal to its owner when there is one; Enter takes the default button, Escape cancels.
    /// </summary>
    public sealed class MessageDialog : Window
    {
        MessageDialogResult result;
        readonly MessageDialogResult cancelResult;

        MessageDialog(string text, string caption, MessageDialogButtons buttons, MessageDialogIcon icon,
            MessageDialogResult defaultResult)
        {
            Title = caption ?? "";
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            CanMinimize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            MinWidth = 320;
            MaxWidth = 720;
            cancelResult = buttons switch
            {
                MessageDialogButtons.OkCancel => MessageDialogResult.Cancel,
                MessageDialogButtons.YesNo => MessageDialogResult.No,
                _ => MessageDialogResult.Ok
            };
            result = cancelResult;

            var glyph = icon switch
            {
                MessageDialogIcon.Information => ("ℹ", Brushes.DodgerBlue),
                MessageDialogIcon.Question => ("?", Brushes.DodgerBlue),
                MessageDialogIcon.Warning => ("⚠", Brushes.DarkOrange),
                MessageDialogIcon.Error => ("✖", Brushes.Firebrick),
                _ => (null, null)
            };
            var body = new DockPanel { Margin = new Thickness(16, 14, 16, 12) };
            var buttonRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6,
                Margin = new Thickness(0, 14, 0, 0)
            };
            DockPanel.SetDock(buttonRow, Dock.Bottom);
            body.Children.Add(buttonRow);
            if (glyph.Item1 != null)
            {
                var iconText = new TextBlock
                {
                    Text = glyph.Item1,
                    FontSize = 26,
                    Foreground = glyph.Item2,
                    Margin = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                DockPanel.SetDock(iconText, Dock.Left);
                body.Children.Add(iconText);
            }
            body.Children.Add(new SelectableTextBlock
            {
                Text = text ?? "",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 620
            });

            Button Add(string label, MessageDialogResult value)
            {
                var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
                button.Click += (_, __) => { result = value; Close(); };
                buttonRow.Children.Add(button);
                if (value == defaultResult) button.Classes.Add("accent");
                return button;
            }
            Button first = buttons switch
            {
                MessageDialogButtons.OkCancel => Add(L.Text("OK"), MessageDialogResult.Ok),
                MessageDialogButtons.YesNo => Add(L.Text("Yes"), MessageDialogResult.Yes),
                _ => Add(L.Text("OK"), MessageDialogResult.Ok)
            };
            Button second = buttons switch
            {
                MessageDialogButtons.OkCancel => Add(L.Text("Cancel"), MessageDialogResult.Cancel),
                MessageDialogButtons.YesNo => Add(L.Text("No"), MessageDialogResult.No),
                _ => null
            };
            Content = body;
            var focusTarget = second != null && defaultResult == cancelResult ? second : first;
            Opened += (_, __) => focusTarget.Focus();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    result = cancelResult;
                    Close();
                    e.Handled = true;
                }
            };
        }

        /// <summary>
        /// Show the message. With an owner the dialog is modal; without one (startup errors)
        /// it is a plain window and the task completes when it closes.
        /// </summary>
        public static async Task<MessageDialogResult> Show(Window owner, string text, string caption,
            MessageDialogButtons buttons = MessageDialogButtons.Ok, MessageDialogIcon icon = MessageDialogIcon.None,
            MessageDialogResult defaultResult = MessageDialogResult.None)
        {
            if (defaultResult == MessageDialogResult.None)
                defaultResult = buttons == MessageDialogButtons.YesNo ? MessageDialogResult.Yes : MessageDialogResult.Ok;
            var dialog = new MessageDialog(text, caption, buttons, icon, defaultResult);
            if (owner != null && owner.IsVisible)
            {
                if (owner.Icon != null) dialog.Icon = owner.Icon;
                await dialog.ShowDialog(owner);
            }
            else
            {
                var closed = new TaskCompletionSource();
                dialog.Closed += (_, __) => closed.TrySetResult();
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.Show();
                await closed.Task;
            }
            return dialog.result;
        }
    }
}
