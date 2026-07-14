using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;

namespace search
{
    /// <summary>
    /// Lets the user choose which drives are indexed. Drives without an explicit choice
    /// default to NTFS only. Each drive's details are probed on its own thread - a dead
    /// network mapping shows "not ready" after its probe times out instead of freezing
    /// the dialog.
    /// </summary>
    public partial class DrivesWindow : Window
    {
        public sealed class DriveChoice : INotifyPropertyChanged //Fody raises the changes
        {
#pragma warning disable 0067 //Raised by the weaved setters
            public event PropertyChangedEventHandler PropertyChanged;
#pragma warning restore 0067
            public string Key { get; set; }     //"C:"
            public string Root { get; set; }    //"C:\"
            public string Details { get; set; }
            public bool IsChecked { get; set; }
        }

        readonly ObservableCollection<DriveChoice> choices = new();

        public DrivesWindow()
        {
            InitializeComponent();
            drivesList.ItemsSource = choices;
            LoadDrives();
        }

        void LoadDrives()
        {
            var selection = DriveSelectionStore.Load();
            foreach (var d in DriveInfo.GetDrives())
            {
                var drive = d;
                var key = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                var known = selection.Drives.TryGetValue(key, out var enabled);
                var choice = new DriveChoice
                {
                    Key = key,
                    Root = drive.Name,
                    Details = L.Text("Checking"),
                    IsChecked = known && enabled
                };
                choices.Add(choice);

                //Probe on a dedicated thread: IsReady/DriveFormat of a dead network mapping
                //block in SMB timeouts, and the startup drive scans may be saturating the pool
                new Thread(() =>
                {
                    string format = null;
                    long size = 0;
                    try
                    {
                        if (drive.IsReady)
                        {
                            format = drive.DriveFormat;
                            size = drive.TotalSize;
                        }
                    }
                    catch { }
                    var ntfs = string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase);
                    Dispatcher.Invoke(() =>
                    {
                        choice.Details = $"{format ?? L.Text("NotReady")} · {TypeName(drive.DriveType)}"
                            + (size > 0 ? $" · {size >> 30} GB" : "")
                            + (drive.VolumeLabelSafe() is string l && l.Length > 0 ? $" · {l}" : "");
                        if (!known) choice.IsChecked = ntfs; //No explicit choice => the NTFS-only default
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
            foreach (var c in choices) selection.Drives[c.Key] = c.IsChecked;
            DriveSelectionStore.Save(selection);
            DialogResult = true;
        }
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
