using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using search.Models;

namespace search
{
    /// <summary>
    /// Runs keyboard sequences (all keys of a command held or typed, the command runs when
    /// the last key is released) and shows the choices of the current level as text.
    /// </summary>
    public class KeysCommander : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        public KeysCommander()
        {
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && !IsVisible) Cancel();
        }

        public Func<IEnumerable<INode>> nodes = Enumerable.Empty<INode>;

        public IEnumerable<Key> KeysDown() => keysDown.AsEnumerable();
        public IEnumerable<Key> CommandKeys() => commandKeys.AsEnumerable();

        public static readonly DirectProperty<KeysCommander, bool> IsReceivingCommandKeysProperty =
            AvaloniaProperty.RegisterDirect<KeysCommander, bool>(nameof(IsReceivingCommandKeys), o => o.IsReceivingCommandKeys);

        bool isReceivingCommandKeys;
        public bool IsReceivingCommandKeys
        {
            get => isReceivingCommandKeys;
            private set
            {
                if (SetAndRaise(IsReceivingCommandKeysProperty, ref isReceivingCommandKeys, value))
                    ReceivingChanged?.Invoke();
            }
        }

        /// <summary>Raised when a sequence starts or ends (the hints panel follows it)</summary>
        public event Action ReceivingChanged;

        HashSet<Key> keysDown = new();
        List<Key> commandKeys = new(); //All keys pressed during this command in time order
        bool commandCanceled;

        /// <summary>
        /// A key went down. keySymbol is the text the key types on the current layout
        /// (KeyEventArgs.KeySymbol) - it tells which key carries "+".
        /// </summary>
        public void KeyPressed(Key key, string keySymbol = null)
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
                    commandKeys.Add(AsCommandKey(key, keySymbol));
                    break;
            }
            OnChange();
        }

        /// <summary>
        /// The key as the command tree names it. "+" is a command key (C then + appends to the
        /// clipboard), but only the numeric keypad sends Key.Add. On the main block the
        /// character sits on a layout-dependent key - OemPlus on a US keyboard, the "1" key on
        /// a Czech one. Such a key counts as "+" only where a "+" command is offered and the key
        /// has no command of its own, so digits and typed names are read as before.
        /// A US keyboard types "+" with Shift held. That Shift has already been recorded as a
        /// key of the sequence, where it matches no command and would turn "C, +" into a plain
        /// C with a Shift argument - it only produced the character, so it is taken back out.
        /// </summary>
        Key AsCommandKey(Key key, string keySymbol)
        {
            if (key == Key.Add) return key;
            var level = Commands;
            List<int> typingShifts = null;
            for (var index = 0; index < commandKeys.Count; index++)
            {
                var pressed = commandKeys[index];
                var next = level.Children.FirstOrDefault(c => c.Key == pressed);
                if (next != null) level = next;
                // A Shift with a command of its own was followed above; any other is a modifier
                else if (pressed is Key.LeftShift or Key.RightShift) (typingShifts ??= new()).Add(index);
                else return key;
            }
            if (level.Children.All(c => c.Key != Key.Add) || level.Children.Any(c => c.Key == key))
                return key;
            if (!TypesPlus(key, keySymbol)) return key;
            if (typingShifts != null)
                for (var i = typingShifts.Count - 1; i >= 0; i--) commandKeys.RemoveAt(typingShifts[i]);
            return Key.Add;
        }

        /// <summary>
        /// The key carries "+": by its name, or as the character it types on the current
        /// keyboard layout (the key symbol of the event, Shift included when it is held)
        /// </summary>
        internal static bool TypesPlus(Key key, string keySymbol = null)
            => key == Key.OemPlus || keySymbol == "+";

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
                        Cursor = new Cursor(StandardCursorType.Wait);
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
            var filtered = Commands.Children.Select(x => x.ForNodes(currentNodes)).Where(x => x != null && x.Visible(currentNodes)).ToArray();
            var title = "";
            var level = 0;
            foreach (var key in commandKeys.ToArray())
            {
                var matching = filtered.Where(x => x.Key == key).ToArray();
                if (matching.Length > 0) level++;
                filtered = matching.SelectMany(x =>
                {
                    title += " " + x.Help;
                    return x.Children;
                }).Select(x => x.ForNodes(currentNodes)).Where(x => x != null && x.Visible(currentNodes)).ToArray();
            }
            level = Math.Max(1, level);
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
                var submenu = h.Children.Select(x => x.ForNodes(currentNodes)).Any(x => x != null && x.Visible(currentNodes)) ? " ›" : "";
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
            // Named as on the keyboard, in the help and in the menus - not "Return"/"Escape"
            Key.Return => "Enter",
            Key.Escape => "Esc",
            >= Key.D0 and <= Key.D9 => (key - Key.D0).ToString(),
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
        public Func<IEnumerable<INode>, CommandTree> ResolveForNodes;
        public CommandTree ForNodes(IEnumerable<INode> nodes) => ResolveForNodes == null ? this : ResolveForNodes(nodes);
    };
}
