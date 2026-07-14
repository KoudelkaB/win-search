using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using search.Models;

namespace search
{
    public class KeysCommander : TextBlock
    {
        public KeysCommander()
        {
            IsVisibleChanged += (o, e) => { if (!(bool)e.NewValue) Cancel(); };
        }
        public Func<IEnumerable<INode>> nodes = Enumerable.Empty<INode>;

        public IEnumerable<Key> KeysDown() => keysDown.AsEnumerable();
        public IEnumerable<Key> CommandKeys() => commandKeys.AsEnumerable();

        public static readonly DependencyProperty IsReceivingCommandKeysProperty = DependencyProperty.Register(
            nameof(IsReceivingCommandKeys), typeof(bool), typeof(KeysCommander), new PropertyMetadata(false));

        public bool IsReceivingCommandKeys
        {
            get => (bool)GetValue(IsReceivingCommandKeysProperty);
            private set => SetValue(IsReceivingCommandKeysProperty, value);
        }

        HashSet<Key> keysDown = new();
        List<Key> commandKeys = new(); //All kayes pressed druring this command in time order
        bool commandCanceled;

        public void KeyPressed(Key key)
        {
            if (keysDown.Contains(key)) return; // If you want to press the key again you need to release it first!
            keysDown.Add(key);
            IsReceivingCommandKeys = true;
            // Keep consuming releases after cancellation so no partially held sequence can run.
            if (commandCanceled) return;
            switch (key)
            {
                case Key.Escape:
                    CancelPendingCommand();
                    return;
                case Key.Back: // Backspace => remove last key
                    // Keep the first key (normally Alt/Ctrl/Shift) as the sequence root.
                    // At the root there is no previous step, so Backspace cancels.
                    if (commandKeys.Count > 1) commandKeys.RemoveAt(commandKeys.Count - 1);
                    else CancelPendingCommand();
                    break;
                default: // Add key to the command
                    commandKeys.Add(key);
                    break;
            }
            OnChange();
        }

        /// <summary>
        /// Fired before command execution
        /// </summary>
        public event Action OnCommand;

        public bool KeyReleased(Key key)
        {
            var handled = false;
            var wasTracked = keysDown.Remove(key);
            IsReceivingCommandKeys = keysDown.Count > 0;
            if (keysDown.Count == 0)
            {
                //IsOpen = false;
                // Run the command action
                (CommandTree.Command cmd, IEnumerable<Key> arg) command = commandCanceled
                    ? (null, Enumerable.Empty<Key>())
                    : Commands.Find(commandKeys);
                if (command.cmd != null)
                {
                    OnCommand?.Invoke();
                    var c = Cursor;
                    try
                    {
                        Cursor = Cursors.Wait;
                        command.cmd(nodes().ToArray(), command.arg);
                    }
                    finally
                    {
                        Cursor = c;
                    }
                    handled = true;
                }

                // Load the main menu
                commandKeys.Clear();
                commandCanceled = false;
                OnChange();
            }
            else OnChange();
            return handled || wasTracked;
        }

        public void CancelPendingCommand()
        {
            if (!IsReceivingCommandKeys) return;
            commandKeys.Clear();
            commandCanceled = true;
            OnChange();
        }

        public void Cancel()
        {
            keysDown.Clear();
            IsReceivingCommandKeys = false;
            commandCanceled = false;

            // Load the main menu
            commandKeys.Clear();
            OnChange();
        }

        // Update help text in tooltip
        public CommandTree Commands = new(Key.Apps, "Main Help");
        public void OnChange()
        {
            if (commandCanceled)
            {
                Text = L.Text("SequenceCanceled");
                return;
            }
            var currentNodes = nodes().ToArray();
            var filtered = Commands.Children.AsEnumerable().Where(x => x.Visible(currentNodes)).ToArray();
            var title = "";
            var level = 1;
            foreach (var key in commandKeys.ToArray())
            {
                filtered = filtered.Where(x => x.Key == key).SelectMany(x =>
                {
                    title += " " + x.Help;
                    return x.Children;
                }).Where(x => x.Visible(currentNodes)).ToArray();
                if (filtered.Count() > 1) level++;
            }
            StringBuilder sb = new();
            if (commandKeys.Count > 0)
            {
                var textArg = commandKeys.Skip(level).ReadTill();
                if (textArg.Length > 0) textArg = $"'{textArg}'";
                sb.AppendLine((string.Join("", commandKeys.Take(level).Select(x => $"<{GetKeyDisplayName(x)}>")) + // Level command
                    $" {title} {textArg}")); // description 'text'
                if (filtered.Count() > 0) sb.AppendLine("==--->");
            }
            foreach (var h in filtered)
            {
                var submenu = h.Children.Any(x => x.Visible(currentNodes)) ? " ›" : "";
                sb.AppendLine(h.Key == Key.None ? h.Help + submenu : $"<{GetKeyDisplayName(h.Key)}> {h.Help}{submenu}");
            }
            var canGoBack = commandKeys.Count > 1;
            var altIsDown = keysDown.Contains(Key.LeftAlt) || keysDown.Contains(Key.RightAlt);
            var canCancelWithEscape = !altIsDown && commandKeys.Count > 0 && Commands.Find(commandKeys).cmd != null;
            if (canGoBack || canCancelWithEscape)
            {
                sb.AppendLine("────────────");
                if (canGoBack) sb.AppendLine($"<Backspace> {L.Text("PreviousStep")}");
                if (canCancelWithEscape) sb.AppendLine($"<Esc> {L.Text("CancelSequence")}");
            }
            Text = sb.ToString();
        }

        private string GetKeyDisplayName(Key key) => key switch
        {
            Key.Add or Key.OemPlus => "+",
            Key.Subtract or Key.OemMinus => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.OemBackslash => "\\",
            Key.Decimal or Key.OemPeriod => ".",
            Key.OemComma => ",",
            _ => key.ToString()
        };
    }

    public class CommandTree
    {
        // Command to be run on given nodes
        public delegate void Command(IEnumerable<INode> nodes, IEnumerable<Key> arg);

        public (Command cmd, IEnumerable<Key> arg) Find(IEnumerable<Key> Keys)
        {
            var key = Keys.FirstOrDefault();
            var ret = Children.FirstOrDefault(c => c.Key == key)?.Find(Keys.Skip(1)) ?? (Cmd, Keys);
            return ret.cmd != null ? ret : (Cmd, Keys);
        }

        public CommandTree() { }


        public static implicit operator CommandTree(ValueTuple<Key, string> v) => new CommandTree(v.Item1, v.Item2);
        public static implicit operator CommandTree(ValueTuple<Key, string, Command> v) => new CommandTree(v.Item1, v.Item2, v.Item3);
        public static implicit operator CommandTree(ValueTuple<Key, string, CommandTree> v)
        {
            var kt = new CommandTree(v.Item1, v.Item2);
            kt.Children.Add(v.Item3);
            return kt;
        }
        public static implicit operator CommandTree(ValueTuple<Key, string, Command, CommandTree> v)
        {
            var kt = new CommandTree(v.Item1, v.Item2, v.Item3);
            kt.Children.Add(v.Item4);
            return kt;
        }
        public static implicit operator CommandTree(ValueTuple<Key, string, CommandTree[]> v)
        {
            var kt = new CommandTree(v.Item1, v.Item2);
            kt.Children.AddRange(v.Item3);
            return kt;
        }
        public static implicit operator CommandTree(ValueTuple<Key, string, Command, CommandTree[]> v)
        {
            var kt = new CommandTree(v.Item1, v.Item2, v.Item3);
            kt.Children.AddRange(v.Item4);
            return kt;
        }

        public CommandTree(Key key, string help, Command command = null)
        {
            Key = key;
            // Command descriptions use their English source text as a RESX key. This
            // also keeps plug-in/test commands safe: unknown keys simply display as-is.
            Help = L.Text(help);
            Cmd = command;
        }

        public static implicit operator CommandTree(ValueTuple<Key, string, Func<IEnumerable<INode>, bool>> v) => new CommandTree(v.Item1, v.Item2) { Visible = v.Item3 };
        public static implicit operator CommandTree(ValueTuple<Key, string, Func<IEnumerable<INode>, bool>, Command> v) => new CommandTree(v.Item1, v.Item2, v.Item4) { Visible = v.Item3 };
        public static implicit operator CommandTree(ValueTuple<Key, string, Func<IEnumerable<INode>, bool>, CommandTree> v)
        {
            CommandTree ct = (v.Item1, v.Item2, v.Item4);
            ct.Visible = v.Item3;
            return ct;
        }

        public CommandTree Add(params CommandTree[] kts)
        {
            Children.AddRange(kts);
            return this;
        }

        public CommandTree Add(Key key, string help, Command command = null) => Add(new CommandTree(key, help, command));

        public Key Key;
        public string Help;
        public List<CommandTree> Children = new();
        public Command Cmd = null;
        public Func<IEnumerable<INode>, bool> Visible = n => true;
    };
}
