//! Unique: the first occurrence of every row, in the original order or sorted.

use super::trim_span;
use crate::cancel::{Cancel, OpResult};
use crate::collate::compare_natural;
use crate::operator::{UniqueOp, UniqueOrder};
use crate::stage::{OutRows, Stage};
use crate::text::{as_str, Span};
use hashbrown::HashTable;
use rayon::prelude::*;
use std::hash::{BuildHasher, Hasher};

/// Hash of the text, of its lowercase when case does not count
fn hash_of(builder: &hashbrown::DefaultHashBuilder, text: &[u8], case_sensitive: bool) -> u64 {
    if case_sensitive {
        return builder.hash_one(text);
    }
    let mut hasher = builder.build_hasher();
    if text.is_ascii() {
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

fn same(left: &[u8], right: &[u8], case_sensitive: bool) -> bool {
    if case_sensitive {
        left == right
    } else if left.is_ascii() && right.is_ascii() {
        left.eq_ignore_ascii_case(right)
    } else {
        as_str(left).to_lowercase() == as_str(right).to_lowercase()
    }
}

pub fn apply(stage: &Stage, op: &UniqueOp, cancel: &Cancel) -> OpResult<Stage> {
    let builder = hashbrown::DefaultHashBuilder::default();
    // Room for every row up front: growing would hash the text of all entries again
    let mut seen: HashTable<u32> = HashTable::with_capacity(stage.len());
    // (value, row) of the first occurrences
    let mut entries: Vec<(Span, u32)> = Vec::new();
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
        let hash = hash_of(&builder, text, op.case_sensitive);
        let found = seen.find(hash, |&entry| same(stage.span_bytes(entries[entry as usize].0), text, op.case_sensitive)).is_some();
        if !found {
            seen.insert_unique(hash, entries.len() as u32, |&entry| hash_of(&builder, stage.span_bytes(entries[entry as usize].0), op.case_sensitive));
            entries.push((span, row as u32));
        }
        Ok(())
    })?;
    drop(seen);
    if op.order != UniqueOrder::Stable {
        cancel.check()?;
        let descending = op.order == UniqueOrder::Desc;
        entries.par_sort_by(|left, right| {
            let order = compare_natural(as_str(stage.span_bytes(left.0)), as_str(stage.span_bytes(right.0)), op.case_sensitive);
            (if descending { order.reverse() } else { order }).then(left.1.cmp(&right.1))
        });
    }
    if !op.trim {
        let origin: Vec<u32> = entries.into_iter().map(|(_, row)| row).collect();
        let preserves_max = op.case_sensitive || stage.bufs.iter().all(|buf| buf.ascii);
        return Ok(stage.derive_rows("Unique", origin, preserves_max.then_some(stage.max_cols)));
    }
    let (spans, origin) = entries.into_iter().unzip();
    Ok(stage.derive("Unique", OutRows { spans, origin: Some(origin), ..Default::default() }))
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
}
