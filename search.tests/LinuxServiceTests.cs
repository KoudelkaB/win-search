using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using search.Models;
using search.Models.LinuxService;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// The Linux file system service: its wire format, the fanotify event handling and the
    /// whole socket conversation with a fake change monitor (the real one needs root)
    /// </summary>
    public class LinuxServiceTests
    {
        const string LinuxOnly = "the file system service runs on Linux only";
        /// <summary>This test process's user - known once the native listing probe has run</summary>
        static UserIdentity Me
        {
            get
            {
                _ = LinuxDirectoryReader.IsSupported;
                return LinuxDirectoryReader.ProcessIdentity;
            }
        }

        // --- protocol --------------------------------------------------------------------

        [Fact]
        public void ChangesSurviveTheWire()
        {
            var changes = new[]
            {
                new ServiceChange(ServiceProtocol.Upsert, "/a/ü b.txt", null, ServiceProtocol.ContentHidden,
                    new ServiceMetadata(FileAttributes.ReadOnly | FileAttributes.Hidden, 42, 638_000_000_000_000_000, 0)),
                ServiceChange.Of(ServiceProtocol.Delete, "/a/gone"),
                ServiceChange.Of(ServiceProtocol.Rename, "/a/new", "/a/old"),
                ServiceChange.Of(ServiceProtocol.Rescan)
            };
            var message = ServiceProtocol.Begin(ServiceProtocol.Changes);
            foreach (var c in changes) ServiceProtocol.WriteChange(message.Writer, c);
            using var wire = new MemoryStream();
            ServiceProtocol.Send(wire, message);
            wire.Position = 0;

            using var r = ServiceProtocol.Open(ServiceProtocol.ReadFrame(wire, 1 << 20), out var type);
            var read = changes.Select(_ => ServiceProtocol.ReadChange(r)).ToArray();

            Assert.Equal(ServiceProtocol.Changes, type);
            Assert.Equal(changes, read);
            Assert.Null(ServiceProtocol.ReadFrame(wire, 1 << 20)); //Clean end
        }

        [Fact]
        public void AnOversizedFrameIsRefused()
        {
            using var wire = new MemoryStream();
            ServiceProtocol.WriteFrame(wire, new byte[100]);
            wire.Position = 0;

            Assert.Throws<InvalidDataException>(() => ServiceProtocol.ReadFrame(wire, 99));
        }

        // --- fanotify events -------------------------------------------------------------

        static byte[] Handle(int folder) => BitConverter.GetBytes((long)folder);

        static byte[] Event(ulong mask, params (byte Kind, int Folder, string Name)[] records)
        {
            var body = new List<byte>();
            foreach (var (kind, folder, name) in records)
            {
                var record = new List<byte> { kind, 0, 0, 0 };
                record.AddRange(BitConverter.GetBytes(0x1122334455667788UL)); //fsid
                record.AddRange(BitConverter.GetBytes(8)); //handle_bytes
                record.AddRange(BitConverter.GetBytes(1)); //handle_type
                record.AddRange(Handle(folder));
                if (name != null) record.AddRange(Encoding.UTF8.GetBytes(name + "\0"));
                while (record.Count % 4 != 0) record.Add(0);
                BinaryPrimitives.WriteUInt16LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(record).Slice(2), (ushort)record.Count);
                body.AddRange(record);
            }
            var e = new byte[24 + body.Count];
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)e.Length);
            e[4] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), 24);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), mask);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), -1); //FAN_NOFD
            body.CopyTo(e, 24);
            return e;
        }

        static List<FanotifyMonitor.FanEvent> Parse(params byte[][] events)
        {
            var parsed = new List<FanotifyMonitor.FanEvent>();
            FanotifyMonitor.Parse(events.SelectMany(e => e).ToArray(), parsed);
            return parsed;
        }

        [Fact]
        public void EventsAreReadWithTheirFolderAndName()
        {
            var events = Parse(
                Event(FanotifyMonitor.FAN_CREATE | FanotifyMonitor.FAN_MODIFY, (FanotifyMonitor.INFO_DFID_NAME, 7, "f.txt"), (FanotifyMonitor.INFO_FID, 9, null)),
                Event(FanotifyMonitor.FAN_RENAME, (FanotifyMonitor.INFO_OLD_DFID_NAME, 7, "f.txt"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 8, "ž.txt")));

            Assert.Equal(2, events.Count);
            Assert.Equal(FanotifyMonitor.FAN_CREATE | FanotifyMonitor.FAN_MODIFY, events[0].Mask);
            Assert.Equal((FanotifyMonitor.INFO_DFID_NAME, "f.txt"), (events[0].Records[0].Kind, events[0].Records[0].Name));
            Assert.Equal(Handle(7), events[0].Records[0].Handle);
            Assert.Equal(0x1122334455667788UL, events[0].Records[0].Fsid);
            Assert.Null(events[0].Records[1].Name);
            Assert.Equal(new[] { "f.txt", "ž.txt" }, events[1].Records.Select(r => r.Name));
        }

        [Theory]
        //A plain file system mounted at / and at a nested mount point
        [InlineData("/home/a/b", "/", "/", "/", "/", "/home/a/b")]
        [InlineData("/home/bohdan/Viper/x", "/home/bohdan/Viper", "/", "/home/bohdan/Viper", "/", "/home/bohdan/Viper/x")]
        [InlineData("/home/bohdan/Viper", "/home/bohdan/Viper", "/", "/home/bohdan/Viper", "/", "/home/bohdan/Viper")]
        //btrfs through the private top level: subvolume "root" at /, "home" at /home
        [InlineData("/run/fsm/btrfs/x/root/etc/hosts", "/run/fsm/btrfs/x", "/", "/", "/root", "/etc/hosts")]
        [InlineData("/run/fsm/btrfs/x/home/bohdan/a", "/run/fsm/btrfs/x", "/", "/home", "/home", "/home/bohdan/a")]
        public void ReportedFoldersMapToTheirWatchedRoot(string resolved, string mountPath, string mountFsRoot,
            string root, string fsRoot, string expected)
        {
            var roots = new List<(string, string)> { (root, fsRoot) };
            if (root == "/home") roots.Add(("/", "/root"));

            Assert.Equal((root, expected), FanotifyMonitor.MapToRoot(resolved, mountPath, mountFsRoot, roots));
        }

        [Theory]
        [InlineData("/run/fsm/btrfs/x/rootfs/etc")] //Not under the "/root" subvolume
        [InlineData("/elsewhere/x")]               //Not under the resolving mount
        public void FoldersOutsideEveryWatchedRootAreDropped(string resolved)
        {
            Assert.Null(FanotifyMonitor.MapToRoot(resolved, "/run/fsm/btrfs/x", "/", new[] { ("/", "/root") }));
        }

        /// <summary>BuildChanges with folders named by handle and a fake disk</summary>
        static List<RawChange> Build(IReadOnlyDictionary<int, string> folders, ISet<string> onDisk, params byte[][] events)
            => FanotifyMonitor.BuildChanges(Parse(events),
                r => folders.TryGetValue((int)BitConverter.ToInt64(r.Handle), out var folder)
                    ? ("/", r.Name == "." ? folder : FanotifyMonitor.Join(folder, r.Name)) : null,
                path => onDisk.Contains(path)
                    ? (new RawStat(0x81A4, 1, 1, false, path.EndsWith("dir"), false, 3, 1, 0), true)
                    : (null, false),
                () => new[] { "/" });

        static string Describe(RawChange c) => c.Kind switch
        {
            ServiceProtocol.Upsert => $"upsert {c.Path}{(c.IndexContent ? " +content" : "")}{(c.AccessChanged ? " +access" : "")}",
            ServiceProtocol.Delete => $"delete {c.Path}",
            ServiceProtocol.Rename => $"rename {c.OldPath} -> {c.Path}",
            _ => $"rescan {c.Root}"
        };

        [Fact]
        public void ARenameComesFirstSoAChangeResolvedToTheNewNameFindsItsFolder()
        {
            //A file was written, then its folder renamed: by now the folder's handle names "/newdir"
            var changes = Build(new Dictionary<int, string> { [1] = "/newdir", [2] = "/" },
                new HashSet<string> { "/newdir", "/newdir/file" },
                Event(FanotifyMonitor.FAN_MODIFY, (FanotifyMonitor.INFO_DFID_NAME, 1, "file")),
                Event(FanotifyMonitor.FAN_RENAME | FanotifyMonitor.FAN_ONDIR,
                    (FanotifyMonitor.INFO_OLD_DFID_NAME, 2, "dir"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 2, "newdir")));

            Assert.Equal(new[] { "rename /dir -> /newdir", "upsert /newdir", "upsert /newdir/file" },
                changes.Select(Describe).Where(c => c != "upsert /"));
        }

        [Fact]
        public void OnlyAReportedDeletionDeletes()
        {
            //"gone" vanished without a deletion of its own (a rename reports that); "removed" was deleted
            var changes = Build(new Dictionary<int, string> { [1] = "/d" }, new HashSet<string> { "/d" },
                Event(FanotifyMonitor.FAN_ATTRIB, (FanotifyMonitor.INFO_DFID_NAME, 1, "gone")),
                Event(FanotifyMonitor.FAN_CREATE | FanotifyMonitor.FAN_DELETE, (FanotifyMonitor.INFO_DFID_NAME, 1, "removed")));

            Assert.Equal(new[] { "delete /d/removed", "upsert /d" }, changes.Select(Describe));
        }

        [Fact]
        public void AFolderReportsItsOwnChangeButNotItsOwnDeletion()
        {
            var changes = Build(new Dictionary<int, string> { [1] = "/d", [2] = "/e" }, new HashSet<string> { "/d", "/e" },
                Event(FanotifyMonitor.FAN_ATTRIB | FanotifyMonitor.FAN_ONDIR, (FanotifyMonitor.INFO_DFID_NAME, 1, ".")),
                Event(FanotifyMonitor.FAN_DELETE_SELF | FanotifyMonitor.FAN_ONDIR, (FanotifyMonitor.INFO_DFID_NAME, 2, ".")));

            Assert.Equal(new[] { "upsert /d +access" }, changes.Select(Describe));
        }

        [Fact]
        public void AFolderMovedInFromOutsideIsIndexedWithItsContent()
        {
            //The old folder is in no watched root (locate answers null)
            var changes = Build(new Dictionary<int, string> { [2] = "/in" }, new HashSet<string> { "/in", "/in/movedir" },
                Event(FanotifyMonitor.FAN_RENAME | FanotifyMonitor.FAN_ONDIR,
                    (FanotifyMonitor.INFO_OLD_DFID_NAME, 99, "movedir"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 2, "movedir")));

            Assert.Equal(new[] { "upsert /in/movedir +content", "upsert /in" }, changes.Select(Describe));
        }

        [Fact]
        public void ADeletionAfterAnAttributeChangeStillDeletes()
        {
            //chmod, then rm: the first event finds it gone without saying why, the second says
            var changes = Build(new Dictionary<int, string> { [1] = "/d" }, new HashSet<string> { "/d" },
                Event(FanotifyMonitor.FAN_ATTRIB, (FanotifyMonitor.INFO_DFID_NAME, 1, "f")),
                Event(FanotifyMonitor.FAN_DELETE, (FanotifyMonitor.INFO_DFID_NAME, 1, "f")));

            Assert.Equal(new[] { "delete /d/f", "upsert /d" }, changes.Select(Describe));
        }

        [Fact]
        public void ARenamedItemDeletedAfterwardsIsDeleted()
        {
            var changes = Build(new Dictionary<int, string> { [1] = "/d" }, new HashSet<string> { "/d" },
                Event(FanotifyMonitor.FAN_RENAME, (FanotifyMonitor.INFO_OLD_DFID_NAME, 1, "a"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 1, "b")),
                Event(FanotifyMonitor.FAN_DELETE, (FanotifyMonitor.INFO_DFID_NAME, 1, "b")));

            Assert.Equal(new[] { "rename /d/a -> /d/b", "upsert /d", "delete /d/b" }, changes.Select(Describe));
        }

        [Fact]
        public void ARenameIsNamedAsItWasBeforeItsFolderWasRenamed()
        {
            //mv /A/x /A/y; mv /A /B - by now the handle of A names /B
            var changes = Build(new Dictionary<int, string> { [1] = "/B", [2] = "/" },
                new HashSet<string> { "/B", "/B/y" },
                Event(FanotifyMonitor.FAN_RENAME, (FanotifyMonitor.INFO_OLD_DFID_NAME, 1, "x"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 1, "y")),
                Event(FanotifyMonitor.FAN_RENAME | FanotifyMonitor.FAN_ONDIR,
                    (FanotifyMonitor.INFO_OLD_DFID_NAME, 2, "A"), (FanotifyMonitor.INFO_NEW_DFID_NAME, 2, "B")));

            Assert.Equal(new[] { "rename /A/x -> /A/y", "rename /A -> /B", "upsert /B/y", "upsert /B" },
                changes.Select(Describe).Where(c => c != "upsert /"));
        }

        /// <summary>
        /// Random renames of files and folders in a small tree, reported as the kernel does:
        /// each names its folders by handle, resolved after the last one. Applied in order to
        /// the index as it was, every rename must find its item and the result be the disk.
        /// </summary>
        [Fact]
        public void AnyOrderOfRenamesKeepsTheIndexRight()
        {
            var random = new Random(20261104);
            for (var round = 0; round < 3000; round++)
            {
                //id -> (parent, name, folder); 0 is "/"
                var tree = new Dictionary<int, (int Parent, string Name, bool Folder)>
                {
                    [1] = (0, "A", true), [2] = (1, "C", true), [3] = (2, "f", false), [4] = (1, "x", false),
                    [5] = (0, "B", true), [6] = (5, "g", false), [7] = (2, "E", true), [8] = (7, "h", false)
                };
                string PathOf(int id) => id == 0 ? "/" : FanotifyMonitor.Join(PathOf(tree[id].Parent), tree[id].Name);
                bool Inside(int id, int folder) => folder == id || folder != 0 && Inside(id, tree[folder].Parent);
                var index = tree.Keys.Select(PathOf).ToHashSet();

                var events = new List<byte[]>();
                for (var n = random.Next(1, 7); n > 0; n--)
                {
                    var moved = random.Next(1, 9);
                    var folders = tree.Where(t => t.Value.Folder).Select(t => t.Key).Append(0).ToList();
                    var target = folders[random.Next(folders.Count)];
                    if (target != 0 && Inside(moved, target)) continue; //Not into itself
                    var name = "n" + random.Next(1000);
                    if (tree.Values.Any(v => v.Parent == target && v.Name == name)) continue;
                    var (parent, old, folder) = tree[moved];
                    events.Add(Event(FanotifyMonitor.FAN_RENAME | (folder ? FanotifyMonitor.FAN_ONDIR : 0),
                        (FanotifyMonitor.INFO_OLD_DFID_NAME, parent, old), (FanotifyMonitor.INFO_NEW_DFID_NAME, target, name)));
                    tree[moved] = (target, name, folder);
                }
                var onDisk = tree.Keys.Select(PathOf).ToHashSet();
                var folderPaths = tree.Where(t => t.Value.Folder).ToDictionary(t => t.Key, t => PathOf(t.Key));
                folderPaths[0] = "/";

                var changes = Build(folderPaths, onDisk, events.ToArray());

                foreach (var c in changes)
                    switch (c.Kind)
                    {
                        case ServiceProtocol.Rename:
                            Assert.True(index.Contains(c.OldPath), $"round {round}: {c.OldPath} -> {c.Path} not in the index");
                            Assert.False(index.Any(p => p == c.Path || p.StartsWith(c.Path + "/")),
                                $"round {round}: {c.OldPath} -> {c.Path} onto what the index has already");
                            var below = index.Where(p => p == c.OldPath || p.StartsWith(c.OldPath + "/")).ToList();
                            foreach (var p in below) index.Remove(p);
                            foreach (var p in below) index.Add(c.Path + p.Substring(c.OldPath.Length));
                            break;
                        case ServiceProtocol.Upsert:
                            index.Add(c.Path);
                            break;
                    }
                index.Remove("/");
                Assert.Equal(onDisk.OrderBy(p => p, StringComparer.Ordinal), index.OrderBy(p => p, StringComparer.Ordinal));
            }
        }

        [Fact]
        public void ALostEventRescansEveryRoot()
        {
            var changes = Build(new Dictionary<int, string>(), new HashSet<string>(),
                Event(FanotifyMonitor.FAN_Q_OVERFLOW));

            Assert.Equal(new[] { "rescan /" }, changes.Select(Describe));
        }

        // --- what one client sees ----------------------------------------------------------

        sealed class FakeAccess : IAccessCheck
        {
            public readonly Func<string, bool> Visible;
            public FakeAccess(Func<string, bool> visible) => Visible = visible;
            public bool[] CanList(IReadOnlyList<string> folders) => folders.Select(Visible).ToArray();
            public void Dispose() { }
        }

        static RawStat FileStat(uint owner, int mode = 0x81A4) => new(mode, owner, owner, false, false, false, 5, 1_700_000_000, 0);

        [Fact]
        public void AUserSeesOnlyTheFoldersItCouldList()
        {
            var batch = new[]
            {
                new RawChange(ServiceProtocol.Upsert, "/", "/open/a", null, FileStat(1)),
                new RawChange(ServiceProtocol.Upsert, "/", "/secret/b", null, FileStat(1)),
                new RawChange(ServiceProtocol.Delete, "/", "/secret/c", null, null),
                //Out of a hidden folder: appears, with its content to index
                new RawChange(ServiceProtocol.Rename, "/", "/open/moved", "/secret/moved", null),
                new RawChange(ServiceProtocol.Upsert, "/", "/open/moved", null, FileStat(1)),
                //Into a hidden folder: gone
                new RawChange(ServiceProtocol.Rename, "/", "/secret/hidden", "/open/hidden", null),
                new RawChange(ServiceProtocol.Rescan, "/", null, null, null)
            };

            var seen = ServiceHost.DescribeFor(batch, Me, new FakeAccess(folder => folder != "/secret"))
                .Select(c => c.Change).ToList();

            Assert.Equal(new[] { "Upsert /open/a 0", "Upsert /open/moved 1", "Delete /open/hidden 0", "Rescan  0" },
                seen.Select(c => $"{(c.Kind switch { 1 => "Upsert", 2 => "Delete", 3 => "Rename", _ => "Rescan" })} {c.Path} {c.Content}"));
        }

        static RawStat FolderStat() => new(0x41ED, 1, 1, false, true, false, 0, 1_700_000_000, 0);

        [Fact]
        public void AFolderWhosePermissionsChangedTellsWhetherTheUserCanListItNow()
        {
            var batch = new[]
            {
                new RawChange(ServiceProtocol.Upsert, "/", "/d/opened", null, FolderStat(), AccessChanged: true),
                new RawChange(ServiceProtocol.Upsert, "/", "/d/closed", null, FolderStat(), AccessChanged: true),
                new RawChange(ServiceProtocol.Upsert, "/", "/d/touched", null, FolderStat())
            };

            var own = ServiceHost.DescribeFor(batch, Me, new FakeAccess(folder => folder != "/d/closed")).Select(c => c.Change.Content);
            var all = ServiceHost.DescribeFor(batch, Me, null).Select(c => c.Change.Content);

            Assert.Equal(new[] { ServiceProtocol.ContentVisible, ServiceProtocol.ContentHidden, ServiceProtocol.ContentUnchanged }, own);
            //Every file: permissions decide nothing
            Assert.All(all, c => Assert.Equal(ServiceProtocol.ContentUnchanged, c));
        }

        static readonly NodeMetadataSnapshot Folder = new(FileAttributes.Directory, 0, DateTime.Now);

        static FileNode FolderWith(string path, int items)
        {
            var folder = FileNode.Create(path, Folder);
            folder.AddCountDelta(items);
            return folder;
        }

        [Fact]
        public void AFolderTheUserCannotListAnyMoreLosesItsContentAndOneItCanGetsIt()
        {
            (bool, bool) Plan(INode known, FolderContent content) => SearchModel.PlanServiceState(known, Folder, content);

            Assert.Equal((true, false), Plan(FolderWith("/full", 3), FolderContent.Unlistable));
            Assert.Equal((false, false), Plan(FolderWith("/empty", 0), FolderContent.Unlistable)); //Nothing to drop
            Assert.Equal((false, true), Plan(FolderWith("/empty", 0), FolderContent.Listable));
            Assert.Equal((false, false), Plan(FolderWith("/full", 3), FolderContent.Listable)); //Indexed already
            Assert.Equal((false, true), Plan(null, FolderContent.Listable));
            Assert.Equal((false, true), Plan(FolderWith("/full", 3), FolderContent.Arrived));
            Assert.Equal((false, false), Plan(FolderWith("/full", 3), FolderContent.Unchanged));
        }

        [Fact]
        public void WhatTheServiceSaysAboutAFolderReachesTheModelAsIs()
        {
            FsEvent On(byte content) => LinuxServiceClient.ToEvents(new ServiceChange(ServiceProtocol.Upsert, "/d", null, content,
                new ServiceMetadata(FileAttributes.Directory, 0, DateTime.UtcNow.Ticks, 0))).Single();

            Assert.Equal(FolderContent.Unchanged, On(ServiceProtocol.ContentUnchanged).Content);
            Assert.Equal(FolderContent.Arrived, On(ServiceProtocol.ContentNew).Content);
            Assert.Equal(FolderContent.Listable, On(ServiceProtocol.ContentVisible).Content);
            Assert.Equal(FolderContent.Unlistable, On(ServiceProtocol.ContentHidden).Content);
            Assert.Equal(FolderContent.Unchanged, On(77).Content);
        }

        /// <summary>
        /// The model's handling of service states (SearchModel's Changed case) over a small
        /// index of folder item counts, in queue order
        /// </summary>
        static void Apply(Dictionary<string, int> index, FsEvent e, Func<string, int> content)
        {
            var known = index.TryGetValue(e.FullPath, out var items) ? FolderWith(e.FullPath, items) : null;
            var (deleteFirst, indexContent) = SearchModel.PlanServiceState(known, e.MetadataSnapshot.Value, e.Content);
            if (deleteFirst) index.Remove(e.FullPath);
            index.TryAdd(e.FullPath, 0);
            if (indexContent) index[e.FullPath] = content(e.FullPath);
        }

        [Fact]
        public void AccessTakenAndGivenBackQuicklyLeavesTheContentIndexed()
        {
            var index = new Dictionary<string, int> { ["/d"] = 3 };
            var queue = new List<FsEvent>();
            //Both arrive before the model applies either - nothing may be decided on arrival
            foreach (var content in new[] { ServiceProtocol.ContentHidden, ServiceProtocol.ContentVisible })
                queue.AddRange(LinuxServiceClient.ToEvents(new ServiceChange(ServiceProtocol.Upsert, "/d", null, content,
                    new ServiceMetadata(FileAttributes.Directory, 0, DateTime.UtcNow.Ticks, 0))));

            foreach (var e in FSChangeProcessor.CoalesceChangedEvents(queue)) Apply(index, e, _ => 3);

            Assert.Equal(3, index["/d"]);
        }

        [Fact]
        public void AFolderThatBecameListableKeepsItThroughALaterChange()
        {
            var now = DateTime.Now;
            var listable = FsEvent.ServiceState("/d", Folder, FolderContent.Listable);
            var later = FsEvent.ServiceState("/d", new NodeMetadataSnapshot(FileAttributes.Directory, 0, now.AddSeconds(1)));
            var other = FsEvent.ServiceState("/e", Folder);

            var events = FSChangeProcessor.CoalesceChangedEvents(new[] { other, listable, later, FsEvent.ServiceState("/d", Folder) });

            Assert.Equal(new[] { FolderContent.Unchanged, FolderContent.Listable, FolderContent.Unchanged },
                events.Select(e => e.Content));
            Assert.Equal(new[] { "/e", "/d", "/d" }, events.Select(e => e.FullPath));
            var index = new Dictionary<string, int>();
            foreach (var e in events) Apply(index, e, _ => 2);
            Assert.Equal(2, index["/d"]);
        }

        [Fact]
        public void ReadOnlyIsTheConnectedUsersNotTheServices()
        {
            //rw-r--r-- of user 1000: writable for its owner, read-only for anyone else
            var batch = new[] { new RawChange(ServiceProtocol.Upsert, "/", "/f", null, FileStat(1000, 0x81A4)) };

            var owner = ServiceHost.DescribeFor(batch, new UserIdentity(1000, 1000, Array.Empty<uint>()), null)[0].Change;
            var other = ServiceHost.DescribeFor(batch, new UserIdentity(2000, 2000, Array.Empty<uint>()), null)[0].Change;

            Assert.False(owner.Metadata.Attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.True(other.Metadata.Attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc).Ticks, owner.Metadata.UtcTicks);
        }

        [Fact]
        public void AnItemThatTurnedIntoAFolderIsDeletedFirst()
        {
            var file = FileNode.Create("/x", new NodeMetadataSnapshot(FileAttributes.Normal, 1, DateTime.Now));

            Assert.Equal((true, false), SearchModel.PlanServiceState(file, Folder, FolderContent.Unchanged));
            Assert.Equal((true, false), SearchModel.PlanServiceState(FolderWith("/x", 2),
                new NodeMetadataSnapshot(FileAttributes.Normal, 1, DateTime.Now), FolderContent.Unchanged));
            Assert.Equal((true, true), SearchModel.PlanServiceState(file, Folder, FolderContent.Listable));
        }

        // --- the real access check -------------------------------------------------------

        [Fact]
        public void TheAccessCheckAnswersAsTheUser()
        {
            Assert.SkipUnless(UserAccess.Supported && Me.Uid != 0, LinuxOnly + " (and not as root)");
            var root = Path.Combine(Path.GetTempPath(), "win-search-access-" + Guid.NewGuid().ToString("N"));
            var closed = Path.Combine(root, "closed");
            Directory.CreateDirectory(closed);
            try
            {
                File.SetUnixFileMode(closed, UnixFileMode.None);
                using var mine = new UserAccess(Me);
                //An identity this process cannot take answers no - and the thread switches back
                using var foreign = new UserAccess(new UserIdentity(Me.Uid + 12345, Me.Gid + 12345, Array.Empty<uint>()));

                Assert.Equal(new[] { true, false, false }, mine.CanList(new[] { root, closed, Path.Combine(root, "missing") }));
                Assert.Equal(new[] { false }, foreign.CanList(new[] { root }));
                Assert.Equal(new[] { true }, mine.CanList(new[] { root })); //Still itself afterwards
            }
            finally
            {
                File.SetUnixFileMode(closed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(root, recursive: true);
            }
        }

        // --- the whole conversation ------------------------------------------------------

        sealed class FakeMonitor : IChangeMonitor
        {
            public readonly ConcurrentBag<string> Subscribed = new();
            public event Action<IReadOnlyList<RawChange>> Changed;
            public string Subscribe(string root)
            {
                Subscribed.Add(root);
                return null;
            }
            public void Raise(params RawChange[] changes) => Changed?.Invoke(changes);
            public void Dispose() { }
        }

        /// <summary>A service on a temporary socket, and the client pointed at it</summary>
        sealed class Running : IDisposable
        {
            public readonly string Folder = Path.Combine(Path.GetTempPath(), "win-search-service-" + Guid.NewGuid().ToString("N"));
            public readonly FakeMonitor Monitor = new();
            public readonly ServiceHost Host;
            public readonly string Socket;

            public Running(bool admin, bool requestAll, Func<string, bool> visible = null, int helloTimeoutMs = 10_000)
            {
                Directory.CreateDirectory(Folder);
                Socket = Path.Combine(Folder, "s.sock");
                Host = new ServiceHost(Socket, Monitor, () => new[] { Folder }, _ => Me, _ => admin,
                    _ => new FakeAccess(visible ?? (_ => true)), ownModeSupported: true) { HelloTimeoutMs = helloTimeoutMs };
                Host.Start();
                LinuxServiceClient.SocketPath = Socket;
                LinuxServiceClient.RequestAllFiles = () => requestAll;
                LinuxServiceClient.Reset();
            }

            public void Dispose()
            {
                LinuxServiceClient.Reset();
                LinuxServiceClient.SocketPath = ServiceProtocol.SocketPath;
                LinuxServiceClient.RequestAllFiles = () => LinuxServiceSettingsStore.ShowAllFiles;
                Host.Dispose();
                Directory.Delete(Folder, recursive: true);
            }
        }

        static void WaitFor(Func<bool> condition)
        {
            var end = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < end) Thread.Sleep(20);
            Assert.True(condition());
        }

        [Fact]
        public void AWatchedRootReceivesItsChangesAsAppEvents()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: false, requestAll: false, visible: folder => !folder.EndsWith("secret"));
            var events = new ConcurrentQueue<FsEvent>();
            var lost = 0;

            using var watch = LinuxServiceClient.TryWatch(service.Folder, events.Enqueue, _ => { }, () => lost++);
            Assert.NotNull(watch);
            Assert.Equal(ServiceStatus.OwnFiles, LinuxServiceClient.Status);
            Assert.Contains(service.Folder, service.Monitor.Subscribed);

            service.Monitor.Raise(
                new RawChange(ServiceProtocol.Upsert, service.Folder, service.Folder + "/a.txt", null, FileStat(Me.Uid)),
                new RawChange(ServiceProtocol.Upsert, service.Folder, service.Folder + "/secret/b.txt", null, FileStat(Me.Uid)),
                new RawChange(ServiceProtocol.Rename, service.Folder, service.Folder + "/d2", service.Folder + "/d1", null),
                new RawChange(ServiceProtocol.Upsert, "/elsewhere", "/elsewhere/x", null, FileStat(Me.Uid)));
            WaitFor(() => events.Count >= 2);
            Thread.Sleep(100); //Nothing else may follow

            var list = events.ToList();
            Assert.Equal(2, list.Count);
            Assert.True(list[0].IsServiceState);
            Assert.Equal(service.Folder + "/a.txt", list[0].FullPath);
            Assert.Equal(5UL, list[0].MetadataSnapshot.Value.Size);
            Assert.Equal((WatcherChangeTypes.Renamed, service.Folder + "/d1"), (list[1].ChangeType, list[1].OldFullPath));
            Assert.Equal(0, lost);
        }

        [Fact]
        public void ARootTheServiceRefusesStaysUnwatchedAndIsNotRetried()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: true, requestAll: true);

            Assert.Null(LinuxServiceClient.TryWatch("/not/a/root", _ => { }, _ => { }, () => { }));
            Assert.False(LinuxServiceClient.WouldWatch("/not/a/root"));
            Assert.True(LinuxServiceClient.WouldWatch(service.Folder));
        }

        [Fact]
        public void AnAdministratorsWalkMatchesTheAppsOwnWalk()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux() && LinuxDirectoryReader.IsSupported, LinuxOnly);
            using var service = new Running(admin: true, requestAll: true);
            Directory.CreateDirectory(Path.Combine(service.Folder, "sub", "deep"));
            File.WriteAllText(Path.Combine(service.Folder, "sub", "a.txt"), "aaa");
            File.WriteAllText(Path.Combine(service.Folder, "sub", "deep", "b.txt"), "bb");
            File.SetLastWriteTimeUtc(Path.Combine(service.Folder, "sub", "a.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            var served = LinuxServiceClient.TryWalk(service.Folder, CancellationToken.None);
            var own = DirectoryWalker.Walk(new DirectoryInfo(service.Folder), TimeSpan.FromSeconds(15), CancellationToken.None, true);

            Assert.NotNull(served);
            Assert.Equal(ServiceStatus.AllFiles, LinuxServiceClient.Status);
            var expected = own.ToDictionary(n => n.FullName);
            Assert.Equal(expected.Keys.OrderBy(k => k), served.Select(n => n.FullName).OrderBy(k => k));
            foreach (var node in served)
            {
                var mine = expected[node.FullName];
                Assert.Equal((mine.Attributes, mine.Size, mine.Count, mine.LastChangeTime),
                    (node.Attributes, node.Size, node.Count, node.LastChangeTime));
            }
        }

        /// <summary>A connection that says nothing</summary>
        static NetworkStream Silent(string socketPath)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            socket.ReceiveTimeout = 5000;
            return new NetworkStream(socket, ownsSocket: true);
        }

        /// <summary>A connection that says hello and then does only what the test does</summary>
        static NetworkStream Raw(string socketPath, byte mode)
        {
            var stream = Silent(socketPath);
            var hello = ServiceProtocol.Begin(ServiceProtocol.Hello);
            hello.Writer.Write(ServiceProtocol.Version);
            hello.Writer.Write(mode);
            ServiceProtocol.Send(stream, hello);
            return stream;
        }

        /// <summary>The service closed the connection without a word (a hello it did not read resets it)</summary>
        static bool Refused(NetworkStream client)
        {
            try { return ServiceProtocol.ReadFrame(client, 1 << 20) == null; }
            catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                return true;
            }
        }

        static byte TypeOf(byte[] frame)
        {
            if (frame == null) return 0;
            using var _ = ServiceProtocol.Open(frame, out var type);
            return type;
        }

        [Fact]
        public void AClientThatStopsReadingGetsARescanInsteadOfAnEndlessBacklog()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: true, requestAll: true);
            using var client = Raw(service.Socket, ServiceProtocol.ModeAll);
            Assert.Equal(ServiceProtocol.Welcome, TypeOf(ServiceProtocol.ReadFrame(client, 1 << 20)));
            var watch = ServiceProtocol.Begin(ServiceProtocol.Watch);
            watch.Writer.Write(service.Folder);
            ServiceProtocol.Send(client, watch);
            Assert.Equal(ServiceProtocol.WatchResult, TypeOf(ServiceProtocol.ReadFrame(client, 1 << 20)));

            //Twice what the service keeps for one client, while it reads nothing
            var batch = Enumerable.Range(0, 1000).Select(i => new RawChange(ServiceProtocol.Upsert, service.Folder,
                service.Folder + "/f" + i, null, FileStat(Me.Uid))).ToArray();
            for (var i = 0; i < 400; i++) service.Monitor.Raise(batch);
            Assert.InRange(service.Host.QueuedChanges, 0, 200_000);

            //Reading again: what got out before, then the rescan, then new changes
            var received = 0;
            string Next()
            {
                using var r = ServiceProtocol.Open(ServiceProtocol.ReadFrame(client, ServiceProtocol.MaxResponseFrame), out var type);
                Assert.Equal(ServiceProtocol.Changes, type);
                r.ReadString();
                string last = null;
                for (var n = r.ReadInt32(); n > 0; n--)
                {
                    var c = ServiceProtocol.ReadChange(r);
                    if (c.Kind == ServiceProtocol.Rescan) return "rescan";
                    received++;
                    last = c.Path;
                }
                return last;
            }
            while (Next() != "rescan") { }
            Assert.InRange(received, 0, 200_000);
            service.Monitor.Raise(new RawChange(ServiceProtocol.Upsert, service.Folder, service.Folder + "/after", null, FileStat(Me.Uid)));
            while (Next() != service.Folder + "/after") { }
        }

        [Fact]
        public void OneUserCannotOpenEndlessConnections()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: false, requestAll: false);
            var clients = new List<NetworkStream>();
            try
            {
                //Served ones and ones that never say a word count alike
                for (var i = 0; i < 16; i++)
                {
                    clients.Add(Raw(service.Socket, ServiceProtocol.ModeOwn));
                    Assert.Equal(ServiceProtocol.Welcome, TypeOf(ServiceProtocol.ReadFrame(clients[^1], 1 << 20)));
                }
                for (var i = 0; i < 16; i++) clients.Add(Silent(service.Socket));
                clients.Add(Raw(service.Socket, ServiceProtocol.ModeOwn));
                Assert.True(Refused(clients[^1]));

                clients[^2].Dispose(); //One gone: room for another, once the service noticed
                var end = Environment.TickCount64 + 5000;
                NetworkStream admitted = null;
                while (admitted == null && Environment.TickCount64 < end)
                {
                    var again = Raw(service.Socket, ServiceProtocol.ModeOwn);
                    if (!Refused(again)) admitted = again;
                    else
                    {
                        again.Dispose();
                        Thread.Sleep(20);
                    }
                }
                Assert.NotNull(admitted);
                clients.Add(admitted);
            }
            finally
            {
                foreach (var c in clients) c.Dispose();
            }
        }

        [Fact]
        public void AConnectionThatSaysNothingIsClosed()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: false, requestAll: false, helloTimeoutMs: 300);
            using var silent = Silent(service.Socket);
            using var talking = Raw(service.Socket, ServiceProtocol.ModeOwn);
            Assert.Equal(ServiceProtocol.Welcome, TypeOf(ServiceProtocol.ReadFrame(talking, 1 << 20)));

            Assert.Null(ServiceProtocol.ReadFrame(silent, 1 << 20)); //Closed, long before the client's own 5 s
            Thread.Sleep(600);
            //A served client may stay quiet for good
            var watch = ServiceProtocol.Begin(ServiceProtocol.Watch);
            watch.Writer.Write(service.Folder);
            ServiceProtocol.Send(talking, watch);
            Assert.Equal(ServiceProtocol.WatchResult, TypeOf(ServiceProtocol.ReadFrame(talking, 1 << 20)));
        }

        [Fact]
        public void TheSecurityLabelIsTheConnectedProcesss()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            var path = Path.Combine(Path.GetTempPath(), "win-search-label-" + Guid.NewGuid().ToString("N") + ".sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            try
            {
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(path));
                using var accepted = listener.Accept();
                string own = null;
                try { own = File.ReadAllText("/proc/self/attr/current").TrimEnd('\0', '\n', ' '); } catch (IOException) { }
                if (own?.Length == 0) own = null;

                Assert.Equal(own, PeerIdentity.SecurityLabel(accepted));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("unconfined_u:unconfined_r:unconfined_t:s0-s0:c0.c1023", true)]
        [InlineData("staff_u:staff_r:staff_t:s0-s0:c0.c1023", false)]
        [InlineData("system_u:system_r:container_t:s0:c12,c34", false)]
        [InlineData("unconfined", true)]
        [InlineData("code (unconfined)", true)]
        [InlineData("snap.firefox.firefox (enforce)", false)]
        [InlineData("/usr/bin/evince (complain)", false)]
        public void OnlyClientsNoSecurityPolicyConfinesAreServed(string label, bool served)
        {
            Assert.Equal(served, PeerIdentity.IsUnconfined(label));
        }

        /// <summary>
        /// A process started by a confining tool, once the tool has run it - the check is
        /// made against this test process's mounts and root, as the service makes it
        /// against init's
        /// </summary>
        static string ConfinementOfChild(string tool, params string[] arguments)
        {
            Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists(tool), $"needs {tool}");
            var start = new System.Diagnostics.ProcessStartInfo(tool);
            foreach (var argument in arguments.Concat(new[] { "sleep", "30" })) start.ArgumentList.Add(argument);
            using var child = System.Diagnostics.Process.Start(start);
            try
            {
                var comm = $"/proc/{child.Id}/comm";
                var end = Environment.TickCount64 + 5000;
                while (!child.HasExited && Environment.TickCount64 < end && File.ReadAllText(comm).Trim() != "sleep") Thread.Sleep(10);
                if (child.HasExited) Assert.Skip($"{tool} cannot run here (exit code {child.ExitCode})");
                return PeerIdentity.ConfinementOfProcess(child.Id, "/proc/self");
            }
            finally
            {
                if (!child.HasExited) child.Kill();
            }
        }

        static bool ThisProcessIsUnconfined => OperatingSystem.IsLinux()
            && PeerIdentity.ConfinementOfProcess(Environment.ProcessId, "/proc/self") == null;

        [Fact]
        public void AnOrdinaryProcessIsNotConfined()
        {
            Assert.SkipUnless(ThisProcessIsUnconfined, "the tests run confined");
            Assert.Null(ConfinementOfChild("/usr/bin/env"));
        }

        [Fact]
        public void AProcessLandlockConfinesIsNotServed()
        {
            Assert.Contains("no_new_privs", ConfinementOfChild("/usr/bin/setpriv",
                "--landlock-access", "fs:read-dir", "--landlock-rule", "path-beneath:read-dir:/usr"));
        }

        [Fact]
        public void AProcessWithNoNewPrivsIsNotServed()
        {
            Assert.Contains("no_new_privs", ConfinementOfChild("/usr/bin/setpriv", "--no-new-privs"));
        }

        [Fact]
        public void AProcessInItsOwnMountNamespaceIsNotServed()
        {
            Assert.SkipUnless(ThisProcessIsUnconfined, "the tests run confined");
            Assert.Contains("other mounts", ConfinementOfChild("/usr/bin/unshare", "--user", "--map-root-user", "--mount"));
        }

        /// <summary>A python process connected to a socket of this test, as the tool starts it</summary>
        static string ConfinementOfConnected(bool exitFirst, params string[] tool)
            => Connected(Connect(exitFirst), exitFirst, says: false, tool).Confinement;

        static string Connect(bool exitFirst) => "import socket, sys, time\n"
            + "s = socket.socket(socket.AF_UNIX)\ns.connect(sys.argv[1])\n"
            + (exitFirst ? "" : "time.sleep(30)\n");

        /// <summary>
        /// Only one thread confines itself - Landlock with no folder listable, which needs
        /// no_new_privs - says whether it can list /usr, and connects; the main thread stays
        /// as it was
        /// </summary>
        const string OneThreadLandlocked = """
            import ctypes, os, socket, sys, threading, time
            libc = ctypes.CDLL(None, use_errno=True)
            def confined():
                libc.prctl(38, 1, 0, 0, 0)
                handled = ctypes.c_uint64(1 << 3)
                ruleset = libc.syscall(ctypes.c_long(444), ctypes.byref(handled), ctypes.c_long(8), ctypes.c_long(0))
                libc.syscall(ctypes.c_long(446), ctypes.c_long(ruleset), ctypes.c_long(0))
                try:
                    os.listdir('/usr')
                    print('listed', flush=True)
                except PermissionError:
                    print('denied', flush=True)
                s = socket.socket(socket.AF_UNIX)
                s.connect(sys.argv[1])
                time.sleep(30)
            threading.Thread(target=confined, daemon=True).start()
            time.sleep(30)
            """;

        /// <param name="says">The script prints a line before it connects</param>
        static (string Confinement, string Said) Connected(string script, bool exitFirst, bool says, params string[] tool)
        {
            Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/python3"), "needs python3");
            var path = Path.Combine(Path.GetTempPath(), "win-search-peer-" + Guid.NewGuid().ToString("N") + ".sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var start = new System.Diagnostics.ProcessStartInfo(tool.Length > 0 ? tool[0] : "/usr/bin/python3")
            {
                RedirectStandardOutput = says
            };
            foreach (var argument in tool.Skip(1)) start.ArgumentList.Add(argument);
            if (tool.Length > 0) start.ArgumentList.Add("/usr/bin/python3");
            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(path);
            using var child = System.Diagnostics.Process.Start(start);
            try
            {
                using var accepted = listener.Accept();
                if (exitFirst) child.WaitForExit();
                var said = says ? child.StandardOutput.ReadLine() : null;
                return (PeerIdentity.ConfinementOf(accepted, "/proc/self"), said);
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                File.Delete(path);
            }
        }

        [Fact]
        public void TheConnectedProcessIsTheOneChecked()
        {
            Assert.SkipUnless(ThisProcessIsUnconfined, "the tests run confined");
            Assert.Null(ConfinementOfConnected(exitFirst: false));
            Assert.Contains("no_new_privs", ConfinementOfConnected(exitFirst: false, "/usr/bin/setpriv", "--no-new-privs"));
            Assert.Equal("it ended", ConfinementOfConnected(exitFirst: true));
        }

        [Fact]
        public void AProcessWithOneConfinedThreadIsNotServed()
        {
            Assert.SkipUnless(ThisProcessIsUnconfined, "the tests run confined");
            var (confinement, said) = Connected(OneThreadLandlocked, exitFirst: false, says: true);

            Assert.Equal("denied", said); //That thread really cannot list a folder
            Assert.Contains("no_new_privs", confinement);
        }

        [Fact]
        public void OnlyAnAdministratorGetsEveryFile()
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
            using var service = new Running(admin: false, requestAll: true);

            Assert.Null(LinuxServiceClient.TryWalk(service.Folder, CancellationToken.None));
            Assert.Equal(ServiceStatus.OwnFiles, LinuxServiceClient.Status);
            Assert.False(LinuxServiceClient.IsAdmin);
        }
    }
}
