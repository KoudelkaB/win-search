using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using search.Models;
using Xunit;

namespace search.Tests
{
    public class DirectoryWalkerTests
    {
        //The walk then uses the managed enumeration, which the other tests cover
        const string NoNativeListing = "getdents64 + statx listing unavailable here (Windows, or Linux without glibc 2.30+, statx or /proc)";

        static string NewTree()
        {
            var root = Path.Combine(Path.GetTempPath(), "win-search-walk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        static Dictionary<string, INode> Walk(string root, bool nativeListing)
            => DirectoryWalker.Walk(new DirectoryInfo(root), TimeSpan.FromSeconds(15),
                CancellationToken.None, nativeListing).ToDictionary(n => n.FullName, StringComparer.Ordinal);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WalkAggregatesEveryDescendantWithoutCountingTheFolderItself(bool nativeListing)
        {
            Assert.SkipWhen(nativeListing && !LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewTree();
            try
            {
                var docs = Path.Combine(root, "Docs");
                var sub = Path.Combine(docs, "Sub");
                Directory.CreateDirectory(sub);
                File.WriteAllBytes(Path.Combine(docs, "a.txt"), new byte[5]);
                File.WriteAllBytes(Path.Combine(sub, "b.txt"), new byte[10]);

                var nodes = Walk(root, nativeListing);

                Assert.Equal(5, nodes.Count); //The walked folder itself is a node, like a drive root
                Assert.Equal(4U, nodes[root].Count); //Docs + a.txt + Sub + b.txt
                Assert.Equal(3U, nodes[docs].Count); //a.txt + Sub + b.txt
                Assert.Equal(1U, nodes[sub].Count);
                Assert.Equal(1U, nodes[Path.Combine(docs, "a.txt")].Count);
                Assert.Equal(15UL, nodes[root].Size);
                Assert.Equal(15UL, nodes[docs].Size);
                Assert.Equal(10UL, nodes[sub].Size);
                Assert.Equal(5UL, nodes[Path.Combine(docs, "a.txt")].Size);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [DllImport("libc", SetLastError = true)]
        static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        /// <summary>
        /// The getdents64 + statx listing must build exactly the nodes of the managed
        /// enumeration - watchers, publish and index comparison rely on them
        /// </summary>
        [Fact]
        public void NativeListingBuildsTheSameNodesAsTheManagedEnumeration()
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewTree();
            var locked = Path.Combine(root, "locked");
            var unsearchable = Path.Combine(root, "unsearchable");
            try
            {
                string P(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
                File.WriteAllText(P("a.txt"), "abc");
                File.WriteAllBytes(P("empty.bin"), Array.Empty<byte>());
                File.WriteAllText(P(".hidden"), "1234");
                File.WriteAllText(P("readonly.txt"), "ro");
                File.SetUnixFileMode(P("readonly.txt"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.WriteAllText(P("žluťoučký kůň.txt"), "ü");
                Directory.CreateDirectory(P("sub", "deep"));
                File.WriteAllText(P("sub", "deep", "c.log"), "1234567");
                Directory.CreateDirectory(P(".hiddendir"));
                File.WriteAllText(P(".hiddendir", "x"), "x");
                File.CreateSymbolicLink(P("link-to-file"), "a.txt");
                Directory.CreateSymbolicLink(P("link-to-dir"), "sub");
                File.CreateSymbolicLink(P("dangling"), "missing");
                Assert.Equal(0, mkfifo(P("fifo"), Convert.ToUInt32("644", 8)));
                Directory.CreateDirectory(locked);
                File.WriteAllText(Path.Combine(locked, "inside.txt"), "secret");
                File.SetUnixFileMode(locked, UnixFileMode.None);
                //Readable but not searchable: the names list, their metadata does not
                Directory.CreateDirectory(unsearchable);
                File.WriteAllText(Path.Combine(unsearchable, "f.txt"), "f");
                File.SetUnixFileMode(unsearchable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetLastWriteTimeUtc(P("a.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567));
                File.SetLastWriteTimeUtc(P("empty.bin"), new DateTime(1960, 6, 1, 0, 0, 0, DateTimeKind.Utc));
                //Last, so the files created inside do not bump it again
                Directory.SetLastWriteTimeUtc(P("sub"), new DateTime(2021, 7, 8, 9, 10, 11, DateTimeKind.Utc));

                var managed = Walk(root, nativeListing: false);
                var native = Walk(root, nativeListing: true);

                var onlyManaged = managed.Keys.Except(native.Keys).ToArray();
                var onlyNative = native.Keys.Except(managed.Keys).ToArray();
                Assert.True(onlyManaged.Length == 0 && onlyNative.Length == 0,
                    $"only managed: {string.Join(", ", onlyManaged)}; only native: {string.Join(", ", onlyNative)}");
                foreach (var (path, expected) in managed)
                {
                    var actual = native[path];
                    Assert.True(expected.GetType() == actual.GetType(), $"{path}: {expected.GetType().Name} != {actual.GetType().Name}");
                    Assert.True(expected.Attributes == actual.Attributes, $"{path}: {expected.Attributes} != {actual.Attributes}");
                    Assert.True(expected.Size == actual.Size, $"{path}: size {expected.Size} != {actual.Size}");
                    Assert.True(expected.Count == actual.Count, $"{path}: count {expected.Count} != {actual.Count}");
                    Assert.True(expected.LastChangeTime == actual.LastChangeTime, $"{path}: {expected.LastChangeTime:O} != {actual.LastChangeTime:O}");
                    Assert.Equal(expected.Exists, actual.Exists);
                }

                //...and both are what the rest of the app expects
                Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567).ToLocalTime(), native[P("a.txt")].LastChangeTime);
                Assert.Equal(new DateTime(1960, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime(), native[P("empty.bin")].LastChangeTime);
                Assert.Equal(new DateTime(2021, 7, 8, 9, 10, 11, DateTimeKind.Utc).ToLocalTime(), native[P("sub")].LastChangeTime);
                Assert.Equal(3UL, native[P("a.txt")].Size);
                Assert.Equal(FileAttributes.Normal, native[P("a.txt")].Attributes);
                Assert.True(native[P(".hidden")].Attributes.HasFlag(FileAttributes.Hidden));
                Assert.True(native[P(".hiddendir")].Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.Directory));
                Assert.True(native.ContainsKey(P("žluťoučký kůň.txt")));
                Assert.Equal(7UL, native[P("sub", "deep", "c.log")].Size);
                Assert.Equal(2U, native[P("sub")].Count);
                Assert.Equal(7UL, native[P("sub")].Size);
                //A link is the link itself: its own size, the target's directory flag, never descended
                Assert.IsType<FileNode>(native[P("link-to-file")]);
                Assert.Equal(FileAttributes.ReparsePoint, native[P("link-to-file")].Attributes);
                Assert.Equal(5UL, native[P("link-to-file")].Size); //strlen("a.txt")
                Assert.Equal(FileAttributes.Directory | FileAttributes.ReparsePoint, native[P("link-to-dir")].Attributes);
                Assert.Equal(0U, native[P("link-to-dir")].Count);
                Assert.False(native.ContainsKey(P("link-to-dir", "deep")));
                Assert.Equal(FileAttributes.ReparsePoint, native[P("dangling")].Attributes);
                Assert.True(native.ContainsKey(P("fifo")));
                Assert.True(native[P("locked")].IsDirectory);
                if (Environment.UserName != "root") //root reads everything
                {
                    Assert.True(native[P("readonly.txt")].Attributes.HasFlag(FileAttributes.ReadOnly));
                    Assert.False(native.ContainsKey(Path.Combine(locked, "inside.txt")));
                    //The managed enumeration throws on the first entry it cannot stat
                    Assert.True(native[unsearchable].IsDirectory);
                    Assert.False(native.ContainsKey(Path.Combine(unsearchable, "f.txt")));
                }
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(unsearchable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(root, true);
            }
        }

        static byte[] Dirent(ulong ino, byte type, string name, int? reclen = null)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            var length = reclen ?? (19 + bytes.Length + 1 + 7) & ~7; //The kernel pads records to 8 bytes
            var record = new byte[Math.Max(length, 19 + bytes.Length)];
            BitConverter.TryWriteBytes(record.AsSpan(0), ino);
            BitConverter.TryWriteBytes(record.AsSpan(8), 0x1234L); //d_off - unused
            BitConverter.TryWriteBytes(record.AsSpan(16), (ushort)length);
            record[18] = type;
            bytes.CopyTo(record, 19);
            return record;
        }

        [Fact]
        public void DirentParserKeepsEveryNameButTheDotEntries()
        {
            var reader = new LinuxDirectoryReader();
            reader.ParseBatch(new[]
            {
                Dirent(2, LinuxDirectoryReader.DT_DIR, "."),
                Dirent(1, LinuxDirectoryReader.DT_DIR, ".."),
                Dirent(42, LinuxDirectoryReader.DT_REG, "a.txt"),
                Dirent(7, LinuxDirectoryReader.DT_DIR, "sub"),
                Dirent(ulong.MaxValue, LinuxDirectoryReader.DT_UNKNOWN, "žluťoučký kůň"),
                Dirent(9, LinuxDirectoryReader.DT_LNK, "..."),
                Dirent(10, LinuxDirectoryReader.DT_REG, ".x"),
                Dirent(11, LinuxDirectoryReader.DT_REG, new string('n', 255)),
            }.SelectMany(r => r).ToArray());

            Assert.Equal(new (ulong, byte, string)[]
            {
                (42, LinuxDirectoryReader.DT_REG, "a.txt"),
                (7, LinuxDirectoryReader.DT_DIR, "sub"),
                (ulong.MaxValue, LinuxDirectoryReader.DT_UNKNOWN, "žluťoučký kůň"),
                (9, LinuxDirectoryReader.DT_LNK, "..."),
                (10, LinuxDirectoryReader.DT_REG, ".x"),
                (11, LinuxDirectoryReader.DT_REG, new string('n', 255)),
            }, reader.CollectedEntries());

            //Each getdents64 batch is added before the next is read - the next one replaces it
            reader.ParseBatch(Dirent(12, LinuxDirectoryReader.DT_REG, "next"));
            Assert.Equal(new (ulong, byte, string)[] { (12, LinuxDirectoryReader.DT_REG, "next") },
                reader.CollectedEntries());
        }

        [Fact]
        public void DirentParserStopsAtAMalformedRecord()
        {
            var reader = new LinuxDirectoryReader();
            //A name filling its record up to the end has no NUL - the record length ends it
            var unterminated = Dirent(5, LinuxDirectoryReader.DT_REG, "abcde", reclen: 24);
            reader.ParseBatch(new[]
            {
                Dirent(3, LinuxDirectoryReader.DT_REG, "ok"),
                unterminated,
                Dirent(4, LinuxDirectoryReader.DT_REG, "cut")[..20], //Truncated: claims more than is left
            }.SelectMany(r => r).ToArray());
            Assert.Equal(new (ulong, byte, string)[]
            {
                (3, LinuxDirectoryReader.DT_REG, "ok"),
                (5, LinuxDirectoryReader.DT_REG, "abcde"),
            }, reader.CollectedEntries());

            reader.ParseBatch(Dirent(6, LinuxDirectoryReader.DT_REG, "zero", reclen: 0)); //Would never advance
            Assert.Empty(reader.CollectedEntries());
        }

        [Fact]
        public void AHungCheckAnswersNoByItsDeadlineWithoutHoldingUpLaterAskers()
        {
            using var release = new ManualResetEventSlim(false);
            var check = new TimedCheck(() => { release.Wait(); return true; }, TimeSpan.FromSeconds(1), "hung test check");
            var clock = Stopwatch.StartNew();
            Assert.False(check.Passed);
            Assert.InRange(clock.ElapsedMilliseconds, 500, 10_000); //Waited for the deadline, not for the check
            clock.Restart();
            Assert.False(check.Passed);
            Assert.InRange(clock.ElapsedMilliseconds, 0, 500); //The deadline is shared, not one per asker
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => check.Passed, TimeSpan.FromSeconds(10))); //A late answer still counts
        }

        [Fact]
        public void AFinishedCheckGivesItsAnswerAndAThrowingOneSaysNo()
        {
            Assert.True(new TimedCheck(() => true, TimeSpan.FromSeconds(30), "test check").Passed);
            Assert.False(new TimedCheck(() => false, TimeSpan.FromSeconds(30), "test check").Passed);
            Assert.False(new TimedCheck(() => throw new EntryPointNotFoundException(), TimeSpan.FromSeconds(30), "test check").Passed);
        }

        /// <summary>Every getdents64 call after a reader's first one hangs, like a dead network mount</summary>
        sealed unsafe class HangingReader : LinuxDirectoryReader
        {
            readonly ManualResetEventSlim release;
            int calls;
            public HangingReader(ManualResetEventSlim release) => this.release = release;

            internal override nint GetDents(int fd, byte* buffer, nuint count)
            {
                if (calls++ == 0) return base.GetDents(fd, buffer, count);
                release.Wait();
                return 0;
            }
        }

        static Func<LinuxDirectoryReader> Reader => LinuxDirectoryReader.IsSupported ? () => new LinuxDirectoryReader() : null;

        /// <summary>Folders with files, two levels deep, a link to a folder and an unreadable folder</summary>
        static string NewWideTree(int folders, int filesEach)
        {
            var root = NewTree();
            for (var f = 0; f < folders; f++)
            {
                var folder = Path.Combine(root, "d" + f, "inner");
                Directory.CreateDirectory(folder);
                for (var i = 0; i < filesEach; i++)
                    File.WriteAllBytes(Path.Combine(i % 2 == 0 ? folder : Path.GetDirectoryName(folder), $"f{i}.bin"), new byte[i + f]);
            }
            if (OperatingSystem.IsLinux())
            {
                File.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "d0"));
                var closed = Directory.CreateDirectory(Path.Combine(root, "closed")).FullName;
                File.WriteAllText(Path.Combine(closed, "x"), "x");
                File.SetUnixFileMode(closed, UnixFileMode.None);
            }
            return root;
        }

        static void DeleteWideTree(string root)
        {
            if (OperatingSystem.IsLinux() && Directory.Exists(Path.Combine(root, "closed")))
                File.SetUnixFileMode(Path.Combine(root, "closed"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, true);
        }

        [Theory]
        [InlineData(int.MaxValue, false)]
        [InlineData(3, false)] //A folder's entries in several batches
        [InlineData(1, false)]
        [InlineData(3, true)] //Every folder's entries before the folder itself
        public void AStreamedWalkWithItsFolderTotalsIsTheWalk(int batch, bool reversed)
        {
            var root = NewWideTree(folders: 20, filesEach: 6);
            try
            {
                var groups = new List<(FileNode Folder, IReadOnlyList<FileNode> Entries)>();
                Assert.True(DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromSeconds(15), CancellationToken.None,
                    Reader, (folder, entries, _) => { lock (groups) groups.Add((folder, entries)); }, batch));
                Assert.All(groups, g => Assert.InRange(g.Entries.Count, 1, batch));

                //The root alone first, then the folders' entries as they came - or the other way round
                var streamed = new List<FileNode>();
                var totals = new DirectoryWalker.StreamedTotals();
                Assert.Null(groups[0].Folder);
                totals.Root(groups[0].Entries.Single());
                streamed.Add(groups[0].Entries.Single());
                var rest = groups.Skip(1).ToList();
                if (reversed) rest.Reverse();
                foreach (var (folder, entries) in rest)
                {
                    var pending = totals.FolderAt(folder.FullName);
                    Assert.NotNull(pending);
                    foreach (var entry in entries)
                    {
                        totals.Add(pending, entry);
                        streamed.Add(entry);
                    }
                }
                totals.Complete();
                var walked = Walk(root, LinuxDirectoryReader.IsSupported);

                Assert.Equal(walked.Keys.OrderBy(k => k), streamed.Select(n => n.FullName).OrderBy(k => k));
                foreach (var node in streamed)
                {
                    var expected = walked[node.FullName];
                    Assert.Equal((expected.Attributes, expected.Size, expected.Count), (node.Attributes, node.Size, node.Count));
                }
                Assert.True(walked[root].Count > 100);
            }
            finally
            {
                DeleteWideTree(root);
            }
        }

        [Fact]
        public void AStreamedWalkKeepsNoFileItHandedOver()
        {
            var root = NewWideTree(folders: 40, filesEach: 10);
            try
            {
                var handed = new List<(int Batch, WeakReference File)>();
                var batches = 0;
                var kept = -1;
                DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromSeconds(15), CancellationToken.None, Reader, (_, folder, _) =>
                {
                    lock (handed)
                    {
                        if (++batches == 30)
                        {
                            //Everything handed before - nothing here holds it. Only the other
                            //worker may not have let go of its last folder yet.
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            kept = handed.Count(h => h.Batch < batches - 1 && h.File.IsAlive);
                        }
                        foreach (var node in folder.Where(n => !n.IsDirectory)) handed.Add((batches, new WeakReference(node)));
                    }
                }, maxThreads: 1);

                Assert.True(batches > 30);
                Assert.Equal(0, kept);
            }
            finally
            {
                DeleteWideTree(root);
            }
        }

        [Fact]
        public void AConsumerThatTakesNothingEndsTheStreamedWalkIncomplete()
        {
            var root = NewWideTree(folders: 5, filesEach: 2);
            try
            {
                var released = false;
                var watch = Stopwatch.StartNew();
                var complete = DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300), CancellationToken.None,
                    Reader, (folder, _, token) =>
                    {
                        if (folder == null) return; //The root's node goes through
                        token.WaitHandle.WaitOne(10_000); //Takes nothing more - until the walk ends
                        released = token.IsCancellationRequested;
                        token.ThrowIfCancellationRequested();
                    });

                Assert.False(complete);
                Assert.True(watch.ElapsedMilliseconds < 5000);
                Assert.True(released); //The waiting consumer was let go, not abandoned
            }
            finally
            {
                DeleteWideTree(root);
            }
        }

        [Fact]
        public void AConsumerThatTakesSlowlyButSteadilyKeepsTheStreamedWalkGoing()
        {
            var root = NewTree();
            try
            {
                for (var i = 0; i < 60; i++) File.WriteAllText(Path.Combine(root, $"f{i}.txt"), "x");
                //One folder handed over for 3 s - longer than the stall timeout and the watchdog's
                //1 s look together - each of its batches in less than the stall timeout
                var arrived = 0;
                var complete = DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300), CancellationToken.None,
                    Reader, (folder, entries, _) =>
                    {
                        if (folder != null) Thread.Sleep(100);
                        Interlocked.Add(ref arrived, entries.Count);
                    }, batch: 2);

                Assert.True(complete);
                Assert.Equal(61, arrived);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>A folder with files and a folder in it with files: each listing fits one read</summary>
        static string NewHangingTree()
        {
            var root = NewTree();
            for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(root, $"a{i}.txt"), "a");
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            for (var i = 0; i < 40; i++) File.WriteAllText(Path.Combine(root, "sub", $"c{i}.txt"), "cc");
            return root;
        }

        [Fact]
        public void AHungListingEndsTheStreamedWalkWithEverythingItRead()
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewHangingTree();
            using var release = new ManualResetEventSlim(false);
            try
            {
                //Each directory's first read arrives, the read after it never returns - with
                //batches that never fill: what was read is the walk, as when the walk keeps it
                var handed = new List<string>();
                var complete = DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300), CancellationToken.None,
                    () => new HangingReader(release), (_, entries, _) => { lock (handed) handed.AddRange(entries.Select(e => e.FullName)); },
                    batch: 8192);
                var kept = DirectoryWalker.Walk(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300),
                    CancellationToken.None, () => new HangingReader(release)).Select(n => n.FullName).ToHashSet();

                Assert.True(complete);
                Assert.Equal(82, kept.Count);
                lock (handed)
                {
                    Assert.Equal(kept, handed.ToHashSet());
                    Assert.Equal(handed.Count, handed.Distinct().Count());
                }
            }
            finally
            {
                release.Set(); //Let the abandoned workers go
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void AConsumerThatDoesNotTakeWhatHungListingsReadEndsTheStreamedWalkIncomplete()
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewHangingTree();
            using var release = new ManualResetEventSlim(false);
            try
            {
                var released = false;
                var watch = Stopwatch.StartNew();
                var complete = DirectoryWalker.Stream(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300), CancellationToken.None,
                    () => new HangingReader(release), (folder, _, token) =>
                    {
                        if (folder == null) return; //The root's node goes through
                        token.WaitHandle.WaitOne(10_000); //Takes nothing more - until it is let go
                        released = token.IsCancellationRequested;
                        token.ThrowIfCancellationRequested();
                    }, batch: 8192);

                Assert.False(complete);
                Assert.True(watch.ElapsedMilliseconds < 8000);
                Assert.True(released);
            }
            finally
            {
                release.Set();
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void AHungListingKeepsTheEntriesReadBeforeIt()
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewTree();
            using var release = new ManualResetEventSlim(false);
            try
            {
                File.WriteAllText(Path.Combine(root, "a.txt"), "a");
                File.WriteAllText(Path.Combine(root, "b.txt"), "b");
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "c");

                //Each directory's first batch arrives, the read after it never returns: the
                //stall watchdog abandons the walk, which keeps what was read - the managed
                //enumeration yields every entry as it reads it, too
                var nodes = DirectoryWalker.Walk(new DirectoryInfo(root), TimeSpan.FromMilliseconds(300),
                    CancellationToken.None, () => new HangingReader(release)).Select(n => n.FullName).ToHashSet();

                Assert.Equal(new[] { root, Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt"),
                    Path.Combine(root, "sub"), Path.Combine(root, "sub", "c.txt") }.ToHashSet(), nodes);
            }
            finally
            {
                release.Set(); //Let the abandoned workers go
                Directory.Delete(root, true);
            }
        }

        /// <summary>Every other statx is interrupted by a signal (EINTR), as on FUSE or network mounts</summary>
        sealed unsafe class InterruptedReader : LinuxDirectoryReader
        {
            int calls;
            internal override int StatAt(int dirfd, byte* path, int flags, uint mask, Statx* buffer)
            {
                if (calls++ % 2 == 0)
                {
                    Marshal.SetLastPInvokeError(4); //EINTR
                    return -1;
                }
                return base.StatAt(dirfd, path, flags, mask, buffer);
            }
        }

        [Fact]
        public void AnInterruptedStatIsRepeated()
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, NoNativeListing);
            var root = NewTree();
            try
            {
                File.WriteAllText(Path.Combine(root, "a.txt"), "a");
                Directory.CreateDirectory(Path.Combine(root, "sub"));
                File.WriteAllText(Path.Combine(root, "sub", "c.txt"), "c");
                Directory.CreateSymbolicLink(Path.Combine(root, "link-to-dir"), "sub"); //Its target stat too

                var expected = Walk(root, nativeListing: false);
                var actual = DirectoryWalker.Walk(new DirectoryInfo(root), TimeSpan.FromSeconds(15),
                    CancellationToken.None, () => new InterruptedReader()).ToDictionary(n => n.FullName);

                Assert.Equal(expected.Keys.ToHashSet(), actual.Keys.ToHashSet());
                foreach (var (path, node) in expected)
                {
                    Assert.Equal(node.Attributes, actual[path].Attributes);
                    Assert.Equal(node.Count, actual[path].Count);
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("444", true)]
        [InlineData("555", true)]
        [InlineData("644", false)] //Owned by the user running the tests below
        [InlineData("666", false)]
        [InlineData("000", false)] //Not even readable - .NET does not call that read-only
        [InlineData("777", false)]
        public void ReadOnlyMeansReadableButNotWritableLikeDotNetOnUnix(string mode, bool readOnly)
        {
            Assert.SkipUnless(LinuxDirectoryReader.IsSupported, "the owner check needs the Linux identity");
            Assert.Equal(readOnly, LinuxDirectoryReader.IsReadOnly(Convert.ToInt32(mode, 8), geteuid(), uint.MaxValue));
        }

        [DllImport("libc")] static extern uint geteuid();

        [Fact]
        public void UnixTimesOutsideTheDateRangeKeepTheDefaultLikeFileSystemInfo()
        {
            Assert.Equal(DateTime.UnixEpoch.ToLocalTime(), LinuxDirectoryReader.LocalTime(0, 0));
            Assert.Equal(DateTime.UnixEpoch.AddTicks(15).ToLocalTime(), LinuxDirectoryReader.LocalTime(0, 1599));
            Assert.Equal(default(DateTime), LinuxDirectoryReader.LocalTime(long.MaxValue, 0));
            Assert.Equal(default(DateTime), LinuxDirectoryReader.LocalTime(long.MinValue, 0));
        }
    }
}
