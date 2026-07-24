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
        readonly Action<DriveScanReason> rescan; //Journal ambiguity => rescan this drive
        readonly Action<UsnDriveWatcher> dead;  //Journal unreadable for good => switch the drive to a watcher
        readonly FrnMap frnMap = new();
        readonly object exactRescanLock = new();
        Timer exactRescanTimer;
        volatile bool stop;
        volatile bool hasBaselineMap;

        //A hard-link storm can emit thousands of records. One exact rebuild after a quiet
        //window is both cheaper and more accurate than trying to rescan for every record.
        internal const int ExactRescanQuietMs = 1000;

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
        public bool ReportsCompleteDirectoryDeletes => !IsDead && hasBaselineMap;

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
        public void Populate(IEnumerable<INode> nodes)
        {
            frnMap.Populate(nodes);
            //Only the MFT reader supplies a complete FRN-addressable baseline. A walked
            //fallback collection contains path nodes without file references, so deleted
            //records may still need conservative parent reconciliation.
            hasBaselineMap = nodes is IFrnNodeSource;
        }

        void Loop()
        {
            //Half-delivered renames within one batch: FRN whose RENAME_OLD_NAME was seen,
            //with the old path (null = unknown), waiting for its RENAME_NEW_NAME
            var pendingRenames = new Dictionary<ulong, string>();
            //Parents of records that could not be resolved - reconciled against the disk
            var unresolvedParents = new HashSet<ulong>();
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
                    hasBaselineMap = false;
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
                        Translate(r, pendingRenames, unresolvedParents, changedSeen, createdSeen, ghosts);
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
            HashSet<ulong> unresolvedParents, HashSet<ulong> changedSeen,
            HashSet<ulong> createdSeen, HashSet<ulong> ghosts)
        {
            var hadMultipleLinks = frnMap.HasMultipleLinks(r.Frn);
            var repairedHardLinks = false;
            var hardLinkFileGone = false;
            var liveLinkCount = 0;
            if (CanRepairHardLinkIncrementally(r.Reason, hadMultipleLinks))
            {
                repairedHardLinks = TryQueueHardLinkUpdate(r.Frn,
                    out liveLinkCount, out hardLinkFileGone);
                if (!repairedHardLinks && !hardLinkFileGone)
                    RequestExactMftRescan();
            }
            if (!repairedHardLinks && !hardLinkFileGone
                && RequiresExactMftRescan(r.Reason, hadMultipleLinks))
                RequestExactMftRescan();

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
            var changed = MapPath(r.Frn);
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

        bool TryQueueHardLinkUpdate(ulong frn, out int liveLinkCount,
            out bool fileReferenceGone)
        {
            liveLinkCount = 0;
            fileReferenceGone = false;
            if (!frnMap.TryGetValue(frn, out var mapped) || mapped.IsDirectory)
            {
                $"USN targeted hard-link repair on {journal.Root} failed: FRN {frn:x} is not mapped".Debug();
                return false;
            }
            var path = mapped.FullName;
            var indexed = FSChangeProcessor.Lookup(path);
            if (indexed == null || indexed.IsDirectory)
            {
                $"USN targeted hard-link repair on {journal.Root} failed: canonical path is not indexed ({path})".Debug();
                return false;
            }
            if (!frnMap.TryGetLinkState(frn, out var oldParentRefs, out var oldSize))
            {
                $"USN targeted hard-link repair on {journal.Root} failed: no baseline parents for {frn:x}".Debug();
                return false;
            }
            if (!journal.TryGetHardLinkPaths(frn, out var currentPaths,
                    out fileReferenceGone)
                || currentPaths.Length == 0)
            {
                if (fileReferenceGone)
                    $"USN hard-link target on {journal.Root} vanished before enumeration: frn={frn:x}".Debug();
                else
                    $"USN targeted hard-link repair on {journal.Root} failed: link-name enumeration for {frn:x}".Debug();
                return false;
            }
            if (!INode.TryReadMetadata(path, out var snapshot) || snapshot.IsDirectory)
            {
                $"USN targeted hard-link repair on {journal.Root} failed: canonical metadata ({path})".Debug();
                return false;
            }

            var oldParentPaths = new string[oldParentRefs.Length];
            for (var i = 0; i < oldParentRefs.Length; i++)
            {
                if (!TryResolveLinkParent(oldParentRefs[i], out var parent))
                {
                    $"USN targeted hard-link repair on {journal.Root} failed: old parent {oldParentRefs[i]:x}".Debug();
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
                    $"USN targeted hard-link repair on {journal.Root} failed: current parent {parentPath}".Debug();
                    return false;
                }
                currentParentRefs[i] = parent.Frn;
                currentParentPaths[i] = parent.FullName;
            }

            if (!TryCalculateHardLinkParentDeltas(oldParentPaths, currentParentPaths,
                    oldParentRefs.Length == 1 ? indexed.Size : oldSize,
                    snapshot.Size, out var deltas))
            {
                $"USN targeted hard-link repair on {journal.Root} failed: aggregate delta overflow".Debug();
                return false;
            }

            liveLinkCount = currentParentRefs.Length;
            //Publish the queried state before enqueueing. Several records for one open
            //handle can share a USN batch while the serialized model queue is still
            //waiting; the following record must diff from this state, not add the same
            //size delta again from the not-yet-updated index node.
            frnMap.SetLinkState(frn, currentParentRefs, snapshot.Size);
            Process(FsEvent.HardLinkUpdate(path, frn, indexed, snapshot, deltas));
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

        void RequestExactMftRescan()
        {
            lock (exactRescanLock)
            {
                if (stop) return;
                exactRescanTimer ??= new Timer(_ =>
                {
                    if (!stop) try
                    {
                        $"USN hard-link changes on {journal.Root} => exact MFT size rebuild".Debug();
                        rescan(DriveScanReason.UsnHardLinkChange);
                    }
                    catch { }
                }, null, Timeout.Infinite, Timeout.Infinite);
                exactRescanTimer.Change(ExactRescanQuietMs, Timeout.Infinite);
            }
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
        void Process(FsEvent e)
        {
            try { lastEnqueued = process(e); } catch { }
        }

        /// <summary>
        /// Path of the file as the index knows it, through the FRN map. Verified against
        /// the live index: an entry staled by a parent-directory rename (its subtree was
        /// re-indexed with new instances under new paths) is healed or dropped, never
        /// trusted - a Remove on a wrong path would leave ghosts.
        /// heal=false skips the live-disk repair - a RENAME record's OLD path must never
        /// resolve from the disk, where the file already sits under its NEW path (the
        /// rename would compare equal and be swallowed).
        /// </summary>
        string MapPath(ulong frn, bool heal = true)
        {
            if (!frnMap.TryGetValue(frn, out var node)) return null;
            var path = node.FullName;
            if (FSChangeProcessor.Lookup(path) != null) return path; //Still indexed under that path
            if (!heal) return null;
            var live = journal.TryResolvePath(frn);
            if (live == null)
            {
                frnMap.Remove(frn);
                return null;
            }
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
                public SourceOverride(ulong frn, INode node) { Frn = frn; Node = node; }
            }

            sealed class LinkOverride
            {
                public readonly ulong Frn;
                public readonly ulong[] Parents;
                public readonly ulong Size;
                public LinkOverride(ulong frn, ulong[] parents, ulong size)
                {
                    Frn = frn;
                    Parents = parents;
                    Size = size;
                }
            }

            readonly object mutationLock = new();
            volatile PageTable pageTable = PageTable.Empty();

            public bool TryGetValue(ulong frn, out INode node)
            {
                var entry = frn & EntryMask;
                var table = pageTable;
                if (table.Source != null)
                {
                    if (!table.Overrides.IsEmpty && table.Overrides.TryGetValue(entry, out var changed))
                    {
                        node = changed.Frn == frn ? changed.Node : null;
                        return node != null;
                    }
                    return table.Source.TryGetByFrn(frn, out node);
                }
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
                        pageTable.Overrides[entry] = new SourceOverride(frn, node);
                        return;
                    }
                    if (!pageTable.Pages.TryGetValue(entry >> PageBits, out var page))
                    {
                        pageTable.Sparse[frn] = node;
                        return;
                    }
                    var slot = (int)(entry & PageMask);
                    page.Nodes[slot] = node;
                    page.Frns[slot] = frn;
                    pageTable.Sparse.TryRemove(frn, out _);
                }
            }

            public void Remove(ulong frn)
            {
                lock (mutationLock)
                {
                    var entry = frn & EntryMask;
                    if (pageTable.Source != null)
                    {
                        //A stale delete must not hide a newer owner of the same MFT slot.
                        if (TryGetValue(frn, out _))
                        {
                            pageTable.Overrides[entry] = new SourceOverride(frn, null);
                            pageTable.LinkOverrides[entry] =
                                new LinkOverride(frn, Array.Empty<ulong>(), 0);
                        }
                        return;
                    }
                    if (pageTable.Pages.TryGetValue(entry >> PageBits, out var page))
                    {
                        var slot = (int)(entry & PageMask);
                        if (page.Frns[slot] == frn) //Another sequence may own the slot by now
                        {
                            page.Frns[slot] = 0;
                            page.Nodes[slot] = null;
                        }
                    }
                    pageTable.Sparse.TryRemove(frn, out _);
                    if (pageTable.LinkOverrides.TryGetValue(entry, out var links)
                        && links.Frn == frn)
                        pageTable.LinkOverrides.TryRemove(entry, out _);
                }
            }

            public void Clear()
            {
                lock (mutationLock)
                {
                    pageTable = PageTable.Empty();
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
                    pageTable.LinkOverrides[entry] =
                        new LinkOverride(frn, parents, size);
                }
            }

            /// <summary>
            /// (Re)fill from a drive scan in one pass. Only pages containing live records
            /// are allocated, bounding memory independently of the highest record number.
            /// </summary>
            public void Populate(IEnumerable<INode> nodes)
            {
                lock (mutationLock)
                {
                    if (nodes is IFrnNodeSource source)
                    {
                        //The source owns both the record table and dense enumeration; retaining
                        //it replaces the old 16-byte-per-slot Frns[] + Nodes[] page pair.
                        pageTable = new PageTable(source, new Dictionary<ulong, Page>(),
                            new(), new(), new());
                        return;
                    }
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
                    //Set/Remove wait on mutationLock and therefore apply after this fresh
                    //table is visible. No event delta from the population window is lost.
                    pageTable = pages.Count == 0 && sparse.IsEmpty
                        ? PageTable.Empty() : new PageTable(null, pages, sparse, new(), new());
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
            lock (exactRescanLock)
            {
                exactRescanTimer?.Dispose();
                exactRescanTimer = null;
            }
            journal.Dispose(); //The blocked read wakes within its finite timeout
        }
    }
}
