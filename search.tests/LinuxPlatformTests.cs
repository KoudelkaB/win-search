using System.IO;
using System.Linq;
using search.Models;
using Xunit;

namespace search.Tests
{
    /// <summary>
    /// Linux pieces that are pure text processing - they run on any host
    /// </summary>
    public class LinuxPlatformTests
    {
        // A desktop with / and /home on separate ext4 partitions, an EFI partition, a USB
        // stick, an NTFS partition through ntfs-3g, a snap, a bind mount of /home, a portal
        // and the usual pseudo file systems
        const string MountInfo =
            "22 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw\n" +
            "23 22 0:21 / /proc rw,nosuid shared:12 - proc proc rw\n" +
            "24 22 0:22 / /sys rw,nosuid shared:2 - sysfs sysfs rw\n" +
            "25 22 0:5 / /dev rw,nosuid shared:8 - devtmpfs udev rw\n" +
            "26 22 0:25 / /run rw,nosuid shared:13 - tmpfs tmpfs rw\n" +
            "30 22 259:3 / /home rw,relatime shared:30 - ext4 /dev/nvme0n1p3 rw\n" +
            "31 22 259:1 / /boot/efi rw,relatime shared:31 - vfat /dev/nvme0n1p1 rw\n" +
            "32 22 7:3 / /snap/core22/1380 ro,nodev shared:32 - squashfs /dev/loop3 ro\n" +
            "33 26 8:17 / /run/media/user/USB\\040Stick rw,nosuid shared:33 - vfat /dev/sdb1 rw\n" +
            "34 22 8:2 / /mnt/windows rw,relatime shared:34 - fuseblk /dev/sda2 rw,user_id=0\n" +
            "35 22 259:3 / /srv/home-copy rw,relatime shared:30 - ext4 /dev/nvme0n1p3 rw\n" +
            "36 26 0:50 / /run/user/1000/doc rw,nosuid shared:36 - fuse.portal portal rw\n" +
            "37 22 0:51 / /tmp rw,nosuid shared:37 - tmpfs tmpfs rw\n" +
            "38 22 0:52 / /mnt/nas rw,relatime shared:38 - nfs4 nas:/export rw\n";

        [Fact]
        public void RealFileSystemsBecomeRootsAndPseudoOnesDoNot()
        {
            var (roots, mountPoints) = FsRoots.ParseMountInfo(MountInfo);
            var paths = roots.Select(r => r.Path).ToArray();

            //Shortest first; \040 in the table is the space of the USB stick's label
            Assert.Equal(new[] { "/", "/home", "/mnt/nas", "/boot/efi", "/mnt/windows", "/run/media/user/USB Stick" },
                paths);
            //A walk of "/" must still stop at every mount point, pseudo ones included
            Assert.Contains("/proc", mountPoints);
            Assert.Contains("/snap/core22/1380", mountPoints);
            Assert.Contains("/tmp", mountPoints);
            //The bind mount of /home is the same file system - not a second root
            Assert.DoesNotContain("/srv/home-copy", paths);
        }

        [Fact]
        public void DriveTypesAndDefaultsFollowTheFileSystem()
        {
            var roots = FsRoots.ParseMountInfo(MountInfo).Roots.ToDictionary(r => r.Path);
            Assert.Equal(DriveType.Fixed, roots["/"].Type);
            Assert.Equal(DriveType.Network, roots["/mnt/nas"].Type);
            Assert.Equal(DriveType.Removable, roots["/run/media/user/USB Stick"].Type);
            //An NTFS partition mounted through ntfs-3g is an ordinary local root
            Assert.Equal("fuseblk", roots["/mnt/windows"].Format);
            Assert.Equal(DriveType.Fixed, roots["/mnt/windows"].Type);

            if (System.OperatingSystem.IsWindows()) return; //The defaults below are the Linux rules
            Assert.True(FsRoots.DefaultEnabled("/", DriveType.Fixed, "ext4"));
            Assert.True(FsRoots.DefaultEnabled("/mnt/windows", DriveType.Fixed, "fuseblk"));
            Assert.True(FsRoots.DefaultEnabled("/mnt/data", DriveType.Fixed, "ntfs3"));
            Assert.False(FsRoots.DefaultEnabled("/boot/efi", DriveType.Fixed, "vfat"));
            Assert.False(FsRoots.DefaultEnabled("/mnt/nas", DriveType.Network, "nfs4"));
        }

        // Inside a Flatpak with --filesystem=host (a real table, shortened): the runtime's /usr
        // and /etc files, the app's /app, the sandbox's own files, a single shared file
        // (/.profile), the computer's folders shared one by one, a USB stick, and the computer's
        // /usr and whole root under /run/host
        const string FlatpakMountInfo =
            "959 900 0:80 /newroot / rw,nosuid,nodev - tmpfs tmpfs rw\n" +
            "1209 959 259:7 /var/lib/flatpak/runtime/org.gnome.Platform/x86_64/50/a1/files /usr ro,nosuid,nodev - ext4 /dev/nvme0n1p4 rw\n" +
            "1211 1209 259:7 /var/lib/flatpak/runtime/org.freedesktop.Platform.GL.default/x86_64/25.08/b2/files /usr/lib/x86_64-linux-gnu/GL/default ro - ext4 /dev/nvme0n1p4 rw\n" +
            "1220 959 259:7 /var/lib/flatpak/app/io.github.koudelkab.FileSearchManager/x86_64/master/c3/files /app ro - ext4 /dev/nvme0n1p4 rw\n" +
            "1228 959 259:7 /etc/machine-id /etc/machine-id ro - ext4 /dev/nvme0n1p4 rw\n" +
            "1237 959 259:7 /var/lib/flatpak/runtime/org.gnome.Platform/x86_64/50/a1/files/etc/fonts /etc/fonts ro - ext4 /dev/nvme0n1p4 rw\n" +
            "1539 959 0:84 /bindfileOldfLp//deleted /.flatpak-info ro - tmpfs tmpfs rw\n" +
            "1640 959 259:7 /.profile /.profile rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1701 959 259:7 /home /home rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1703 1701 259:2 / /home/user/Data rw - xfs /dev/nvme2n1p1 rw\n" +
            "1706 1701 0:91 / /home/user/.local/share/flatpak rw - tmpfs tmpfs rw\n" +
            "1711 959 259:7 /mnt /mnt rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1712 959 259:7 /opt /opt rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1713 959 0:29 /media /run/media rw - tmpfs tmpfs rw\n" +
            "1714 1713 8:17 / /run/media/user/USB rw - vfat /dev/sdb1 rw\n" +
            "1715 959 259:7 /usr /run/host/usr rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1717 959 259:7 / /run/host/root rw - ext4 /dev/nvme0n1p4 rw\n" +
            "1751 1717 259:2 / /run/host/root/home/user/Data rw - xfs /dev/nvme2n1p1 rw\n" +
            "1760 959 0:95 /export/projects /srv/projects rw - nfs4 nas:/export rw\n";

        [Fact]
        public void AFlatpakIndexesTheSharedFoldersOfTheComputerOnly()
        {
            //A shared folder of an NFS share is never asked - a dead server would never answer
            var (roots, mountPoints) = FsRoots.ParseMountInfo(FlatpakMountInfo, sandboxed: true,
                isFolder: path => path == "/srv/projects" ? throw new Xunit.Sdk.XunitException("asked a network share")
                    : path != "/.profile");
            Assert.Equal(new[] { "/mnt", "/opt", "/home", "/srv/projects", "/home/user/Data", "/run/media/user/USB" },
                roots.Select(r => r.Path).ToArray());
            //Walks still stop at the sandbox's own mounts
            Assert.Contains("/home/user/.local/share/flatpak", mountPoints);
            Assert.Contains("/usr", mountPoints);

            //Without the sandbox rules the runtime and a single file would be "drives"
            var unfiltered = FsRoots.ParseMountInfo(FlatpakMountInfo, isFolder: path => path != "/.profile" && path != "/srv/projects")
                .Roots.Select(r => r.Path).ToArray();
            Assert.Contains("/usr", unfiltered);
            Assert.Contains("/etc/fonts", unfiltered);
            Assert.Contains("/.profile", unfiltered);
        }

        [Theory]
        [InlineData("/home/user/a.txt", "/home")]
        [InlineData("/home", "/home")]
        [InlineData("/homework/x", "/")]
        [InlineData("/boot/efi/EFI/boot.efi", "/boot/efi")]
        [InlineData("/boot/vmlinuz", "/")]
        [InlineData("/", "/")]
        public void APathBelongsToTheDeepestRootAboveIt(string path, string expected)
        {
            var byLength = FsRoots.ParseMountInfo(MountInfo).Roots.Select(r => r.Path)
                .OrderByDescending(p => p.Length).ToArray();
            Assert.Equal(expected, FsRoots.OwnerRoot(path, byLength));
        }

        [Theory]
        [InlineData("/home/user/My File.txt", "/home/user/My%20File.txt")]
        [InlineData("/data/čeština (1).log", "/data/%C4%8De%C5%A1tina%20(1).log")]
        [InlineData("/a/100%.txt", "/a/100%25.txt")]
        public void TrashInfoPathsArePercentEncoded(string path, string expected)
            => Assert.Equal(expected, LinuxTrash.Escape(path));

        const string CaseSensitiveOnly = "names differing only in case are different items only where the platform says so";

        [Fact]
        public void FoldersDifferingOnlyInCaseAreBothFiltered()
        {
            Assert.SkipWhen(PathText.IgnoreCase, CaseSensitiveOnly);
            Assert.Equal("/data/Foo /data/foo", new NodeFilter("/data/Foo /data/foo /data/Foo").ToString());
        }

        [Fact]
        public void TargetsDifferingOnlyInCaseAreBothKept()
        {
            Assert.SkipWhen(PathText.IgnoreCase, CaseSensitiveOnly);
            var settings = new WorkspaceSettings
            {
                BasketTargets =
                {
                    new BasketTarget { Path = "/data/Foo", Kind = BasketTargetKind.Folder },
                    new BasketTarget { Path = "/data/foo", Kind = BasketTargetKind.Folder },
                    new BasketTarget { Path = "/data/Foo", Kind = BasketTargetKind.Folder }
                }
            };

            WorkspaceSettingsStore.Validate(settings);

            Assert.Equal(new[] { "/data/Foo", "/data/foo" }, settings.BasketTargets.Select(t => t.Path));
        }

        [Fact]
        public void AnArchiveEntryCannotLeaveForAFolderOfTheSameNameInAnotherCase()
        {
            Assert.SkipWhen(PathText.IgnoreCase, CaseSensitiveOnly);
            Assert.Null(ZipExtensions.SafeExtractionPath("/tmp/Out", "../out/escaped.txt"));
            Assert.Equal("/tmp/Out/inside.txt", ZipExtensions.SafeExtractionPath("/tmp/Out", "inside.txt"));
        }

        [Fact]
        public void MountPointsDifferingOnlyInCaseAreChosenSeparately()
        {
            Assert.SkipWhen(PathText.IgnoreCase, CaseSensitiveOnly);
            var selection = new DriveSelection();
            selection.Drives["/mnt/Data"] = true;
            selection.Drives["/mnt/data"] = false;

            Assert.True(selection.Drives["/mnt/Data"]);
            Assert.False(selection.Drives["/mnt/data"]);
            //One drive's rescan request does not stand for the other's
            var now = new System.DateTime(2026, 11, 9, 12, 0, 0, System.DateTimeKind.Utc);
            var scanned = new[]
            {
                new System.Collections.Generic.KeyValuePair<string, System.DateTime>("/mnt/Data", now.AddHours(-1)),
                new System.Collections.Generic.KeyValuePair<string, System.DateTime>("/mnt/data", now)
            };
            Assert.Equal(new[] { "/mnt/Data" }, SearchModel.DrivesToRefresh(scanned,
                System.Array.Empty<System.Collections.Generic.KeyValuePair<string, System.DateTime>>(), now,
                System.TimeSpan.FromMinutes(2), _ => true, _ => false));
        }
    }
}
