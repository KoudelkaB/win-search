# NTFS loading and live updates review — 2026-09-05

Baseline: `58f968f5781c17bdba12cb391b97eb26bf165a10`.

## Changes

- **MFT attributes:** read file flags from `$STANDARD_INFORMATION`, with `$FILE_NAME` as a fallback. The filename copy could restore stale flags after F12. The record header continues to determine whether an entry is a directory.
- **MFT parent validation:** reject a file as the parent of another MFT entry. Inconsistent records must not create searchable paths below ordinary files.
- **Mixed snapshots:** preserved live nodes can reference ancestors from the previous scan. Subtree filtering and removal now recognize their paths, and aggregate updates resolve the currently published directories before changing sizes and counts.
- **Directory renames:** the USN resolver rebases scanned descendants through the renamed parent's file reference, even before the ordered model queue applies the rename. Path-backed live overrides are rebased explicitly. Renamed descendants retain their NTFS identities. This avoids a disk lookup for a child that has already been deleted at its new path. The normal resolver path remains unchanged until a directory has been remapped.
- **Event coalescing:** adjacent creates or deletes with distinct nonzero file references are separate operations, even when they name the same path.
- **Long paths:** resize the native path buffer when Windows reports that the initial 1,024 characters are insufficient.
- **USN parsing and recovery:** validate filename bounds against the individual record; discard pending rename/ghost bookkeeping when journal history is invalidated.
- **Metadata memory use:** updates of existing base nodes do not create persistent overlay entries outside a pending scan. Cancelled scans release metadata-only overlays and transient tombstones. Mutations during a scan still survive publication.

## Verification

The original suite passed 334 tests. The expanded suite passes 345 tests, including real NTFS journal tests, in Debug and Release. New tests cover mixed snapshot generations, aggregate destinations, scanned and live descendants after a rename, preservation across scan publication, exact deletion of a child under a renamed directory, long paths, reused path identities, stale MFT flags, invalid MFT parents, and USN record bounds.

Commands:

```powershell
dotnet test search.tests/search.tests.csproj --no-restore --verbosity minimal
dotnet test search.tests/search.tests.csproj -c Release --no-restore --verbosity minimal
```

## Focused performance experiment

An isolated Release console harness compiled the baseline and modified `DriveNodeIndex`, `NodePath`, and `INode` sources with the same .NET 10 runtime and NonBlocking dependency. Tiered compilation was disabled for both. Each measurement followed a warm-up and full collection. The dataset contained 500,000 file nodes under 1,000 directories; 20,000 files received metadata updates. Directory filtering used the cached criterion terminal now used by `NodeFilter`.

| Operation | Baseline | Modified | Allocated bytes, baseline → modified |
| --- | ---: | ---: | ---: |
| 20,000 metadata touches | 3.14 ms | 3.11 ms | 803,672 → 3,672 |
| 10 snapshots plus name filters | 259.22 ms | 78.32 ms | 58,812,416 → 656 |
| 10 directory filters | 53.34 ms | 50.13 ms | 336 → 336 |

Both versions produced identical match counts. These are single local measurements of a metadata-only workload, not whole-application or F12 timings. Structural overlays still require merging a snapshot; no overall startup speedup is claimed. The exploratory harness is retained locally under ignored `obj/ntfs-review-benchmark/`.

## Limits

These changes repair specific causes of index drift without adding full-drive reloads. They do not prove that every possible concurrent filesystem operation is lossless. Full scans remain necessary when journal history actually disappears, a watcher overflows, or the data source fails. Microsoft explicitly describes re-indexing after the journal deletes records that an indexer still needs: [Change Journal Records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records).

Existing hard-link repair failures caused by inaccessible names or unavailable topology can still leave directory aggregates stale; the existing diagnostics report this instead of triggering repeated full-drive reloads. No claim is made that this review eliminates those provider limitations or every automatic scan.

Native path-buffer handling follows [GetFinalPathNameByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew). The standard-information flags occupy the field following the four timestamps; see the [NTFS structure definition in ReactOS](https://doxygen.reactos.org/d1/da8/drivers_2filesystems_2ntfs_2ntfs_8h_source.html#l00328).
