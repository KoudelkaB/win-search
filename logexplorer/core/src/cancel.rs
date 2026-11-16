//! Cancelling a running computation and the error of an operation.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

/// A flag a long computation polls; set when its result is no longer wanted.
#[derive(Clone, Default)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.0.store(true, Ordering::Relaxed);
    }

    #[inline]
    pub fn is_canceled(&self) -> bool {
        self.0.load(Ordering::Relaxed)
    }

    #[inline]
    pub fn check(&self) -> OpResult<()> {
        if self.is_canceled() {
            Err(OpError::Canceled)
        } else {
            Ok(())
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum OpError {
    Canceled,
    /// An English message, `{0}`... filled from `args` after translation
    Msg(String),
    Fmt(&'static str, Vec<String>),
}

impl OpError {
    pub fn msg(text: &str) -> Self {
        OpError::Msg(text.to_string())
    }
}

impl std::fmt::Display for OpError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            OpError::Canceled => write!(f, "Canceled"),
            OpError::Msg(text) => write!(f, "{text}"),
            OpError::Fmt(text, args) => write!(f, "{}", crate::i18n::fill(text, args)),
        }
    }
}

impl std::error::Error for OpError {}

pub type OpResult<T> = Result<T, OpError>;
