use std::hint::black_box;
use std::time::{Duration, Instant};

use msync::{compiler::Compiler, syntax};

fn measure(name: &str, iterations: u32, mut action: impl FnMut()) {
    for _ in 0..iterations.min(20) {
        action();
    }
    let mut samples = Vec::new();
    for _ in 0..7 {
        let start = Instant::now();
        for _ in 0..iterations {
            action();
        }
        samples.push(start.elapsed());
    }
    samples.sort();
    let elapsed: Duration = samples[samples.len() / 2];
    println!(
        "{name}: {:.1} ns/iter (median of 7 x {iterations})",
        elapsed.as_nanos() as f64 / f64::from(iterations)
    );
}

fn main() {
    if cfg!(debug_assertions) {
        println!("pipeline timings skipped in debug builds; use cargo bench --bench pipeline");
        return;
    }
    let seed = include_str!("../docs/dsl/spread.msync");
    let large = (0..1000)
        .map(|i| format!("file{i}.txt -> output/file{i}.txt\n"))
        .collect::<String>();
    let selectors = format!(
        "root := ~/configs\n{}",
        (0..100)
            .map(|i| format!("root/<.+\\.toml> -> out{i}/\n"))
            .collect::<String>()
    );
    let aliases = format!(
        "{}{}",
        (0..1000)
            .map(|i| format!("alias{i} := ~/configs/{i}\n"))
            .collect::<String>(),
        (0..1000)
            .map(|i| format!("alias{i}/x -> out{i}\n"))
            .collect::<String>()
    );
    for (label, source, count) in [
        ("seed", seed, 2000),
        ("1000-files", large.as_str(), 200),
        ("100-selectors", selectors.as_str(), 100),
        ("1000-aliases", aliases.as_str(), 20),
    ] {
        let ast = syntax::parse(source).unwrap();
        measure(&format!("parse/{label}"), count, || {
            black_box(syntax::parse(black_box(source)).unwrap());
        });
        measure(&format!("compile/{label}"), count, || {
            black_box(Compiler::new().compile(black_box(&ast)).unwrap());
        });
        measure(&format!("format/{label}"), count, || {
            black_box(syntax::format(black_box(source)).unwrap());
        });
        measure(&format!("pipeline/{label}"), count, || {
            let ast = syntax::parse(black_box(source)).unwrap();
            black_box(Compiler::new().compile(&ast).unwrap());
        });
    }
    let declarations = syntax::parse(
        &(0..1000)
            .map(|i| format!("alias{i} := ~/configs/{i}\n"))
            .collect::<String>(),
    )
    .unwrap();
    let mut repl = Compiler::new();
    repl.compile(&declarations).unwrap();
    let statement = syntax::parse("alias999/x -> output\n").unwrap();
    measure("repl/1000-bindings", 1000, || {
        black_box(repl.compile(black_box(&statement)).unwrap());
    });
}
