//! The engine of Log Explorer: loading sources into shared UTF-8 buffers and the staged
//! Filter / Replace / Sort / Diff / Extract / Unique / Join pipeline over them.
//!
//! The text of a source is kept once. A stage - the view after some operations - is a list
//! of 12-byte spans into shared buffers, so a pipeline over a file of gigabytes costs a few
//! bytes a row for each stage, not another copy of the text.

pub mod cancel;
pub mod collate;
pub mod find;
pub mod i18n;
pub mod literal;
pub mod load;
pub mod operator;
pub mod ops;
pub mod pipeline;
pub mod profiles;
pub mod select;
pub mod selection;
pub mod stage;
pub mod text;
pub mod typed;

pub use cancel::{Cancel, OpError, OpResult};
pub use operator::{OpKind, OpType, Operator};
pub use selection::{Point, Selection};
pub use stage::Stage;
