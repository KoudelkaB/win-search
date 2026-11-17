//! Times the engine on a file and reports the memory it holds:
//! `cargo run --release -p logexplorer-core --example bench -- <file> [--generate <megabytes>]`

use logexplorer_core::cancel::Cancel;
use logexplorer_core::load::{read_input, Input, LoadProgress};
use logexplorer_core::operator::{create_default, OpKind, OpType, Operator};
use logexplorer_core::pipeline::{active_operators, compute, StageCache};
use logexplorer_core::stage::Stage;
use logexplorer_core::typed::ValueType;
use std::io::Write;
use std::sync::{Arc, Mutex};
use std::time::Instant;

/// Resident memory of this process in MiB: the working set
#[cfg(windows)]
fn resident_mib() -> f64 {
    /// PROCESS_MEMORY_COUNTERS of psapi.h
    #[repr(C)]
    #[derive(Default)]
    struct Counters {
        size: u32,
        page_faults: u32,
        peak_working_set: usize,
        working_set: usize,
        pools_and_pagefile: [usize; 6],
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentProcess() -> isize;
        fn K32GetProcessMemoryInfo(process: isize, counters: *mut Counters, size: u32) -> i32;
    }
    let mut counters = Counters { size: std::mem::size_of::<Counters>() as u32, ..Default::default() };
    let known = unsafe { K32GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, counters.size) } != 0;
    if known {
        counters.working_set as f64 / (1 << 20) as f64
    } else {
        0.0
    }
}

/// Resident memory of this process in MiB (0 where it is not known)
#[cfg(not(windows))]
fn resident_mib() -> f64 {
    std::fs::read_to_string("/proc/self/status")
        .ok()
        .and_then(|status| status.lines().find(|line| line.starts_with("VmRSS:")).and_then(|line| line.split_whitespace().nth(1)?.parse::<f64>().ok()))
        .map_or(0.0, |kib| kib / 1024.0)
}

fn generate(path: &str, megabytes: usize) -> std::io::Result<()> {
    let mut out = std::io::BufWriter::with_capacity(1 << 22, std::fs::File::create(path)?);
    let levels = ["INFO ", "DEBUG", "WARN ", "ERROR"];
    let services = ["api", "worker", "auth", "billing", "search"];
    let mut written = 0usize;
    let mut state = 0x2545F4914F6CDD1Du64;
    let mut millis = 1_773_393_242_105u64;
    let mut request = 4711u64;
    while written < megabytes << 20 {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        millis += state % 40;
        request += 1;
        let (seconds, ms) = (millis / 1000, millis % 1000);
        let line = format!(
            "[{request:08}] 2026-03-{:02}T{:02}:{:02}:{:02}.{ms:03}Z {} {:<7} {} /api/orders/{} status={} took={}ms user={}\n",
            13 + (seconds / 86400) % 15,
            (seconds / 3600) % 24,
            (seconds / 60) % 60,
            seconds % 60,
            levels[(state >> 8) as usize % if state.is_multiple_of(50) { 4 } else { 2 }],
            services[(state >> 16) as usize % services.len()],
            if state.is_multiple_of(2) { "START" } else { "END  " },
            (state >> 24) % 100_000,
            [200, 200, 200, 201, 404, 500][(state >> 40) as usize % 6],
            (state >> 44) % 3000,
            (state >> 52) % 5000,
        );
        written += line.len();
        out.write_all(line.as_bytes())?;
    }
    out.flush()
}

fn op(id: u64, op_type: OpType, edit: impl FnOnce(&mut OpKind)) -> Operator {
    let mut operator = create_default(id, op_type, None);
    edit(&mut operator.kind);
    operator
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let path = args.first().expect("a file to load");
    if let Some(position) = args.iter().position(|arg| arg == "--generate") {
        let megabytes = args[position + 1].parse().expect("megabytes");
        generate(path, megabytes).expect("generated file");
        println!("generated {megabytes} MiB into {path}");
    }
    let base = resident_mib();
    let started = Instant::now();
    let Ok(Input::Text(source)) = read_input(std::path::Path::new(path), &LoadProgress::default(), &Cancel::new()) else { panic!("not a text file") };
    let raw: Arc<Stage> = Arc::new(source.stage);
    let file_mib = raw.bufs[0].len() as f64 / (1 << 20) as f64;
    println!(
        "load      {:>8.0} ms  {:>10} rows  {file_mib:.0} MiB text  RSS +{:.0} MiB ({:.2}x the file)",
        started.elapsed().as_secs_f64() * 1000.0,
        raw.len(),
        resident_mib() - base,
        (resident_mib() - base) / file_mib
    );

    let cache = Mutex::new(StageCache::default());
    let operators = vec![
        op(1, OpType::Filter, |kind| {
            if let OpKind::Filter(filter) = kind {
                filter.include[0].text = "status=500\nERROR".into();
            }
        }),
        op(2, OpType::Replace, |kind| {
            if let OpKind::Replace(replace) = kind {
                replace.search = "/api/orders/".into();
                replace.replacement = "/o/".into();
            }
        }),
        op(3, OpType::Sort, |kind| {
            if let OpKind::Sort(sort) = kind {
                (sort.pos, sort.len, sort.value_type) = (1, 8, ValueType::String);
            }
        }),
        op(4, OpType::Diff, |kind| {
            if let OpKind::Diff(diff) = kind {
                (diff.pos, diff.len, diff.value_type) = (11, 24, ValueType::DateTime);
            }
        }),
        op(5, OpType::Extract, |kind| {
            if let OpKind::Extract(extract) = kind {
                extract.pattern = r"user=(\d+)".into();
            }
        }),
        op(6, OpType::Unique, |_| {}),
        op(7, OpType::Join, |_| {}),
    ];
    for selected in 0..operators.len() {
        let started = Instant::now();
        let chain = active_operators(&operators, Some(selected));
        let stage = compute(&cache, &raw, &chain, &Cancel::new(), &|_| {}).expect("stage");
        println!(
            "{:<9} {:>8.0} ms  {:>10} rows  stage holds {:>7.1} MiB  RSS +{:.0} MiB",
            operators[selected].op_type().label(),
            started.elapsed().as_secs_f64() * 1000.0,
            stage.len(),
            stage.own_bytes as f64 / (1 << 20) as f64,
            resident_mib() - base
        );
    }

    // Operations over all rows of the source, each on its own
    let whole = [
        (
            "Filter 50%",
            op(10, OpType::Filter, |kind| {
                if let OpKind::Filter(filter) = kind {
                    filter.include[0].text = "start".into();
                }
            }),
        ),
        (
            "Sort date",
            op(11, OpType::Sort, |kind| {
                if let OpKind::Sort(sort) = kind {
                    (sort.pos, sort.len, sort.value_type) = (11, 24, ValueType::DateTime);
                }
            }),
        ),
        (
            "Sort text",
            op(12, OpType::Sort, |kind| {
                if let OpKind::Sort(sort) = kind {
                    sort.mode = logexplorer_core::operator::SortMode::Anchor;
                    sort.anchor = "took=".into();
                    sort.value_type = ValueType::String;
                }
            }),
        ),
        (
            "Diff date",
            op(13, OpType::Diff, |kind| {
                if let OpKind::Diff(diff) = kind {
                    (diff.pos, diff.len, diff.value_type) = (11, 24, ValueType::DateTime);
                }
            }),
        ),
        (
            "Replace all",
            op(14, OpType::Replace, |kind| {
                if let OpKind::Replace(replace) = kind {
                    replace.search = "status=".into();
                    replace.replacement = "s=".into();
                }
            }),
        ),
        (
            "Extract re",
            op(15, OpType::Extract, |kind| {
                if let OpKind::Extract(extract) = kind {
                    extract.pattern = r"took=(\d+)ms".into();
                }
            }),
        ),
        ("Unique", op(16, OpType::Unique, |_| {})),
    ];
    println!("--- each over the whole source ---");
    for (name, operator) in whole {
        let before = resident_mib();
        let started = Instant::now();
        let stage = logexplorer_core::ops::apply(&raw, &operator, &Cancel::new()).expect("stage");
        println!(
            "{name:<11} {:>8.0} ms  {:>10} rows  stage holds {:>7.1} MiB  RSS +{:.0} MiB while kept",
            started.elapsed().as_secs_f64() * 1000.0,
            stage.len(),
            stage.own_bytes as f64 / (1 << 20) as f64,
            resident_mib() - before
        );
    }
}
