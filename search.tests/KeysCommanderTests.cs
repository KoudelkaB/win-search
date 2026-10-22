using System;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace search.Tests
{
    public class KeysCommanderTests
    {
        [AvaloniaFact]
        public void ActiveSequenceShowsOnlyCurrentChoicesAndFlowControls()
        {
            RunSta(() =>
            {
                var executed = false;
                var commander = new KeysCommander();
                commander.Commands.Add(new CommandTree[]
                {
                    (Key.LeftAlt, "ALT", new CommandTree[]
                    {
                        (Key.V, "paste", (n, a) => executed = true,
                            new CommandTree[] { (Key.L, "as link") })
                    }),
                    (Key.LeftCtrl, "CTRL", new CommandTree[] { (Key.C, "copy") }),
                    (Key.C, "copy", (n, a) => executed = true)
                });

                commander.KeyPressed(Key.LeftAlt);

                Assert.True(commander.IsReceivingCommandKeys);
                Assert.Contains("<V> paste ›", commander.Text);
                Assert.DoesNotContain("CTRL", commander.Text);
                Assert.DoesNotContain("<Esc>", commander.Text);
                Assert.DoesNotContain("<Backspace>", commander.Text);

                commander.KeyPressed(Key.V);

                Assert.Contains($"<L> {L.Text("as link")}", commander.Text);
                Assert.DoesNotContain($"<L> {L.Text("as link")} ›", commander.Text);
                Assert.DoesNotContain("<C> copy", commander.Text);
                Assert.Contains($"<Backspace> {L.Text("PreviousStep")}", commander.Text);
                Assert.DoesNotContain("<Esc>", commander.Text);

                // Escape becomes available as soon as Alt is released while another
                // sequence key remains held.
                Assert.True(commander.KeyReleased(Key.LeftAlt));
                Assert.Contains($"<Esc> {L.Text("CancelSequence")}", commander.Text);
                commander.KeyPressed(Key.Escape);
                Assert.Contains(L.Text("SequenceCanceled").Split('\n')[0], commander.Text);
                Assert.True(commander.KeyReleased(Key.Escape));
                Assert.True(commander.KeyReleased(Key.V));
                Assert.False(executed);

                commander.KeyPressed(Key.LeftAlt);
                commander.KeyPressed(Key.V);
                Assert.True(commander.KeyReleased(Key.V));

                commander.KeyPressed(Key.Back);
                Assert.True(commander.KeyReleased(Key.Back));

                Assert.Equal(new[] { Key.LeftAlt }, commander.CommandKeys());
                Assert.Contains("<V> paste", commander.Text);
                Assert.DoesNotContain("<Esc>", commander.Text);

                Assert.True(commander.KeyReleased(Key.LeftAlt));
                Assert.False(executed);

                commander.KeyPressed(Key.C);
                Assert.Contains($"<Esc> {L.Text("CancelSequence")}", commander.Text);

                commander.KeyPressed(Key.Escape);
                Assert.True(commander.IsReceivingCommandKeys);
                Assert.Contains(L.Text("SequenceCanceled").Split('\n')[0], commander.Text);
                Assert.True(commander.KeyReleased(Key.Escape));
                Assert.True(commander.KeyReleased(Key.C));

                Assert.False(commander.IsReceivingCommandKeys);
                Assert.Empty(commander.CommandKeys());
                Assert.False(executed);
            });
        }

        [AvaloniaFact]
        public void PlusOfTheMainKeyBlockReachesThePlusCommand()
        {
            RunSta(() =>
            {
                var executed = "";
                var commander = new KeysCommander();
                commander.Commands.Add(new CommandTree[]
                {
                    (Key.C, "copy", (n, a) => executed = "copy", new CommandTree[]
                    {
                        (Key.Add, "append", (n, a) => executed = "append")
                    }),
                    (Key.OemPlus, "own command", (n, a) => executed = "own")
                });

                //Only the numeric keypad sends Key.Add - a laptop has just the main-block key
                commander.KeyPressed(Key.C);
                commander.KeyPressed(Key.OemPlus);
                Assert.Contains("append", commander.Text);
                commander.KeyReleased(Key.OemPlus);
                commander.KeyReleased(Key.C);
                Assert.Equal("append", executed);

                //Where the key has a command of its own, it stays that key
                commander.KeyPressed(Key.OemPlus);
                commander.KeyReleased(Key.OemPlus);
                Assert.Equal("own", executed);
            });
        }

        [AvaloniaTheory]
        [InlineData(Key.LeftShift)]
        [InlineData(Key.RightShift)]
        public void ShiftHeldToTypePlusIsNotAStepOfTheSequence(Key shift)
        {
            RunSta(() =>
            {
                var executed = "";
                var arguments = Array.Empty<Key>();
                var commander = new KeysCommander();
                commander.Commands.Add(new CommandTree[]
                {
                    (Key.C, "copy", (n, a) => { executed = "copy"; arguments = a.ToArray(); }, new CommandTree[]
                    {
                        (Key.Add, "append", (n, a) => { executed = "append"; arguments = a.ToArray(); },
                            new CommandTree[] { (Key.V, "version") })
                    }),
                    (Key.LeftShift, "SHIFT", new CommandTree[]
                    {
                        (Key.Delete, "delete", (n, a) => executed = "delete"),
                        (Key.Add, "shifted plus", (n, a) => executed = "shifted plus")
                    })
                });

                //A US keyboard types "+" as Shift+OemPlus: C, Shift, OemPlus
                commander.KeyPressed(Key.C);
                commander.KeyPressed(shift);
                commander.KeyPressed(Key.OemPlus);
                Assert.Equal(new[] { Key.C, Key.Add }, commander.CommandKeys());
                Assert.Contains("<V> version", commander.Text);
                commander.KeyReleased(Key.OemPlus);
                commander.KeyReleased(shift);
                commander.KeyReleased(Key.C);
                Assert.Equal("append", executed);
                Assert.Empty(arguments);

                //...and what follows the "+" is still its argument
                commander.KeyPressed(Key.C);
                commander.KeyPressed(shift);
                commander.KeyPressed(Key.OemPlus);
                commander.KeyReleased(shift);
                commander.KeyPressed(Key.V);
                commander.KeyReleased(Key.V);
                commander.KeyReleased(Key.OemPlus);
                commander.KeyReleased(Key.C);
                Assert.Equal("append", executed);
                Assert.Equal(new[] { Key.V }, arguments);

                //A Shift that is a step of its own keeps that meaning
                commander.KeyPressed(Key.LeftShift);
                commander.KeyPressed(Key.OemPlus);
                Assert.Equal(new[] { Key.LeftShift, Key.Add }, commander.CommandKeys());
                commander.KeyReleased(Key.OemPlus);
                commander.KeyReleased(Key.LeftShift);
                Assert.Equal("shifted plus", executed);

                //Shift before any other key is not swallowed
                commander.KeyPressed(Key.C);
                commander.KeyPressed(shift);
                commander.KeyPressed(Key.V);
                Assert.Equal(new[] { Key.C, shift, Key.V }, commander.CommandKeys());
                commander.Cancel();
            });
        }

        //[AvaloniaFact] already runs the test on the Avalonia UI thread
        static void RunSta(Action action) => action();
    }
}
