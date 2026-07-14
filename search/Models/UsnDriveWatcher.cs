using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using search.Core;

namespace search.Models
{
    /// <summary>
    /// Watches one NTFS volume through its USN change journal and translates the records
    /// into FsEvents. Unlike FileSystemWatcher the journal is kernel-persisted - a change
    /// can never be dropped to buffer pressure; losing history (journal wrap/recreation)
    /// is detected and answered with a drive rescan.
    ///
    /// The unprivileged journal read carries no file names, only file reference numbers
    /// (FRNs), so paths resolve in two ways: a file that still exists resolves by FRN
    /// through OpenFileById; a deleted/renamed-away file resolves through the FRN map
    /// filled from the MFT scan (the parser knows every record's FRN) and maintained from
    /// the events themselves. A record neither can resolve degrades to reconciling its
    /// parent directory against the disk - correctness never depends on the map.
    /// </summary>
    sealed class UsnDriveWatcher : IDisposable
    {
        readonly UsnJournal journal;
        readonly Func<FsEvent, Task> process; //Enqueue into the drive's serialized queue
        readonly Action rescan;                 //Journal history lost => rescan this drive
        readonly Action<UsnDriveWatcher> dead;  //Journal unreadable for good => switch the drive to a watcher
        readonly NonBlocking.ConcurrentDictionary<ulong, INode> frnMap = new();
        volatile bool stop;

        /// <summary>
        /// True once the journal proved unreadable. The dead callback may fire before the
        /// creator registered this instance anywhere - the creator re-checks this flag
        /// after registering so the fallback can never slip through the gap.
        /// </summary>
        public bool IsDead { get; private set; }

        UsnDriveWatcher(UsnJournal journal, Func<FsEvent, Task> process, Action rescan, Action<UsnDriveWatcher> dead)
        {
            this.journal = journal;
            this.process = process;
            this.rescan = rescan;
            this.dead = dead;
            //A dedicated thread - the read blocks in the FSCTL waiting for changes
            new Thread(Loop) { IsBackground = true, Name = $"usn {journal.Root}" }.Start();
        }

        /// <summary>
        /// Start watching the volume's journal, positioned at its current end. NTFS only -
        /// the V2 record parsing and the 64-bit file references match what NTFS serves;
        /// ReFS (V3 records, 128-bit references), FAT and network mappings return null and
        /// the caller falls back to FileSystemWatcher, as does any volume whose journal
        /// cannot be opened. Call before starting the drive scan so no change can fall
        /// between the scan snapshot and the first read.
        /// The dead callback fires if the journal later turns out unreadable on this
        /// system (e.g. no unprivileged-read FSCTL on older Windows 10) - the caller must
        /// then swap this watcher for a FileSystemWatcher.
        /// </summary>
        public static UsnDriveWatcher TryStart(string root, Func<FsEvent, Task> process, Action rescan, Action<UsnDriveWatcher> dead)
        {
            try
            {
                if (!string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    return null;
            }
            catch { return null; }
            return UsnJournal.TryOpen(root) is { } journal ? new UsnDriveWatcher(journal, process, rescan, dead) : null;
        }

        /// <summary>
        /// (Re)fill the FRN map from a freshly published drive scan. MFT nodes carry their
        /// FRN; walked FileNodes do not (map stays empty and every record degrades to the
        /// resolve-or-reconcile path, still correct).
        /// </summary>
        public void Populate(IEnumerable<INode> nodes)
        {
            frnMap.Clear();
            foreach (var n in nodes)
                if (n.Frn != 0) frnMap[n.Frn] = n;
        }

        void Loop()
        {
            //Half-delivered renames within one batch: FRN whose RENAME_OLD_NAME was seen,
            //with the old path (null = unknown), waiting for its RENAME_NEW_NAME
            var pendingRenames = new Dictionary<ulong, string>();
            //Parents of records that could not be resolved - reconciled against the disk
            var unresolvedParents = new HashSet<ulong>();
            while (!stop)
            {
                List<UsnRecord> batch;
                bool invalid;
                try { batch = journal.ReadBatch(out invalid); }
                catch { batch = null; invalid = false; }
                if (batch == null)
                {
                    //Unreadable for good (volume removed, FSCTL unsupported on this system,
                    //access denied) => hand the drive over to a FileSystemWatcher
                    IsDead = true;
                    if (!stop)
                    {
                        $"USN journal on {journal.Root} unreadable => falling back to FileSystemWatcher".Debug();
                        try { dead(this); } catch { }
                    }
                    return;
                }
                if (invalid)
                {
                    $"USN journal on {journal.Root} lost history => rescan".Debug();
                    frnMap.Clear(); //Stale beyond repair - the rescan repopulates it
                    try { rescan(); } catch { }
                    continue;
                }
                if (batch.Count == 0)
                {
                    Thread.Sleep(300); //Wait-read timed out or is unsupported - don't spin
                    continue;
                }
                try
                {
                    var changedSeen = new HashSet<ulong>(); //Coalesce data-change records per file
                    foreach (var r in batch)
                    {
                        if (stop) return;
                        Translate(r, pendingRenames, unresolvedParents, changedSeen);
                    }
                    //A RENAME_OLD whose NEW half falls into the next batch is rare (batch
                    //boundary); its entry survives in pendingRenames and pairs up then.
                    Reconcile(unresolvedParents);
                }
                catch (Exception e) { $"USN processing on {journal.Root} failed: {e.Message}".Debug(); }
            }
        }

        void Translate(UsnRecord r, Dictionary<ulong, string> pendingRenames, HashSet<ulong> unresolvedParents, HashSet<ulong> changedSeen)
        {
            //Reason bits accumulate over a file's open-close session - classify by the
            //most existence-relevant bit. A delete record may still carry the create bits
            //of a short-lived temp file.
            if ((r.Reason & UsnJournal.ReasonFileDelete) != 0)
            {
                pendingRenames.Remove(r.Frn);
                var path = MapPath(r.Frn) ?? PathFromRecord(r);
                frnMap.TryRemove(r.Frn, out _);
                if (path != null) Process(new FsEvent(WatcherChangeTypes.Deleted, path));
                else unresolvedParents.Add(r.ParentFrn);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonRenameNewName) != 0)
            {
                var oldPath = pendingRenames.Remove(r.Frn, out var pending) ? pending : MapPath(r.Frn);
                var newPath = journal.TryResolvePath(r.Frn) ?? PathFromRecord(r);
                if (newPath == null) return; //Already gone again - its delete record follows
                if (oldPath == null) Process(new FsEvent(WatcherChangeTypes.Created, newPath)); //Moved in from an unindexed place
                else if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    Process(new FsEvent(WatcherChangeTypes.Renamed, newPath, oldPath));
                Remap(r.Frn, newPath);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonRenameOldName) != 0)
            {
                //First half of a rename - remember the old path for the NEW record.
                //Unknown old path: the old entry (if any) is staled - reconcile its parent.
                var oldPath = MapPath(r.Frn) ?? PathFromRecord(r);
                pendingRenames[r.Frn] = oldPath;
                if (oldPath == null) unresolvedParents.Add(r.ParentFrn);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonFileCreate) != 0)
            {
                var path = journal.TryResolvePath(r.Frn) ?? PathFromRecord(r);
                if (path == null) return; //Vanished before we read - nothing was indexed
                Process(new FsEvent(WatcherChangeTypes.Created, path));
                Remap(r.Frn, path);
                return;
            }
            //Data/attribute change
            if (!changedSeen.Add(r.Frn)) return;
            var changed = MapPath(r.Frn) ?? journal.TryResolvePath(r.Frn) ?? PathFromRecord(r);
            if (changed != null) Process(new FsEvent(WatcherChangeTypes.Changed, changed));
        }

        /// <summary>
        /// Path built from the record's own name and its parent's current path. The
        /// unprivileged read blanks all names, but the privileged one (elevated process)
        /// carries them - then even files the FRN map never saw resolve exactly, deleted
        /// ones included (their parent still exists). Handlers stat the disk, so a path
        /// misled by a parent renamed later in the journal indexes nothing wrong.
        /// </summary>
        string PathFromRecord(UsnRecord r)
        {
            if (string.IsNullOrEmpty(r.Name)) return null;
            var parent = journal.TryResolvePath(r.ParentFrn);
            return parent == null ? null : Path.Combine(parent, r.Name);
        }

        /// <summary>
        /// Enqueue and wait - records must apply in journal order (a rename must not race
        /// its own delete) and the wait paces us to the handler (backpressure).
        /// </summary>
        void Process(FsEvent e)
        {
            try { process(e).Wait(); } catch { }
        }

        /// <summary>
        /// Path of the file as the index knows it, through the FRN map. Verified against
        /// the live index: an entry staled by a parent-directory rename (its subtree was
        /// re-indexed with new instances under new paths) is healed or dropped, never
        /// trusted - a Remove on a wrong path would leave ghosts.
        /// </summary>
        string MapPath(ulong frn)
        {
            if (!frnMap.TryGetValue(frn, out var node)) return null;
            var path = node.FullName;
            if (FSChangeProcessor.Lookup(path) != null) return path; //Still indexed under that path
            var live = journal.TryResolvePath(frn);
            if (live == null)
            {
                frnMap.TryRemove(frn, out _);
                return null;
            }
            Remap(frn, live);
            return live;
        }

        /// <summary>Point the FRN at the node the index now holds under the path</summary>
        void Remap(ulong frn, string path)
        {
            var node = FSChangeProcessor.Lookup(path);
            if (node != null) frnMap[frn] = node;
            else frnMap.TryRemove(frn, out _); //Filtered out (e.g. non-existing) - resolve next time
        }

        /// <summary>
        /// Records whose FRN the map does not know (walked drive, staled entry) name at
        /// least their parent - resolve it and let the model diff that directory against
        /// the disk. Batched: one reconcile pass covers a whole storm's worth of misses.
        /// </summary>
        void Reconcile(HashSet<ulong> unresolvedParents)
        {
            if (unresolvedParents.Count == 0) return;
            var dirs = unresolvedParents.Select(journal.TryResolvePath).Where(p => p != null)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            unresolvedParents.Clear();
            if (dirs.Length == 0) return;
            try { FSChangeProcessor.ReconcileDirs(dirs).Wait(); } catch { }
        }

        public void Dispose()
        {
            stop = true;
            journal.Dispose(); //The blocked read wakes within its finite timeout
        }
    }
}
