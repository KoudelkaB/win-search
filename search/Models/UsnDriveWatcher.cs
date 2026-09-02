using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using search.Core;

namespace search.Models
{
    internal enum HardLinkBaselineAction
    {
        ProcessNow,
        DeferredUntilSnapshot,
        ScheduleWalkRescan
    }

    internal readonly record struct PendingHardLinkRepair(ulong Frn, uint Reason);

    /// <summary>
    /// Coordinates hard-link records with publication of the first drive snapshot. The USN
    /// watcher intentionally starts before the scan, so records can arrive while its FRN map
    /// is empty. Remember those records and replay them against an MFT baseline instead of
    /// immediately queuing a redundant full scan. A folder-walk snapshot cannot provide that
    /// baseline; later records request one bounded, trailing-edge retry instead.
    /// </summary>
    internal sealed class HardLinkBaselineGate
    {
        const int AwaitingSnapshot = 0;
        const int WalkSnapshot = 1;
        const int MftSnapshot = 2;

        readonly object sync = new();
        readonly Dictionary<ulong, uint> pending = new();
        int state;

        public bool HasMftBaseline => Volatile.Read(ref state) == MftSnapshot;

        public HardLinkBaselineAction Observe(ulong frn, uint reason)
        {
            lock (sync)
            {
                if (state == MftSnapshot) return HardLinkBaselineAction.ProcessNow;
                if (state == WalkSnapshot) return HardLinkBaselineAction.ScheduleWalkRescan;
                pending[frn] = pending.TryGetValue(frn, out var prior)
                    ? prior | reason : reason;
                return HardLinkBaselineAction.DeferredUntilSnapshot;
            }
        }

        public PendingHardLinkRepair[] Publish(bool hasMftBaseline)
        {
            lock (sync)
            {
                state = hasMftBaseline ? MftSnapshot : WalkSnapshot;
                var result = pending.Select(pair =>
                    new PendingHardLinkRepair(pair.Key, pair.Value)).ToArray();
                pending.Clear();
                return result;
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                state = AwaitingSnapshot;
                pending.Clear();
            }
        }
    }

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
        readonly Action<DriveScanReason> rescan; //Journal ambiguity => rescan this drive
        readonly Action<UsnDriveWatcher> dead;  //Journal unreadable for good => switch the drive to a watcher
        readonly FrnMap frnMap = new();
        readonly HardLinkBaselineGate hardLinkBaseline = new();
        readonly object exactRescanLock = new();
        Timer exactRescanTimer;
        int exactRescanRequests;
        ulong exactRescanFirstFrn;
        uint exactRescanReasons;
        string exactRescanFailure;
        volatile bool stop;

        //A hard-link storm can emit thousands of records. One exact rebuild after a quiet
        //window is both cheaper and more accurate than trying to rescan for every record.
        internal const int ExactRescanQuietMs = 1000;
        //A walked snapshot has no FRN/link topology, so an immediate retry normally walks
        //again and establishes no new repair capability. Bound that degraded-mode work.
        internal const int WalkExactRescanQuietMs = 60_000;

        /// <summary>
        /// True once the journal proved unreadable. The dead callback may fire before the
        /// creator registered this instance anywhere - the creator re-checks this flag
        /// after registering so the fallback can never slip through the gap.
        /// </summary>
        public bool IsDead { get; private set; }

        /// <summary>
        /// True when the journal is live and its FRN map has been seeded by a complete MFT
        /// scan. In that state a recursive delete supplies an exact, ordered record for
        /// every indexed descendant. An app-owned directory delete may therefore remove
        /// its visible root immediately without conservatively scanning the whole index;
        /// the journal records remove the descendants that follow.
        /// </summary>
        public bool ReportsCompleteDirectoryDeletes => !IsDead && hardLinkBaseline.HasMftBaseline;

        /// <summary>Live FRN-map watermark paired with a drive-scan start.</summary>
        internal long FrnMutationVersion => frnMap.MutationVersion;

        UsnDriveWatcher(UsnJournal journal, Func<FsEvent, Task> process,
            Action<DriveScanReason> rescan, Action<UsnDriveWatcher> dead)
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
        public static UsnDriveWatcher TryStart(string root, Func<FsEvent, Task> process,
            Action<DriveScanReason> rescan, Action<UsnDriveWatcher> dead)
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
        public void Populate(IEnumerable<INode> nodes,
            long preserveMutationsAfter = long.MaxValue)
        {
            frnMap.Populate(nodes, preserveMutationsAfter);
            //Only the MFT reader supplies a complete FRN-addressable baseline. A walked
            //fallback collection contains path nodes without file references, so deleted
            //records may still need conservative parent reconciliation.
            var mftBaseline = nodes is IFrnNodeSource;
            var pending = hardLinkBaseline.Publish(mftBaseline);
            if (pending.Length == 0) return;
            if (mftBaseline)
            {
                foreach (var repair in pending) ReplayHardLinkRepair(repair);
            }
            else
            {
                RequestExactMftRescan(pending[0].Frn,
                    pending.Aggregate(0u, (reason, repair) => reason | repair.Reason),
                    $"no FRN baseline after folder walk; deferred={pending.Length}",
                    WalkExactRescanQuietMs);
            }
        }

        void Loop()
        {
            //Half-delivered renames within one batch: FRN whose RENAME_OLD_NAME was seen,
            //with the old path (null = unknown), waiting for its RENAME_NEW_NAME
            var pendingRenames = new Dictionary<ulong, string>();
            //A hard-link record can be translated before the create/rename events already
            //ahead of it have reached the serialized model queue. Retry once after that
            //queue catches up; a transient lookup miss must not become a 20-second rescan.
            var delayedHardLinkRepairs = new Dictionary<ulong, uint>();
            //Parents of records that could not be resolved - reconciled against the disk
            var unresolvedParents = new UnresolvedParents();
            //FRNs whose create/rename never resolved to a path: nothing was ever indexed
            //for them, so their later delete has nothing to prune - dropping it silently
            //saves the parent reconcile a short-lived temp file would otherwise cost
            var ghosts = new HashSet<ulong>();
            long lastReconcile = 0; //Reconciles are full-index passes - throttled in storms
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
                    hardLinkBaseline.Reset();
                    frnMap.Clear(); //Stale beyond repair - the rescan repopulates it
                    try { rescan(DriveScanReason.UsnHistoryLost); } catch { }
                    continue;
                }
                if (batch.Count == 0)
                {
                    //Quiet moment - flush what the reconcile throttle held back in the storm
                    if (unresolvedParents.Count > 0)
                    {
                        Reconcile(unresolvedParents);
                        lastReconcile = Environment.TickCount64;
                    }
                    Thread.Sleep(300); //Wait-read timed out or is unsupported - don't spin
                    continue;
                }
                try
                {
                    if (ghosts.Count > (1 << 16)) ghosts.Clear(); //Bound memory - losing entries only costs reconciles
                    var changedSeen = new HashSet<ulong>(); //Coalesce data-change records per file
                    var createdSeen = new HashSet<ulong>(); //Create reason repeats while the same handle is open
                    foreach (var r in batch)
                    {
                        if (stop) return;
                        Translate(r, pendingRenames, unresolvedParents, changedSeen,
                            createdSeen, ghosts, delayedHardLinkRepairs);
                    }
                    //Wait for the batch's last event only: the drive queue is FIFO with a single
                    //consumer, so journal order is preserved without waiting between events, and
                    //the queue can hand the handler real batches (deletes coalesce, grid passes
                    //are shared). One wait per read batch still paces the reader to the handler -
                    //a per-record wait would cap it at one pipeline round trip per change, fall
                    //behind on a busy volume and let the journal wrap past our position, which
                    //costs a full drive rescan every time.
                    var last = lastEnqueued;
                    lastEnqueued = null;
                    if (last != null) try { last.Wait(); } catch { }
                    RetryDelayedHardLinkRepairs(delayedHardLinkRepairs);
                    //A successful retry enqueued one absolute snapshot/delta after all of
                    //the structural events it depended on. Apply it before reading another
                    //journal batch so its optimistic link baseline cannot run ahead again.
                    last = lastEnqueued;
                    lastEnqueued = null;
                    if (last != null) try { last.Wait(); } catch { }
                    //A RENAME_OLD whose NEW half falls into the next batch is rare (batch
                    //boundary); its entry survives in pendingRenames and pairs up then.
                    //A reconcile is a full-index pass - during sustained activity run at
                    //most one per interval and let the parents accumulate in between (the
                    //quiet-timeout flush above covers the tail after the storm ends).
                    if (unresolvedParents.Count > 0 && Environment.TickCount64 - lastReconcile >= 5000)
                    {
                        Reconcile(unresolvedParents);
                        lastReconcile = Environment.TickCount64;
                    }
                }
                catch (Exception e) { $"USN processing on {journal.Root} failed: {e.Message}".Debug(); }
            }
        }

        void Translate(UsnRecord r, Dictionary<ulong, string> pendingRenames,
            UnresolvedParents unresolvedParents, HashSet<ulong> changedSeen,
            HashSet<ulong> createdSeen, HashSet<ulong> ghosts,
            Dictionary<ulong, uint> delayedHardLinkRepairs)
        {
            var hadMultipleLinks = frnMap.HasMultipleLinks(r.Frn);
            var repairedHardLinks = false;
            var hardLinkFileGone = false;
            var liveLinkCount = 0;
            var baselineAction = (r.Reason & UsnJournal.ReasonHardLinkChange) != 0
                ? hardLinkBaseline.Observe(r.Frn, r.Reason)
                : HardLinkBaselineAction.ProcessNow;
            //Even before a complete baseline, a file created after watcher startup may already
            //have enough dynamic FRN/parent state for targeted repair. Try it now; the gate only
            //controls the fallback. Deferred records are still replayed after publication so a
            //scan that replaced the dynamic map cannot lose the successful repair.
            if (CanRepairHardLinkIncrementally(r.Reason, hadMultipleLinks))
            {
                repairedHardLinks = TryQueueHardLinkUpdate(r.Frn,
                    out liveLinkCount, out hardLinkFileGone, out var failure);
                if (!repairedHardLinks && !hardLinkFileGone)
                {
                    if (baselineAction == HardLinkBaselineAction.ProcessNow)
                        delayedHardLinkRepairs[r.Frn] =
                            delayedHardLinkRepairs.TryGetValue(r.Frn, out var prior)
                                ? prior | r.Reason : r.Reason;
                    else if (baselineAction == HardLinkBaselineAction.ScheduleWalkRescan)
                        RequestExactMftRescan(r.Frn, r.Reason, failure,
                            WalkExactRescanQuietMs);
                }
            }
            if (!repairedHardLinks && !hardLinkFileGone
                && RequiresExactMftRescan(r.Reason, hadMultipleLinks))
            {
                var failure = "canonical path change on a multi-linked file";
                if (baselineAction == HardLinkBaselineAction.ProcessNow)
                    RequestExactMftRescan(r.Frn, r.Reason, failure);
                else if (baselineAction == HardLinkBaselineAction.ScheduleWalkRescan)
                    RequestExactMftRescan(r.Frn, r.Reason, failure,
                        WalkExactRescanQuietMs);
            }

            //Hard-link reason flags can retain FILE_CREATE/FILE_DELETE from the link
            //operation. The targeted topology diff already represents that name change;
            //treating it as the canonical node's create/delete would corrupt the index.
            const uint renameReasons = UsnJournal.ReasonRenameOldName
                | UsnJournal.ReasonRenameNewName;
            if (repairedHardLinks && liveLinkCount > 0
                && (r.Reason & UsnJournal.ReasonHardLinkChange) != 0
                && (r.Reason & renameReasons) == 0)
                return;

            //Reason bits accumulate over a file's open-close session - classify by the
            //most existence-relevant bit. A delete record may still carry the create bits
            //of a short-lived temp file.
            if ((r.Reason & UsnJournal.ReasonFileDelete) != 0)
            {
                pendingRenames.Remove(r.Frn);
                //Path the map knew before MapPath heals or drops the entry - the
                //nothing-indexed proof below needs it even when verification fails
                var lastKnown = frnMap.TryGetValue(r.Frn, out var known) ? known.FullName : null;
                var path = MapPath(r.Frn) ?? PathFromRecord(r);
                frnMap.Remove(r.Frn);
                if (path != null)
                {
                    ghosts.Remove(r.Frn);
                    //NTFS emits a FILE_DELETE record for every descendant removed by a
                    //recursive delete. Mark that completeness guarantee so SearchModel does
                    //not rescan the entire million-node index for each directory record.
                    Process(new FsEvent(WatcherChangeTypes.Deleted, path,
                        descendantDeletesReported: true, frn: r.Frn,
                        ntfsAttributes: r.Attributes));
                }
                //A ghost's create never resolved, so nothing was indexed - nothing to
                //prune. Same when the index holds nothing under the last known path while
                //that path's parent is still indexed: the entry was already pruned (the
                //app's own delete echoes ahead of the journal record) - a parent renamed
                //away meanwhile would have re-keyed the parent path and fails this proof.
                else if (!ghosts.Remove(r.Frn) && !ProvablyUnindexed(lastKnown))
                    unresolvedParents.Add(r.ParentFrn);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonRenameNewName) != 0)
            {
                //heal:false - the map's stored path is the pre-rename path; healing it
                //would resolve the file at its NEW location (== newPath => swallowed)
                var oldPath = pendingRenames.Remove(r.Frn, out var pending) ? pending : MapPath(r.Frn, heal: false);
                var newPath = journal.TryResolvePath(r.Frn) ?? PathFromRecord(r);
                if (newPath == null)
                {
                    //Already gone again - its delete record follows (a ghost when nothing
                    //was indexed under the old path either: that delete then drops silently)
                    if (oldPath == null) ghosts.Add(r.Frn);
                    //Something IS indexed under the old name. A terminal delete record
                    //normally follows and prunes it, but a resolve that failed for any
                    //other reason never produces one - reconcile the directory the stale
                    //name sits in so it cannot survive in the grid forever. That is the
                    //OLD parent: this record's ParentFrn is where the file moved TO, and
                    //a cross-directory move leaves nothing stale there.
                    else unresolvedParents.Add(Path.GetDirectoryName(oldPath));
                    return;
                }
                if (UsnJournal.IsNtfsDeletedPath(journal.Root, newPath))
                {
                    //POSIX-style delete can first rename the file into NTFS's private
                    //$Extend\$Deleted namespace. Report the user-visible path as deleted
                    //now; never index the private holding name while a handle drains.
                    frnMap.Remove(r.Frn);
                    ghosts.Add(r.Frn); //Swallow the later terminal FILE_DELETE duplicate.
                    if (oldPath != null)
                        Process(new FsEvent(WatcherChangeTypes.Deleted, oldPath,
                            descendantDeletesReported: true, frn: r.Frn,
                            ntfsAttributes: r.Attributes));
                    return;
                }
                ghosts.Remove(r.Frn);
                if (oldPath == null) Process(new FsEvent(WatcherChangeTypes.Created,
                    newPath, frn: r.Frn, ntfsAttributes: r.Attributes)); //Moved in from an unindexed place
                else if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                    Process(new FsEvent(WatcherChangeTypes.Renamed, newPath, oldPath,
                        frn: r.Frn, ntfsAttributes: r.Attributes));
                Remap(r.Frn, newPath);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonRenameOldName) != 0)
            {
                //First half of a rename - remember the old path for the NEW record.
                //Unknown old path: the old entry (if any) is staled - reconcile its parent
                //(a ghost never had an entry, so there is nothing to stale).
                //heal:false - see the NEW_NAME branch (the file is already at its new path).
                var oldPath = MapPath(r.Frn, heal: false) ?? PathFromRecord(r);
                pendingRenames[r.Frn] = oldPath;
                if (oldPath == null && !ghosts.Contains(r.Frn)) unresolvedParents.Add(r.ParentFrn);
                return;
            }
            if ((r.Reason & UsnJournal.ReasonFileCreate) != 0)
            {
                var path = journal.TryResolvePath(r.Frn) ?? PathFromRecord(r);
                if (path == null)
                {
                    ghosts.Add(r.Frn); //Vanished before we read - nothing was indexed
                    return;
                }
                //The create bits of a write-then-rename save (foo.tmp -> foo) are still set
                //on records written before the rename, and the path above is resolved LIVE -
                //so the second create record of one session commonly resolves to the name
                //the file has after the rename. Report the move instead of a second create,
                //otherwise the temp name this watcher already reported stays in the index
                //(and the rename records that follow compare equal and are swallowed).
                if (ReportMove(r, path, pendingRenames, ghosts)) return;
                //USN reason flags are cumulative for an open-close session. Several records
                //for the same new file can therefore all carry FILE_CREATE; the first queued
                //event observes the final on-disk metadata when the serialized handler runs.
                if (!createdSeen.Add(r.Frn)) return;
                ghosts.Remove(r.Frn);
                Process(new FsEvent(WatcherChangeTypes.Created, path,
                    frn: r.Frn, ntfsAttributes: r.Attributes));
                Remap(r.Frn, path);
                return;
            }
            //Data/attribute change
            if (repairedHardLinks) return; //Snapshot + every link parent were updated together.
            if (!changedSeen.Add(r.Frn)) return;
            //A change record written before a rename can be read after it too - MapPath then
            //heals to the new path and reports what name it left behind
            var changed = MapPath(r.Frn, heal: true, out var movedFrom);
            if (movedFrom != null)
            {
                pendingRenames.Remove(r.Frn);
                Process(new FsEvent(WatcherChangeTypes.Renamed, changed, movedFrom,
                    frn: r.Frn, ntfsAttributes: r.Attributes));
            }
            if (changed == null && journal.TryResolvePath(r.Frn) is { } live)
            {
                //Map the resolved path - a hot file (growing log, download) must not pay
                //the OpenFileById round trip again on every following batch
                ghosts.Remove(r.Frn);
                Remap(r.Frn, live);
                changed = live;
            }
            changed ??= PathFromRecord(r);
            if (changed != null) Process(new FsEvent(WatcherChangeTypes.Changed, changed,
                frn: r.Frn, ntfsAttributes: r.Attributes));
        }

        void RetryDelayedHardLinkRepairs(Dictionary<ulong, uint> repairs)
        {
            if (repairs.Count == 0) return;
            var pending = repairs.Select(pair =>
                new PendingHardLinkRepair(pair.Key, pair.Value)).ToArray();
            repairs.Clear();
            foreach (var repair in pending)
            {
                if (!CanRepairHardLinkIncrementally(repair.Reason,
                        frnMap.HasMultipleLinks(repair.Frn))) continue;
                if (!TryQueueHardLinkUpdate(repair.Frn, out _, out var gone,
                        out var failure) && !gone)
                    RequestExactMftRescan(repair.Frn, repair.Reason,
                        $"targeted repair still failed after queue catch-up: {failure}");
            }
        }

        /// <summary>
        /// Report that a file reference this watcher already named moved to a different
        /// path, when no rename record produced that transition. USN reason bits accumulate
        /// over a whole open-close session while the paths are resolved live, so a record
        /// can arrive with stale reasons and a current - already renamed - path. Leaving the
        /// previously reported name in place would keep it in the grid forever for a file
        /// that no longer has it. Returns false (and reports nothing) when the reference is
        /// still known under exactly this path.
        /// </summary>
        bool ReportMove(UsnRecord r, string path, Dictionary<ulong, string> pendingRenames,
            HashSet<ulong> ghosts)
        {
            var reported = MapPath(r.Frn, heal: false);
            if (reported == null
                || string.Equals(reported, path, StringComparison.OrdinalIgnoreCase))
                return false;
            //The rename records of this same transition may still follow; they resolve the
            //old name from the map, which now holds the new path, and are then swallowed.
            pendingRenames.Remove(r.Frn);
            ghosts.Remove(r.Frn);
            Process(new FsEvent(WatcherChangeTypes.Renamed, path, reported,
                frn: r.Frn, ntfsAttributes: r.Attributes));
            Remap(r.Frn, path);
            return true;
        }

        /// <summary>
        /// A hard-link topology change, or a size change of an already multi-linked file,
        /// can be repaired by enumerating that one FRN's live names and diffing their
        /// parents against the sparse topology retained from the MFT scan.
        /// </summary>
        internal static bool CanRepairHardLinkIncrementally(uint reason, bool hasMultipleLinks)
        {
            if ((reason & UsnJournal.ReasonHardLinkChange) != 0) return true;
            if (!hasMultipleLinks) return false;
            const uint size = UsnJournal.ReasonDataOverwrite | UsnJournal.ReasonDataExtend
                | UsnJournal.ReasonDataTruncation;
            return (reason & size) != 0;
        }

        /// <summary>
        /// Renaming/deleting a canonical name of a multi-linked record still needs the rare
        /// fallback: the compact index intentionally stores one searchable row per FRN, and
        /// changing which name owns that row is separate from adjusting link-parent aggregates.
        /// </summary>
        internal static bool RequiresExactMftRescan(uint reason, bool hasMultipleLinks)
        {
            if (!hasMultipleLinks) return false;
            const uint canonicalPath = UsnJournal.ReasonFileDelete
                | UsnJournal.ReasonRenameOldName | UsnJournal.ReasonRenameNewName;
            return (reason & canonicalPath) != 0;
        }

        void ReplayHardLinkRepair(PendingHardLinkRepair repair)
        {
            var hadMultipleLinks = frnMap.HasMultipleLinks(repair.Frn);
            var repaired = false;
            var fileReferenceGone = false;
            if (CanRepairHardLinkIncrementally(repair.Reason, hadMultipleLinks))
            {
                repaired = TryQueueHardLinkUpdate(repair.Frn, out _,
                    out fileReferenceGone, out var failure, trackLoopCompletion: false);
                if (!repaired && !fileReferenceGone)
                    RequestExactMftRescan(repair.Frn, repair.Reason,
                        $"deferred targeted repair failed: {failure}");
            }
            if (!repaired && !fileReferenceGone
                && RequiresExactMftRescan(repair.Reason, hadMultipleLinks))
                RequestExactMftRescan(repair.Frn, repair.Reason,
                    "deferred canonical path change on a multi-linked file");
        }

        bool TryQueueHardLinkUpdate(ulong frn, out int liveLinkCount,
            out bool fileReferenceGone, out string failure,
            bool trackLoopCompletion = true)
        {
            liveLinkCount = 0;
            fileReferenceGone = false;
            failure = null;
            if (!frnMap.TryGetValue(frn, out var mapped) || mapped.IsDirectory)
            {
                failure = $"FRN {frn:x} is not mapped";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }
            var path = mapped.FullName;
            var indexed = FSChangeProcessor.Lookup(path);
            if (indexed == null || indexed.IsDirectory)
            {
                failure = $"canonical path is not indexed ({path})";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }
            if (!frnMap.TryGetLinkState(frn, out var oldParentRefs, out var oldSize))
            {
                failure = $"no baseline parents for {frn:x}";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }
            if (!journal.TryGetHardLinkPaths(frn, out var currentPaths,
                    out fileReferenceGone)
                || currentPaths.Length == 0)
            {
                if (fileReferenceGone)
                    failure = $"file reference {frn:x} vanished before enumeration";
                else
                    failure = $"link-name enumeration for {frn:x}";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }
            if (!INode.TryReadMetadata(path, out var snapshot) || snapshot.IsDirectory)
            {
                failure = $"canonical metadata ({path})";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }

            var oldParentPaths = new string[oldParentRefs.Length];
            for (var i = 0; i < oldParentRefs.Length; i++)
            {
                if (!TryResolveLinkParent(oldParentRefs[i], out var parent))
                {
                    failure = $"old parent {oldParentRefs[i]:x}";
                    $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                    return false;
                }
                oldParentPaths[i] = parent.FullName;
            }

            var currentParentRefs = new ulong[currentPaths.Length];
            var currentParentPaths = new string[currentPaths.Length];
            for (var i = 0; i < currentPaths.Length; i++)
            {
                var parentPath = Path.GetDirectoryName(currentPaths[i]);
                var parent = string.IsNullOrEmpty(parentPath)
                    ? null : FSChangeProcessor.Lookup(parentPath);
                if (parent?.IsDirectory != true || parent.Frn == 0)
                {
                    failure = $"current parent {parentPath}";
                    $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                    return false;
                }
                currentParentRefs[i] = parent.Frn;
                currentParentPaths[i] = parent.FullName;
            }

            if (!TryCalculateHardLinkParentDeltas(oldParentPaths, currentParentPaths,
                    oldParentRefs.Length == 1 ? indexed.Size : oldSize,
                    snapshot.Size, out var deltas))
            {
                failure = "aggregate delta overflow";
                $"USN targeted hard-link repair on {journal.Root} failed: {failure}".Debug();
                return false;
            }

            liveLinkCount = currentParentRefs.Length;
            //Publish the queried state before enqueueing. Several records for one open
            //handle can share a USN batch while the serialized model queue is still
            //waiting; the following record must diff from this state, not add the same
            //size delta again from the not-yet-updated index node.
            frnMap.SetLinkState(frn, currentParentRefs, snapshot.Size);
            Process(FsEvent.HardLinkUpdate(path, frn, indexed, snapshot, deltas),
                trackLoopCompletion);
            ($"USN hard-link update on {journal.Root}: frn={frn:x}, links "
                + $"{oldParentRefs.Length}->{currentParentRefs.Length}, parents changed={deltas.Length}")
                .Debug();
            return true;
        }

        bool TryResolveLinkParent(ulong parentId, out INode parent)
        {
            if (frnMap.TryGetValue(parentId, out parent) && parent.IsDirectory)
                return true;
            var path = journal.TryResolvePath(parentId);
            parent = path == null ? null : FSChangeProcessor.Lookup(path);
            return parent?.IsDirectory == true;
        }

        internal static bool TryCalculateHardLinkParentDeltas(
            IReadOnlyList<string> oldParents, IReadOnlyList<string> currentParents,
            ulong oldSize, ulong currentSize, out HardLinkParentDelta[] deltas)
        {
            deltas = null;
            if (oldParents == null || currentParents == null
                || oldSize > long.MaxValue || currentSize > long.MaxValue)
                return false;
            var counts = new Dictionary<string, (int Old, int Current)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var path in oldParents)
            {
                if (string.IsNullOrEmpty(path)) return false;
                counts.TryGetValue(path, out var count);
                if (count.Old == int.MaxValue) return false;
                counts[path] = (count.Old + 1, count.Current);
            }
            foreach (var path in currentParents)
            {
                if (string.IsNullOrEmpty(path)) return false;
                counts.TryGetValue(path, out var count);
                if (count.Current == int.MaxValue) return false;
                counts[path] = (count.Old, count.Current + 1);
            }

            var result = new List<HardLinkParentDelta>(counts.Count);
            foreach (var (path, count) in counts.OrderBy(x => x.Key,
                         StringComparer.OrdinalIgnoreCase))
            {
                if ((oldSize != 0 && (ulong)count.Old > (ulong)long.MaxValue / oldSize)
                    || (currentSize != 0
                        && (ulong)count.Current > (ulong)long.MaxValue / currentSize))
                    return false;
                var oldContribution = checked((long)oldSize * count.Old);
                var currentContribution = checked((long)currentSize * count.Current);
                var sizeDelta = currentContribution - oldContribution;
                var countDelta = (long)count.Current - count.Old;
                if (sizeDelta != 0 || countDelta != 0)
                    result.Add(new HardLinkParentDelta(path, sizeDelta, countDelta));
            }
            deltas = result.ToArray();
            return true;
        }

        void RequestExactMftRescan(ulong frn, uint reason, string failure,
            int quietMs = ExactRescanQuietMs)
        {
            lock (exactRescanLock)
            {
                if (stop) return;
                if (exactRescanRequests++ == 0) exactRescanFirstFrn = frn;
                exactRescanReasons |= reason;
                exactRescanFailure ??= failure;
                exactRescanTimer ??= new Timer(_ => FireExactMftRescan(),
                    null, Timeout.Infinite, Timeout.Infinite);
                exactRescanTimer.Change(Math.Max(ExactRescanQuietMs, quietMs),
                    Timeout.Infinite);
            }
        }

        void FireExactMftRescan()
        {
            int requests;
            ulong firstFrn;
            uint reasons;
            string failure;
            lock (exactRescanLock)
            {
                if (stop) return;
                requests = exactRescanRequests;
                firstFrn = exactRescanFirstFrn;
                reasons = exactRescanReasons;
                failure = exactRescanFailure;
                exactRescanRequests = 0;
                exactRescanFirstFrn = 0;
                exactRescanReasons = 0;
                exactRescanFailure = null;
            }
            if (requests == 0) return;
            var detail = $"USN hard-link rescan on {journal.Root}: requests={requests}; "
                + $"firstFrn={firstFrn:x}; reasons=0x{reasons:x}; failure={failure}";
            StorageMaintenance.AppendDiagnostic(detail);
            detail.Debug();
            try { if (!stop) rescan(DriveScanReason.UsnHardLinkChange); } catch { }
        }

        /// <summary>
        /// True when provably nothing is indexed for the file last known under this path:
        /// the index does not hold the path (the caller's MapPath just verified that) but
        /// still holds its parent directory - so the entry was pruned by path, not moved
        /// away by a parent rename (which would have re-keyed the parent too). Deleting
        /// such a file needs no event and no reconcile.
        /// </summary>
        static bool ProvablyUnindexed(string lastKnown)
            => lastKnown != null
            && Path.GetDirectoryName(lastKnown) is { Length: > 0 } parent
            && FSChangeProcessor.Lookup(parent) != null;

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
            //The parent is usually a scan-indexed directory - its map entry saves the
            //OpenFileById round trip (MapPath verifies and heals it like any entry)
            var parent = MapPath(r.ParentFrn) ?? journal.TryResolvePath(r.ParentFrn);
            return parent == null ? null : Path.Combine(parent, r.Name);
        }

        Task lastEnqueued; //Loop-thread only - the newest enqueued event's completion

        /// <summary>
        /// Enqueue without waiting - the drive queue applies events in order (FIFO, single
        /// consumer), so a rename cannot race its own delete. The Loop waits once per read
        /// batch on the last enqueued event (backpressure), never per record: at most one
        /// read buffer's worth of records is ever in flight.
        /// </summary>
        void Process(FsEvent e, bool trackLoopCompletion = true)
        {
            try
            {
                var queued = process(e);
                if (trackLoopCompletion) lastEnqueued = queued;
            }
            catch { }
        }

        /// <summary>
        /// Path of the file as the index knows it, through the FRN map. Verified against
        /// the live index: an entry staled by a parent-directory rename (its subtree was
        /// re-indexed with new instances under new paths) is healed or dropped, never
        /// trusted - a Remove on a wrong path would leave ghosts.
        /// heal=false skips the live-disk repair - a RENAME record's OLD path must never
        /// resolve from the disk, where the file already sits under its NEW path (the
        /// rename would compare equal and be swallowed).
        /// A placeholder entry is trusted without the index check: it means this watcher
        /// already enqueued a Created/Renamed for that exact path and the ordered drive
        /// queue (FIFO, single consumer) has not applied it yet. Whatever we enqueue now
        /// is applied after it, so the path is as good as indexed. Treating it as unknown
        /// is what left a file that was created and immediately renamed away (atomic
        /// "write temp, rename over the target" saves) or deleted indexed forever under a
        /// name that no longer exists on disk.
        /// </summary>
        string MapPath(ulong frn, bool heal = true) => MapPath(frn, heal, out _);

        /// <summary>
        /// <inheritdoc cref="MapPath(ulong, bool)"/>
        /// movedFrom is the name this watcher had already reported when healing found the
        /// file somewhere else - the caller must report that move, nothing else will.
        /// </summary>
        string MapPath(ulong frn, bool heal, out string movedFrom)
        {
            movedFrom = null;
            if (!frnMap.TryGetValue(frn, out var node)) return null;
            var path = node.FullName;
            if (FSChangeProcessor.Lookup(path) != null) return path; //Still indexed under that path
            var queued = node is PathNode; //Our own event for this path is still in flight
            if (!heal) return queued ? path : null;
            var live = journal.TryResolvePath(frn);
            if (live == null)
            {
                //Gone from disk - but a queued create still needs its exact prune event
                if (queued) return path;
                frnMap.Remove(frn);
                return null;
            }
            //A path this watcher reported itself is not "staled by a parent rename" - the
            //queued event will put it into the index, so the move has to be reported.
            if (queued && !string.Equals(path, live, StringComparison.OrdinalIgnoreCase))
                movedFrom = path;
            Remap(frn, live);
            return live;
        }

        /// <summary>
        /// Point the FRN at the node the index now holds under the path - or at a
        /// path-only placeholder while the async handler has not indexed it yet: a temp
        /// file's delete would otherwise find the map empty (its Created is still in the
        /// queue) and degrade to a parent reconcile. MapPath never trusts the placeholder
        /// itself - it verifies against the live index or the disk like any entry.
        /// </summary>
        void Remap(ulong frn, string path)
            => frnMap.Set(frn, FSChangeProcessor.Lookup(path) ?? new PathNode(path));

        /// <summary>
        /// FRN -> node map. A normal MFT scan supplies its already allocated record-number
        /// table directly, so lookup is one bounds check and array read with no second full
        /// map. Generic/test sources use fixed-size pages; watcher mutations sit in a tiny
        /// per-entry override map. The full FRN must match exactly, so a reused record
        /// (same entry, new sequence) never resolves to the old file.
        /// Pages are published as an immutable table. Lookups are lock-free; Populate,
        /// Clear, Set and Remove serialize publication/mutation so watcher changes arriving
        /// during a repopulation wait and are applied to the new table rather than lost.
        /// </summary>
        internal sealed class FrnMap
        {
            const ulong EntryMask = 0xffffffffffff;
            const int PageBits = 12;
            const int PageSize = 1 << PageBits;
            const ulong PageMask = PageSize - 1;

            sealed class Page
            {
                public readonly ulong[] Frns = new ulong[PageSize]; //Full FRN per slot; 0 = free
                public readonly INode[] Nodes = new INode[PageSize];
            }

            sealed class PageTable
            {
                public static PageTable Empty() => new(null, new Dictionary<ulong, Page>(),
                    new(), new(), new());
                public readonly IFrnNodeSource Source;
                public readonly Dictionary<ulong, Page> Pages;
                public readonly NonBlocking.ConcurrentDictionary<ulong, INode> Sparse;
                public readonly NonBlocking.ConcurrentDictionary<ulong, SourceOverride> Overrides;
                public readonly NonBlocking.ConcurrentDictionary<ulong, LinkOverride> LinkOverrides;
                public PageTable(IFrnNodeSource source, Dictionary<ulong, Page> pages,
                    NonBlocking.ConcurrentDictionary<ulong, INode> sparse,
                    NonBlocking.ConcurrentDictionary<ulong, SourceOverride> overrides,
                    NonBlocking.ConcurrentDictionary<ulong, LinkOverride> linkOverrides)
                {
                    Source = source;
                    Pages = pages;
                    Sparse = sparse;
                    Overrides = overrides;
                    LinkOverrides = linkOverrides;
                }
            }

            sealed class SourceOverride
            {
                public readonly ulong Frn;
                public readonly INode Node; //null = tombstone hiding the immutable scan slot
                public readonly long Version;
                public SourceOverride(ulong frn, INode node, long version)
                {
                    Frn = frn;
                    Node = node;
                    Version = version;
                }
            }

            sealed class LinkOverride
            {
                public readonly ulong Frn;
                public readonly ulong[] Parents;
                public readonly ulong Size;
                public readonly long Version;
                public LinkOverride(ulong frn, ulong[] parents, ulong size, long version)
                {
                    Frn = frn;
                    Parents = parents;
                    Size = size;
                    Version = version;
                }
            }

            readonly object mutationLock = new();
            volatile PageTable pageTable = PageTable.Empty();
            long mutationVersion;

            public long MutationVersion => Interlocked.Read(ref mutationVersion);

            public bool TryGetValue(ulong frn, out INode node)
            {
                var entry = frn & EntryMask;
                var table = pageTable;
                if (!table.Overrides.IsEmpty && table.Overrides.TryGetValue(entry, out var changed))
                {
                    if (changed.Frn == frn)
                    {
                        node = changed.Node;
                        return node != null;
                    }
                    //A live override owns this reused slot and hides the older scanned
                    //sequence. A tombstone for an older sequence, preserved across a later
                    //scan, does not hide the new source owner.
                    if (changed.Node != null)
                    {
                        node = null;
                        return false;
                    }
                }
                if (table.Source != null) return table.Source.TryGetByFrn(frn, out node);
                if (table.Pages.TryGetValue(entry >> PageBits, out var page))
                {
                    var slot = (int)(entry & PageMask);
                    node = page.Frns[slot] == frn ? page.Nodes[slot] : null;
                    if (node != null) return true;
                }
                return table.Sparse.TryGetValue(frn, out node);
            }

            public void Set(ulong frn, INode node)
            {
                lock (mutationLock)
                {
                    var entry = frn & EntryMask;
                    var version = ++mutationVersion;
                    if (pageTable.Source != null)
                    {
                        //Metadata-only events keep the original MFT node in the live index.
                        //Do not grow the override dictionary for those very common writes.
                        if (pageTable.Source.TryGetByFrn(frn, out var scanned)
                            && ReferenceEquals(scanned, node))
                        {
                            pageTable.Overrides.TryRemove(entry, out _);
                            return;
                        }
                        pageTable.Overrides[entry] = new SourceOverride(frn, node, version);
                        return;
                    }
                    //Keep dynamic entries in the same versioned overlay used by an MFT
                    //source. A later Populate can then retain everything newer than the
                    //scan's watermark, including startup creates before the first baseline.
                    pageTable.Overrides[entry] = new SourceOverride(frn, node, version);
                }
            }

            public void Remove(ulong frn)
            {
                lock (mutationLock)
                {
                    var entry = frn & EntryMask;
                    var version = ++mutationVersion;
                    //A stale delete must not hide a newer owner of the same MFT slot.
                    if (TryGetValue(frn, out _))
                    {
                        pageTable.Overrides[entry] = new SourceOverride(frn, null, version);
                        pageTable.LinkOverrides[entry] =
                            new LinkOverride(frn, Array.Empty<ulong>(), 0, version);
                    }
                }
            }

            public void Clear()
            {
                lock (mutationLock)
                {
                    pageTable = PageTable.Empty();
                    ++mutationVersion;
                }
            }

            public bool HasMultipleLinks(ulong frn)
            {
                var table = pageTable;
                var entry = frn & EntryMask;
                if (table.LinkOverrides.TryGetValue(entry, out var changed))
                    return changed.Frn == frn && changed.Parents.Length > 1;
                return table.Source?.HasMultipleLinks(frn) == true;
            }

            public bool TryGetLinkParents(ulong frn, out ulong[] parents)
                => TryGetLinkState(frn, out parents, out _);

            public bool TryGetLinkState(ulong frn, out ulong[] parents, out ulong size)
            {
                parents = null;
                size = 0;
                var entry = frn & EntryMask;
                var table = pageTable;
                if (table.LinkOverrides.TryGetValue(entry, out var changed))
                {
                    if (changed.Frn != frn || changed.Parents.Length == 0) return false;
                    parents = changed.Parents;
                    size = changed.Size;
                    return true;
                }
                if (table.Source?.TryGetLinkParents(frn, out var scanned) == true
                    && table.Source.TryGetByFrn(frn, out var scannedNode))
                {
                    parents = scanned.ToArray();
                    size = scannedNode.Size;
                    return true;
                }
                if (!TryGetValue(frn, out var node) || node.PathParent?.Frn is not { } parent
                    || parent == 0)
                    return false;
                parents = new[] { parent };
                size = node.Size;
                return true;
            }

            public void SetLinkState(ulong frn, ulong[] parents, ulong size)
            {
                if (parents == null) throw new ArgumentNullException(nameof(parents));
                lock (mutationLock)
                {
                    var entry = frn & EntryMask;
                    var version = ++mutationVersion;
                    pageTable.LinkOverrides[entry] =
                        new LinkOverride(frn, parents, size, version);
                }
            }

            /// <summary>
            /// (Re)fill from a drive scan in one pass. Only pages containing live records
            /// are allocated, bounding memory independently of the highest record number.
            /// </summary>
            public void Populate(IEnumerable<INode> nodes,
                long preserveMutationsAfter = long.MaxValue)
            {
                lock (mutationLock)
                {
                    var old = pageTable;
                    PageTable populated;
                    if (nodes is IFrnNodeSource source)
                    {
                        //The source owns both the record table and dense enumeration; retaining
                        //it replaces the old 16-byte-per-slot Frns[] + Nodes[] page pair.
                        populated = new PageTable(source, new Dictionary<ulong, Page>(),
                            new(), new(), new());
                    }
                    else
                    {
                        var pages = new Dictionary<ulong, Page>();
                        var sparse = new NonBlocking.ConcurrentDictionary<ulong, INode>();
                        //At most ~64 MiB of dense pages. The count-derived limit requires a
                        //page to average at least 25% occupancy; excess sparse ranges retain
                        //dictionary storage instead of amplifying one record into a 64 KiB page.
                        var pageLimit = 1024;
                        if (nodes.TryGetNonEnumeratedCount(out var nodeCount))
                        {
                            var densePages = Math.Max(1L, ((long)nodeCount + PageSize - 1) / PageSize);
                            pageLimit = (int)Math.Min(1024, densePages * 4);
                        }
                        foreach (var n in nodes)
                        {
                            var frn = n.Frn;
                            if (frn == 0) continue;
                            var entry = frn & EntryMask;
                            var pageIndex = entry >> PageBits;
                            if (!pages.TryGetValue(pageIndex, out var page))
                            {
                                if (pages.Count >= pageLimit)
                                {
                                    sparse[frn] = n;
                                    continue;
                                }
                                pages.Add(pageIndex, page = new Page());
                            }
                            var slot = (int)(entry & PageMask);
                            page.Nodes[slot] = n;
                            page.Frns[slot] = frn;
                        }
                        populated = pages.Count == 0 && sparse.IsEmpty
                            ? PageTable.Empty() : new PageTable(null, pages, sparse,
                                new(), new());
                    }

                    if (preserveMutationsAfter != long.MaxValue)
                    {
                        foreach (var pair in old.Overrides)
                            if (pair.Value.Version > preserveMutationsAfter)
                                populated.Overrides[pair.Key] = pair.Value;
                        foreach (var pair in old.LinkOverrides)
                            if (pair.Value.Version > preserveMutationsAfter)
                                populated.LinkOverrides[pair.Key] = pair.Value;
                    }
                    //Set/Remove wait on mutationLock and therefore apply after this fresh
                    //table is visible. Earlier changes newer than the scan watermark were
                    //copied above; changes arriving now will apply to the new table.
                    pageTable = populated;
                }
            }
        }

        /// <summary>FRN-map placeholder carrying only a path - never indexed, never trusted unverified</summary>
        sealed class PathNode : INode
        {
            readonly string path;
            public PathNode(string path) => this.path = path;
            public override string FullName => path;
            public override string Name => Path.GetFileName(path);
            public override bool Exists => false; //Must never enter the index - it carries no metadata
            public override FileAttributes Attributes { get => 0; protected set { } }
            public override ulong Size { get => 0; protected set { } }
            public override DateTime LastChangeTime { get => default; protected set { } }
        }

        /// <summary>
        /// Directories the watcher must diff against the disk because it could not account
        /// for their content from the records alone. Most records name only a parent FRN,
        /// which stays unresolved until the reconcile actually runs - deduplicating first
        /// costs one OpenFileById per distinct directory instead of one per record in a
        /// storm. A caller that already holds the directory path (a rename whose old name
        /// is known but whose record's ParentFrn points at the move's destination) adds it
        /// directly; the two sets deduplicate against each other in the model's diff.
        /// </summary>
        sealed class UnresolvedParents
        {
            readonly HashSet<ulong> frns = new();
            readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

            public int Count => frns.Count + paths.Count;

            public void Add(ulong parentFrn) => frns.Add(parentFrn);

            public void Add(string directory)
            {
                if (!string.IsNullOrEmpty(directory)) paths.Add(directory);
            }

            /// <summary>Resolve and take everything collected so far, leaving this empty</summary>
            public string[] Drain(Func<ulong, string> resolve)
            {
                var dirs = frns.Select(resolve).Where(p => p != null).Concat(paths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                frns.Clear();
                paths.Clear();
                return dirs;
            }
        }

        /// <summary>
        /// Records whose FRN the map does not know (walked drive, staled entry) name at
        /// least their parent - resolve it and let the model diff that directory against
        /// the disk. Batched: one reconcile pass covers a whole storm's worth of misses.
        /// </summary>
        void Reconcile(UnresolvedParents unresolvedParents)
        {
            if (unresolvedParents.Count == 0) return;
            var dirs = unresolvedParents.Drain(journal.TryResolvePath);
            if (dirs.Length == 0) return;
            try { FSChangeProcessor.ReconcileDirs(dirs).Wait(); } catch { }
        }

        public void Dispose()
        {
            stop = true;
            lock (exactRescanLock)
            {
                exactRescanTimer?.Dispose();
                exactRescanTimer = null;
            }
            journal.Dispose(); //The blocked read wakes within its finite timeout
        }
    }
}
