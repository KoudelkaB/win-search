//! The pipeline: which stage follows from the source and the enabled operations, and a
//! cache of the stages already computed.
//!
//! A stage is cached under the whole chain of operations that leads to it, so editing
//! operation 5 keeps the stages of operations 1-4 and switching an operation off and on
//! again costs nothing.

use crate::cancel::{Cancel, OpResult};
use crate::operator::Operator;
use crate::ops;
use crate::stage::Stage;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::{Arc, Mutex};

pub const CACHE_MAX_STAGES: usize = 24;
const CACHE_FLOOR_BYTES: usize = 64 << 20;
const CACHE_MIN_BYTES: usize = 128 << 20;
const CACHE_MAX_BYTES: usize = 512 << 20;

#[cfg(windows)]
fn available_memory_bytes() -> Option<usize> {
    #[repr(C)]
    struct MemoryStatusEx {
        length: u32,
        memory_load: u32,
        total_phys: u64,
        avail_phys: u64,
        total_page_file: u64,
        avail_page_file: u64,
        total_virtual: u64,
        avail_virtual: u64,
        avail_extended_virtual: u64,
    }
    unsafe extern "system" {
        fn GlobalMemoryStatusEx(buffer: *mut MemoryStatusEx) -> i32;
    }
    let mut status = MemoryStatusEx {
        length: std::mem::size_of::<MemoryStatusEx>() as u32,
        memory_load: 0,
        total_phys: 0,
        avail_phys: 0,
        total_page_file: 0,
        avail_page_file: 0,
        total_virtual: 0,
        avail_virtual: 0,
        avail_extended_virtual: 0,
    };
    let ok = unsafe { GlobalMemoryStatusEx(&mut status) };
    (ok != 0 && status.avail_phys > 0).then(|| status.avail_phys.min(usize::MAX as u64) as usize)
}

#[cfg(target_os = "linux")]
fn available_memory_bytes() -> Option<usize> {
    let meminfo = std::fs::read_to_string("/proc/meminfo").ok()?;
    for line in meminfo.lines() {
        if let Some(rest) = line.strip_prefix("MemAvailable:") {
            let kib: u64 = rest.split_whitespace().next()?.parse().ok()?;
            return Some(kib.saturating_mul(1024).min(usize::MAX as u64) as usize);
        }
    }
    None
}

#[cfg(not(any(windows, target_os = "linux")))]
fn available_memory_bytes() -> Option<usize> {
    None
}

fn cache_budget(source_bytes: usize, available_ram: Option<usize>) -> usize {
    let target = (source_bytes / 2).clamp(CACHE_MIN_BYTES, CACHE_MAX_BYTES);
    match available_ram {
        Some(available) => target.min((available / 4).max(CACHE_FLOOR_BYTES)),
        None => target,
    }
}

pub type ChainKey = u64;

fn chain_key(parent: ChainKey, signature: &str) -> ChainKey {
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    parent.hash(&mut hasher);
    signature.hash(&mut hasher);
    hasher.finish()
}

struct Entry {
    stage: Arc<Stage>,
    parent: ChainKey,
    signature: String,
    used: u64,
}

/// Computed stages, the least recently used dropped first.
pub struct StageCache {
    entries: HashMap<ChainKey, Entry>,
    clock: u64,
    max_bytes: usize,
}

impl Default for StageCache {
    fn default() -> Self {
        StageCache { entries: HashMap::new(), clock: 0, max_bytes: cache_budget(0, available_memory_bytes()) }
    }
}

impl StageCache {
    pub fn clear(&mut self) {
        self.entries.clear();
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Memory held by the cached stages on top of the source
    pub fn bytes(&self) -> usize {
        self.entries.values().map(|entry| entry.stage.own_bytes).sum()
    }

    /// Adaptive cap: half the source clamped to 128-512 MiB, and never more than a quarter
    /// of currently available RAM (with a 64 MiB floor so at least a few stages stay cached).
    pub fn set_source_bytes(&mut self, bytes: usize) {
        self.max_bytes = cache_budget(bytes, available_memory_bytes());
    }

    fn get(&mut self, key: ChainKey, parent: ChainKey, signature: &str) -> Option<Arc<Stage>> {
        self.clock += 1;
        let entry = self.entries.get_mut(&key).filter(|entry| entry.parent == parent && entry.signature == signature)?;
        entry.used = self.clock;
        Some(entry.stage.clone())
    }

    fn insert(&mut self, key: ChainKey, parent: ChainKey, signature: String, stage: Arc<Stage>) {
        self.clock += 1;
        self.entries.insert(key, Entry { stage, parent, signature, used: self.clock });
        while self.entries.len() > 1 && (self.entries.len() > CACHE_MAX_STAGES || self.bytes() > self.max_bytes) {
            let oldest = self.entries.iter().filter(|(candidate, _)| **candidate != key).min_by_key(|(_, entry)| entry.used).map(|(key, _)| *key);
            match oldest {
                Some(oldest) => self.entries.remove(&oldest),
                None => break,
            };
        }
    }
}

/// The enabled operations up to and including `selected` (`None`: the source itself)
pub fn active_operators(operators: &[Operator], selected: Option<usize>) -> Vec<&Operator> {
    match selected {
        None => Vec::new(),
        Some(selected) => operators.iter().take(selected + 1).filter(|operator| operator.enabled).collect(),
    }
}

/// The deepest stage of the chain that is already computed, and whether it is the last one
pub fn cached_stage(cache: &Mutex<StageCache>, raw: &Arc<Stage>, chain: &[&Operator]) -> (Arc<Stage>, bool) {
    let mut cache = cache.lock().unwrap();
    let mut stage = raw.clone();
    let mut key = raw.id;
    for operator in chain {
        let signature = operator.signature();
        let next = chain_key(key, &signature);
        match cache.get(next, key, &signature) {
            Some(found) => stage = found,
            None => return (stage, false),
        }
        key = next;
    }
    (stage, true)
}

/// The stage after the whole chain; every new stage on the way is cached. `on_step` is told
/// the operation being applied.
pub fn compute(cache: &Mutex<StageCache>, raw: &Arc<Stage>, chain: &[&Operator], cancel: &Cancel, on_step: &dyn Fn(&Operator)) -> OpResult<Arc<Stage>> {
    let mut stage = raw.clone();
    let mut key = raw.id;
    for operator in chain {
        let signature = operator.signature();
        let next = chain_key(key, &signature);
        let found = cache.lock().unwrap().get(next, key, &signature);
        stage = match found {
            Some(found) => found,
            None => {
                on_step(operator);
                let computed = Arc::new(ops::apply(&stage, operator, cancel)?);
                cache.lock().unwrap().insert(next, key, signature, computed.clone());
                computed
            }
        };
        key = next;
    }
    Ok(stage)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::operator::{create_default, OpKind, OpType};
    use crate::stage::testing::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    fn filter(id: u64, text: &str) -> Operator {
        let mut operator = create_default(id, OpType::Filter, None);
        if let OpKind::Filter(filter) = &mut operator.kind {
            filter.include[0].text = text.to_string();
        }
        operator
    }

    #[test]
    fn stages_are_computed_once_and_survive_edits_further_down() {
        let raw = Arc::new(stage_of(&["a1", "a2", "b1", "b2"]));
        let cache = Mutex::new(StageCache::default());
        let mut operators = vec![filter(1, "a"), filter(2, "1")];
        let steps = AtomicUsize::new(0);
        let count = |_: &Operator| {
            steps.fetch_add(1, Ordering::Relaxed);
        };
        let run = |operators: &[Operator], selected| {
            let chain = active_operators(operators, selected);
            lines_of(&compute(&cache, &raw, &chain, &Cancel::new(), &count).unwrap())
        };
        assert_eq!(run(&operators, Some(1)), ["a1"]);
        assert_eq!(steps.load(Ordering::Relaxed), 2);
        assert_eq!(run(&operators, Some(0)), ["a1", "a2"]);
        assert_eq!(run(&operators, None), ["a1", "a2", "b1", "b2"]);
        assert_eq!(steps.load(Ordering::Relaxed), 2);

        // Editing the second operation recomputes only it
        operators[1] = filter(2, "2");
        assert_eq!(run(&operators, Some(1)), ["a2"]);
        assert_eq!(steps.load(Ordering::Relaxed), 3);

        // Disabling the first one gives a different chain; enabling it again is cached
        operators[0].enabled = false;
        assert_eq!(run(&operators, Some(1)), ["a2", "b2"]);
        operators[0].enabled = true;
        let before = steps.load(Ordering::Relaxed);
        assert_eq!(run(&operators, Some(1)), ["a2"]);
        assert_eq!(steps.load(Ordering::Relaxed), before);

        let chain = active_operators(&operators, Some(1));
        assert!(cached_stage(&cache, &raw, &chain).1);
        operators[1] = filter(2, "zzz");
        let chain = active_operators(&operators, Some(1));
        let (partial, complete) = cached_stage(&cache, &raw, &chain);
        assert!(!complete);
        assert_eq!(lines_of(&partial), ["a1", "a2"]);
    }

    #[test]
    fn a_canceled_computation_stops() {
        let raw = Arc::new(stage_of(&["a"]));
        let cache = Mutex::new(StageCache::default());
        let operators = vec![filter(1, "a")];
        let cancel = Cancel::new();
        cancel.cancel();
        let chain = active_operators(&operators, Some(0));
        assert!(compute(&cache, &raw, &chain, &cancel, &|_| {}).is_err());
        assert!(cache.lock().unwrap().is_empty());
    }

    #[test]
    fn cache_budget_scales_with_source_and_respects_available_ram() {
        assert_eq!(cache_budget(20 << 20, Some(8 << 30)), CACHE_MIN_BYTES);
        assert_eq!(cache_budget(1200 << 20, Some(8 << 30)), CACHE_MAX_BYTES);
        assert_eq!(cache_budget(1200 << 20, Some(1024 << 20)), 256 << 20);
        assert_eq!(cache_budget(1200 << 20, Some(100 << 20)), CACHE_FLOOR_BYTES);
        assert_eq!(cache_budget(600 << 20, None), 300 << 20);
    }
}
