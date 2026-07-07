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

        public bool IsReceivingCommandKeys => keysDown.Count > 0;

        HashSet<Key> keysDown = new();
        List<Key> commandKeys = new(); //All kayes pressed druring this command in time order

        public void KeyPressed(Key key)
        {
            if (keysDown.Contains(key)) return; // If you want to press the key again you need to release it first!
            keysDown.Add(key);
            switch (key)
            {
                case Key.Escape:
                    Cancel();
                    return;
                case Key.Back: // Backspace => remove last key
                    if (commandKeys.Count > 0) commandKeys.RemoveAt(commandKeys.Count - 1);
                    else Cancel();
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
            keysDown.Remove(key);
            if (keysDown.Count == 0)
            {
                //IsOpen = false;
                // Run the command action
                var cmd = Commands.Find(commandKeys);
                if (cmd.cmd != null)
                {
                    OnCommand?.Invoke();
                    var c = Cursor;
                    try
                    {
                        Cursor = Cursors.Wait;
                        cmd.cmd(nodes().ToArray(), cmd.arg);
                    }
                    finally
                    {
                        Cursor = c;
                    }
                    handled = true;
                }

                // Load the main menu
                commandKeys.Clear();
                OnChange();
            }
            else OnChange();
            return handled;
        }

        public void Cancel()
        {
            keysDown.Clear();

            // Load the main menu
            commandKeys.Clear();
            OnChange();
        }

        // Update help text in tooltip
        public CommandTree Commands = new(Key.Apps, "Main Help");
        public void OnChange()
        {
            var filtered = Commands.Children.AsEnumerable().Where(x => x.Visible(nodes())).ToArray();
            var title = "";
            var level = 1;
            foreach (var key in commandKeys.ToArray())
            {
                filtered = filtered.Where(x => x.Key == key).SelectMany(x =>
                {
                    title += " " + x.Help;
                    return x.Children;
                }).Where(x => x.Visible(nodes())).ToArray();
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
            foreach (var h in filtered) sb.AppendLine(h.Key == Key.None ? h.Help : $"<{GetKeyDisplayName(h.Key)}> {h.Help}");
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
            Help = help;
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
