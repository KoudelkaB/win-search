using System;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Xunit;

namespace search.Tests
{
    public class KeysCommanderTests
    {
        [Fact]
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

        static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }
    }
}
