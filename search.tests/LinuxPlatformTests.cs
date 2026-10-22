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
    }
}
