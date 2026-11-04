using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using search.Models;

namespace search
{
    /// <summary>
    /// Lets the user choose which drives are indexed - Windows drive letters, or the mount
    /// points of real file systems on Linux. Drives without an explicit choice default to
    /// local NTFS on Windows and to local file systems (except boot partitions) on Linux.
    /// Each drive's details are probed on its own thread - a dead network mapping shows
    /// "not ready" after its probe times out instead of freezing the dialog.
    /// </summary>
    public partial class DrivesWindow : Window
    {
        public sealed class DriveChoice : INotifyPropertyChanged
        {
            string details;
            bool isChecked;
            public event PropertyChangedEventHandler PropertyChanged;
            public string Key { get; set; }     //"C:" or "/home"
            public string Root { get; set; }    //"C:\" or "/home"
            public string Details
            {
                get => details;
                set { details = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details))); }
            }
            public bool IsChecked
            {
                get => isChecked;
                set { isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
            }
            public bool Known { get; set; }     //The user made an explicit choice earlier
            public bool Probed { get; set; }    //The default for an unknown drive is settled
        }

        readonly ObservableCollection<DriveChoice> choices = new();
        public IReadOnlyList<string> ChangedRoots { get; private set; } = Array.Empty<string>();
        /// <summary>The every-file choice changed: every drive is scanned and watched anew</summary>
        public bool ShowAllFilesChanged { get; private set; }

        public DrivesWindow()
        {
            InitializeComponent();
            drivesList.ItemsSource = choices;
            LoadDrives();
            if (OperatingSystem.IsLinux()) LoadServiceState();
        }

        /// <summary>
        /// The file system service's state - asked on its own thread: connecting to a service
        /// that hangs must not freeze the dialog
        /// </summary>
        void LoadServiceState()
        {
            servicePanel.IsVisible = true;
            showAllFiles.IsChecked = LinuxServiceSettingsStore.ShowAllFiles;
            showAllFiles.IsEnabled = false;
            new Thread(() =>
            {
                var status = Models.LinuxService.LinuxServiceClient.Status;
                var admin = status is Models.LinuxService.ServiceStatus.AllFiles or Models.LinuxService.ServiceStatus.OwnFiles
                    && Models.LinuxService.LinuxServiceClient.IsAdmin;
                Dispatcher.UIThread.Post(() =>
                {
                    serviceState.Text = ServiceStateText(status, admin);
                    //Not installed or not running: the choice waits for the service.
                    //Running: only an administrator may see every file.
                    showAllFiles.IsEnabled = admin || status is Models.LinuxService.ServiceStatus.NotInstalled
                        or Models.LinuxService.ServiceStatus.NotRunning;
                });
            })
            { IsBackground = true }.Start();
        }

        internal static string ServiceStateText(Models.LinuxService.ServiceStatus status, bool admin) => status switch
        {
            Models.LinuxService.ServiceStatus.NotInstalled => L.Text("ServiceNotInstalled"),
            Models.LinuxService.ServiceStatus.NotRunning => L.Text("ServiceNotRunning"),
            Models.LinuxService.ServiceStatus.AllFiles => L.Text("ServiceAllFiles"),
            _ => L.Text("ServiceOwnFiles") + (admin ? "" : " " + L.Text("ServiceNotAdmin"))
        };

        void LoadDrives()
        {
            var selection = DriveSelectionStore.Load();
            foreach (var r in FsRoots.All())
            {
                var root = r;
                var known = selection.Drives.TryGetValue(root.Key, out var enabled);
                var choice = new DriveChoice
                {
                    Key = root.Key,
                    Root = root.Path,
                    Details = L.Text("Checking"),
                    IsChecked = known && enabled,
                    Known = known
                };
                choices.Add(choice);

                //Probe on a dedicated thread: IsReady/DriveFormat of a dead network mapping
                //block in SMB/NFS timeouts, and the startup drive scans may be saturating the pool
                new Thread(() =>
                {
                    string format = root.Format, label = null;
                    long size = 0;
                    try
                    {
                        var drive = new DriveInfo(root.Path);
                        if (DriveAvailability.IsReady(drive))
                        {
                            format ??= drive.DriveFormat;
                            size = drive.TotalSize;
                            //The label is a volume query too - never on the UI thread
                            label = OperatingSystem.IsWindows() ? drive.VolumeLabelSafe() : root.Source;
                        }
                        else if (OperatingSystem.IsWindows()) format = null;
                    }
                    catch { }
                    var defaultEnabled = DriveSelectionStore.DefaultEnabled(root.Path, root.Type, format);
                    Dispatcher.UIThread.Post(() =>
                    {
                        choice.Details = $"{format ?? L.Text("NotReady")} · {TypeName(root.Type)}"
                            + (size > 0 ? $" · {size >> 30} GB" : "")
                            + (label is { Length: > 0 } ? $" · {label}" : "");
                        if (!known) choice.IsChecked = defaultEnabled; //No explicit choice => the platform default
                        choice.Probed = true;
                    });
                })
                { IsBackground = true }.Start();
            }
        }

        static string TypeName(DriveType t) => t switch
        {
            DriveType.Fixed => L.Text("Local"),
            DriveType.Network => L.Text("Network"),
            DriveType.Removable => L.Text("Removable"),
            DriveType.CDRom => "CD/DVD",
            DriveType.Ram => "RAM disk",
            _ => L.Text("Unknown")
        };

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            var selection = DriveSelectionStore.Load();
            //A drive still showing "Checking…" is unchecked only because its default is not
            //known yet - saving that would switch a slow USB NTFS drive off for good. Keep it
            //untouched unless the user checked it.
            ChangedRoots = DriveSelectionStore.ApplyChoices(selection,
                choices.Where(c => c.Known || c.Probed || c.IsChecked)
                    .Select(c => (c.Key, c.Root, c.IsChecked)));
            DriveSelectionStore.Save(selection, ChangedRoots);
            if (servicePanel.IsVisible && showAllFiles.IsChecked is { } showAll
                && showAll != LinuxServiceSettingsStore.ShowAllFiles)
            {
                LinuxServiceSettingsStore.Save(showAll);
                ShowAllFilesChanged = true;
            }
            Close(true);
        }

        void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);
    }

    internal static class DriveInfoExtensions
    {
        /// <summary>
        /// VolumeLabel without the exceptions unlabeled/odd volumes throw
        /// </summary>
        public static string VolumeLabelSafe(this DriveInfo d)
        {
            try { return d.VolumeLabel; } catch { return null; }
        }
    }
}
