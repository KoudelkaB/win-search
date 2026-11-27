//! Unique: the first occurrence of every row, in the original order or sorted.

use super::{par_chunks, trim_span};
use crate::cancel::{Cancel, OpResult};
use crate::collate::compare_natural;
use crate::operator::{UniqueOp, UniqueOrder};
use crate::stage::{OutRows, Stage};
use crate::text::{as_str, char_len, Span};
use hashbrown::HashTable;
use rayon::prelude::*;
use std::hash::{BuildHasher, Hasher};

const PARALLEL_THRESHOLD: usize = 16_384;
const PARTITIONS: usize = 128;
const PART_SHIFT: u32 = 57; // 64 - log2(128)
const LOCAL_FILTER_SIZE: usize = 2048;

/// Hash of the text, of its lowercase when case does not count
#[inline]
fn hash_of(builder: &hashbrown::DefaultHashBuilder, text: &[u8], case_sensitive: bool, known_ascii: bool) -> u64 {
    if case_sensitive {
        return builder.hash_one(text);
    }
    let mut hasher = builder.build_hasher();
    if known_ascii || text.is_ascii() {
        // Lowercased block by block: no copy of the row is allocated
        let mut block = [0u8; 64];
        for chunk in text.chunks(64) {
            block[..chunk.len()].copy_from_slice(chunk);
            block[..chunk.len()].make_ascii_lowercase();
            hasher.write(&block[..chunk.len()]);
        }
    } else {
        for chunk in as_str(text).to_lowercase().as_bytes().chunks(64) {
            hasher.write(chunk);
        }
    }
    hasher.write_usize(usize::MAX);
    hasher.finish()
}

#[inline]
fn same(left: &[u8], right: &[u8], case_sensitive: bool, known_ascii: bool) -> bool {
    if case_sensitive {
        left == right
    } else if known_ascii || (left.is_ascii() && right.is_ascii()) {
        left.eq_ignore_ascii_case(right)
    } else {
        as_str(left).to_lowercase() == as_str(right).to_lowercase()
    }
}

fn collect_serial(stage: &Stage, op: &UniqueOp, all_ascii: bool, cancel: &Cancel) -> OpResult<Vec<(Span, u32)>> {
    let builder = hashbrown::DefaultHashBuilder::default();
    let mut seen: HashTable<u32> = HashTable::with_capacity(stage.len().min(4096));
    let mut entries: Vec<(Span, u32)> = Vec::new();
    let mut hashes: Vec<u64> = Vec::new();
    stage.try_for_each_row(0..stage.len(), |row, raw_span, raw_line| {
        if row % 8192 == 0 {
            cancel.check()?;
        }
        let (span, text) = if op.trim {
            let trimmed = trim_span(stage, raw_span);
            (trimmed, stage.span_bytes(trimmed))
        } else {
            (raw_span, raw_line)
        };
        if op.ignore_empty && span.is_empty() {
            return Ok(());
        }
        let hash = hash_of(&builder, text, op.case_sensitive, all_ascii);
        let found = seen.find(hash, |&entry| same(stage.span_bytes(entries[entry as usize].0), text, op.case_sensitive, all_ascii)).is_some();
        if !found {
            seen.insert_unique(hash, entries.len() as u32, |&entry| hashes[entry as usize]);
            entries.push((span, row as u32));
            hashes.push(hash);
        }
        Ok(())
    })?;
    Ok(entries)
}

fn collect_parallel(stage: &Stage, op: &UniqueOp, all_ascii: bool, cancel: &Cancel) -> OpResult<Vec<(Span, u32)>> {
    let builder = hashbrown::DefaultHashBuilder::default();
    // Phase 1: hash rows in parallel and scatter into hash partitions (with a tiny L1 duplicate filter).
    let chunk_buckets: Vec<Vec<Vec<(Span, u32, u64)>>> = par_chunks(stage.len(), cancel, |range| {
        let mut buckets: Vec<Vec<(Span, u32, u64)>> = (0..PARTITIONS).map(|_| Vec::new()).collect();
        let mut recent: Vec<(u64, Span)> = vec![(0, Span::EMPTY); LOCAL_FILTER_SIZE];
        let mut occupied: Vec<bool> = vec![false; LOCAL_FILTER_SIZE];
        stage.for_each_row(range, |row, raw_span, raw_line| {
            let (span, text) = if op.trim {
                let trimmed = trim_span(stage, raw_span);
                (trimmed, stage.span_bytes(trimmed))
            } else {
                (raw_span, raw_line)
            };
            if op.ignore_empty && span.is_empty() {
                return;
            }
            let hash = hash_of(&builder, text, op.case_sensitive, all_ascii);
            let slot = (hash as usize) & (LOCAL_FILTER_SIZE - 1);
            if occupied[slot] && recent[slot].0 == hash && same(stage.span_bytes(recent[slot].1), text, op.case_sensitive, all_ascii) {
                return;
            }
            occupied[slot] = true;
            recent[slot] = (hash, span);
            let part = (hash >> PART_SHIFT) as usize;
            buckets[part].push((span, row as u32, hash));
        });
        Ok(buckets)
    })?;

    cancel.check()?;
    // Phase 2: deduplicate each partition independently in chunk order (preserving global first occurrence).
    let per_part: OpResult<Vec<Vec<(Span, u32)>>> = (0..PARTITIONS)
        .into_par_iter()
        .map(|part| {
            cancel.check()?;
            let mut seen: HashTable<u32> = HashTable::new();
            let mut entries: Vec<(Span, u32)> = Vec::new();
            let mut hashes: Vec<u64> = Vec::new();
            for chunk in &chunk_buckets {
                for &(span, row, hash) in &chunk[part] {
                    let text = stage.span_bytes(span);
                    let found =
                        seen.find(hash, |&entry| hashes[entry as usize] == hash && same(stage.span_bytes(entries[entry as usize].0), text, op.case_sensitive, all_ascii)).is_some();
                    if !found {
                        seen.insert_unique(hash, entries.len() as u32, |&entry| hashes[entry as usize]);
                        entries.push((span, row));
                        hashes.push(hash);
                    }
                }
            }
            Ok(entries)
        })
        .collect();
    drop(chunk_buckets);
    let per_part = per_part?;
    let total: usize = per_part.iter().map(Vec::len).sum();
    let mut entries = Vec::with_capacity(total);
    for part in per_part {
        entries.extend_from_slice(&part);
    }
    if op.order == UniqueOrder::Stable {
        cancel.check()?;
        entries.par_sort_unstable_by_key(|&(_, row)| row);
    }
    Ok(entries)
}

pub fn apply(stage: &Stage, op: &UniqueOp, cancel: &Cancel) -> OpResult<Stage> {
    let all_ascii = stage.bufs.iter().all(|buf| buf.ascii);
    let mut entries = if stage.len() < PARALLEL_THRESHOLD { collect_serial(stage, op, all_ascii, cancel)? } else { collect_parallel(stage, op, all_ascii, cancel)? };
    if op.order != UniqueOrder::Stable {
        cancel.check()?;
        let descending = op.order == UniqueOrder::Desc;
        entries.par_sort_unstable_by(|left, right| {
            let order = compare_natural(as_str(stage.span_bytes(left.0)), as_str(stage.span_bytes(right.0)), op.case_sensitive);
            (if descending { order.reverse() } else { order }).then(left.1.cmp(&right.1))
        });
    }
    if !op.trim {
        let origin: Vec<u32> = entries.into_iter().map(|(_, row)| row).collect();
        let preserves_max = op.case_sensitive || all_ascii;
        return Ok(stage.derive_rows("Unique", origin, preserves_max.then_some(stage.max_cols)));
    }
    let mut spans = Vec::with_capacity(entries.len());
    let mut origin = Vec::with_capacity(entries.len());
    let mut max_cols = 0u64;
    for (span, row) in entries {
        let cols = if span.is_empty() {
            0
        } else if all_ascii || stage.bufs[span.buf() as usize].ascii {
            span.len() as u64
        } else {
            char_len(stage.span_bytes(span)) as u64
        };
        max_cols = max_cols.max(cols);
        spans.push(span);
        origin.push(row);
    }
    Ok(stage.derive("Unique", OutRows { spans, origin: Some(origin), arena: Vec::new(), max_cols: Some(max_cols), arena_resolved: true, arena_ascii: None }))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    fn unique(order: UniqueOrder) -> UniqueOp {
        UniqueOp { order, trim: true, ignore_empty: true, case_sensitive: true }
    }

    #[test]
    fn the_first_occurrence_stays_in_place() {
        let stage = stage_of(&["b", " a ", "b", "", "a", "B"]);
        let out = apply(&stage, &unique(UniqueOrder::Stable), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["b", "a", "B"]);
        assert_eq!(row_ids_of(&out), [0, 1, 5]);
        let loose = UniqueOp { case_sensitive: false, trim: false, ignore_empty: false, ..unique(UniqueOrder::Stable) };
        assert_eq!(lines_of(&apply(&stage, &loose, &Cancel::new()).unwrap()), ["b", " a ", "", "a"]);
    }

    #[test]
    fn case_can_be_ignored_beyond_ascii_too() {
        let stage = stage_of(&["Žluť", "žLUŤ", "žluť!"]);
        let loose = UniqueOp { case_sensitive: false, ..unique(UniqueOrder::Stable) };
        assert_eq!(lines_of(&apply(&stage, &loose, &Cancel::new()).unwrap()), ["Žluť", "žluť!"]);
    }

    #[test]
    fn the_result_can_be_sorted_with_numbers_by_value() {
        let stage = stage_of(&["id10", "id9", "id10", "id100"]);
        assert_eq!(lines_of(&apply(&stage, &unique(UniqueOrder::Asc), &Cancel::new()).unwrap()), ["id9", "id10", "id100"]);
        assert_eq!(lines_of(&apply(&stage, &unique(UniqueOrder::Desc), &Cancel::new()).unwrap()), ["id100", "id10", "id9"]);
    }

    #[test]
    fn parallel_unique_matches_serial_and_preserves_first_occurrence() {
        let mut lines = Vec::with_capacity(25_000);
        for index in 0..25_000usize {
            if index % 7 == 0 {
                lines.push(format!("  item-{}  ", index % 500));
            } else if index % 11 == 0 {
                lines.push(format!("ITEM-{}", index % 500));
            } else {
                lines.push(format!("unique-{index}"));
            }
        }
        let stage = crate::load::stage_from_text(lines.join("\n"), "big", "big");
        for (trim, case_sensitive) in [(false, true), (true, false)] {
            let op = UniqueOp { order: UniqueOrder::Stable, trim, ignore_empty: true, case_sensitive };
            let serial = collect_serial(&stage, &op, true, &Cancel::new()).unwrap();
            let parallel = collect_parallel(&stage, &op, true, &Cancel::new()).unwrap();
            assert_eq!(parallel, serial, "trim={trim} cs={case_sensitive}");
            let out = apply(&stage, &op, &Cancel::new()).unwrap();
            assert_eq!(out.len(), serial.len());
            if !trim {
                assert!(matches!(out.rows, crate::stage::Rows::Indexed { .. }));
            }
        }
    }
}
