using Xunit;

//One app window at a time on every system: the keys go to the focused window, and the
//clipboard is the same for all - two tests at once would take them from each other
[assembly: CollectionBehavior(DisableTestParallelization = true)]
