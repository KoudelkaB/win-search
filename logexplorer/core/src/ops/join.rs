//! Join: all rows as one value with a delimiter, prefix and suffix.

use super::{merge_chunks, trim_span, Assembler};
use crate::cancel::{Cancel, OpResult};
use crate::load::TAB_REPLACEMENT;
use crate::operator::JoinOp;
use crate::stage::{Stage, NO_ROW};

/// `\n`, `\t` and `\\` of the text typed into a field
pub fn decode_escapes(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    let mut chars = value.chars().peekable();
    while let Some(ch) = chars.next() {
        if ch == '\\' {
            match chars.peek() {
                Some('n') => out.push('\n'),
                Some('t') => out.push_str(TAB_REPLACEMENT),
                Some('\\') => out.push('\\'),
                _ => {
                    out.push(ch);
                    continue;
                }
            }
            chars.next();
        } else {
            out.push(ch);
        }
    }
    out
}

pub fn apply(stage: &Stage, op: &JoinOp, cancel: &Cancel) -> OpResult<Stage> {
    let decode = |value: &str| if op.interpret_escapes { decode_escapes(value) } else { value.to_string() };
    let (delimiter, prefix, suffix) = (decode(&op.delimiter), decode(&op.prefix), decode(&op.suffix));
    let mut assembler = Assembler::with_origin(stage, true);
    let (mut first, mut last) = (NO_ROW, NO_ROW);
    let mut joined = 0usize;
    stage.try_for_each_row(0..stage.len(), |row, raw_span, _| {
        if row % 8192 == 0 {
            cancel.check()?;
        }
        let span = if op.trim { trim_span(stage, raw_span) } else { raw_span };
        if op.skip_empty && span.is_empty() {
            return Ok(());
        }
        if joined == 0 {
            first = row as u32;
            assembler.push_text(prefix.as_bytes(), first);
        } else {
            assembler.push_text(delimiter.as_bytes(), row as u32);
        }
        assembler.push_span(span, row as u32);
        last = row as u32;
        joined += 1;
        Ok(())
    })?;
    if joined == 0 {
        assembler.push_text(prefix.as_bytes(), first);
    }
    assembler.push_text(suffix.as_bytes(), last);
    assembler.end_row(last);
    Ok(stage.derive("Join", merge_chunks(vec![assembler.finish()?], true, stage.bufs.len() as u16)?))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::stage::testing::*;

    fn join(delimiter: &str, prefix: &str, suffix: &str) -> JoinOp {
        JoinOp { delimiter: delimiter.into(), prefix: prefix.into(), suffix: suffix.into(), trim: false, skip_empty: true, interpret_escapes: true }
    }

    #[test]
    fn rows_become_one_delimited_value() {
        let stage = stage_of(&["id1", "", "id2", "id3"]);
        let out = apply(&stage, &join("|", "(", ")"), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["(id1|id2|id3)"]);
        assert_eq!(row_ids_of(&out), [0]);
    }

    #[test]
    fn escapes_can_put_every_value_on_its_own_row_without_copying_it() {
        let stage = stage_of(&["a", "b"]);
        let out = apply(&stage, &join("\\n", "", ""), &Cancel::new()).unwrap();
        assert_eq!(lines_of(&out), ["a", "b"]);
        assert_eq!(out.bufs.len(), stage.bufs.len());
        let literal = JoinOp { interpret_escapes: false, ..join("\\n", "", "") };
        assert_eq!(lines_of(&apply(&stage, &literal, &Cancel::new()).unwrap()), ["a\\nb"]);
        assert_eq!(decode_escapes("a\\tb\\\\n\\x"), "a    b\\n\\x");
    }

    #[test]
    fn nothing_to_join_gives_one_empty_row() {
        let stage = stage_of(&["", "  "]);
        let trimmed = JoinOp { trim: true, ..join("|", "", "") };
        assert_eq!(lines_of(&apply(&stage, &trimmed, &Cancel::new()).unwrap()), [""]);
        assert_eq!(lines_of(&apply(&stage, &JoinOp { trim: true, ..join("|", "[", "]") }, &Cancel::new()).unwrap()), ["[]"]);
    }
}
