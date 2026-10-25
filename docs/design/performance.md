# Compiler performance round

## Workload and scope

The correctness baseline passed the normal library, compiler, syntax, real-filesystem,
CLI/REPL and localhost HTTP journeys before optimization. This round targets CPU
latency in `check`, `compile`, and persistent REPL submissions, not disk or network
throughput. The supplied seed remains the small representative script. Larger
controls exercise plain copy lists, repeated directory selectors, and retained
bindings without changing the DSL's semantics.

`benches/pipeline.rs` measures:

- parse, compile, format, and parse+compile for the supplied seed;
- 1,000 plain file operations with no bindings;
- 100 operations sharing the same `.toml` selector;
- 1,000 path aliases followed by 1,000 copy operations;
- a one-operation REPL submission with 1,000 previously retained aliases.

Parse+compile includes AST construction and destruction, compilation and plan
destruction. Compile-only excludes parsing but includes compiler/plan allocation
and destruction. The REPL case retains its compiler between iterations. No copies,
external clients, telemetry exporter, or filesystem metadata operations run in the
timed paths. Consequently these numbers do not predict copy throughput.

## Setup and method

Measured on 2026-10-25, Windows x86_64 MSVC, Intel Core i9-12900H (14 physical /
20 logical cores), Rust 1.99.0, default Cargo optimized bench profile, lockfile
dependencies. Telemetry was disabled, as it is by default. Each case has up to 20
warmup iterations and seven samples; the printed result is the sample median.
Iterations per sample are 2,000 (seed), 200 (plain copies), 100 (selectors), 20
(alias batch), and 1,000 (REPL). `black_box` prevents unused-result elimination.

```powershell
cargo bench --bench pipeline
```

The harness skips timing in debug builds, so `cargo test --all-targets` does not
accidentally run a long unoptimized microbenchmark.

The initial before/after runs showed substantial laptop scheduling/turbo noise.
For the final comparison, saved pre/post executables were run in ABBA order with
PowerShell and inherited child affinity mask `1`, reducing hybrid-core migration:

```powershell
$process = Get-Process -Id $PID
$process.ProcessorAffinity = 1
# Run the saved baseline, optimized, optimized, baseline executables.
```

Raw experiment outputs and executable snapshots are under `.cache/performance/`
in this working checkout. The result ranges below retain both final per-run
medians rather than treating one favorable run as decisive.

## Bottlenecks and changes

1. **Repeated selector validation.** Previously each operation called
   `Regex::new` even when many operations used the same pattern. A successful
   validation set now lives only for one compilation, keyed by the original
   pattern. Each distinct pattern is validated once; invalid patterns still
   produce independent diagnostics. There is no unbounded process/REPL cache and
   no regex-engine substitution. Additional memory is bounded by the distinct
   successful pattern strings in the current submission.
2. **Alias lookup scanned every binding.** Binding names are already constrained
   to single path components. Looking up the first component in the existing
   `BTreeMap` replaces the linear longest-prefix scan, without introducing a trie
   or changing whole-component matching. Empty alias maps skip component scanning
   so the plain-copy workload does not pay for unused alias machinery.
3. **Transactional snapshots cloned all bindings.** Private binding maps now use
   `Arc<BTreeMap<...>>` with `Arc::make_mut`. Creating a candidate compiler is
   constant-time. Read-only submissions share the immutable environment; the
   first write clones only the modified map, and successful compilation commits
   it. Failed submissions retain the original environment. `Compiler` remains
   `Send + Sync`; `Rc` would have narrowed that library contract.

All public AST, plan and compiler interfaces, binding order, alias expansion,
rollback, and selector anchoring remain unchanged. Compiled regex execution in
the runtime is outside this optimization; the compiler only avoids redundant
validation work.

## Results

Ranges are the two final pinned per-run medians; units are microseconds. Ratios
for targeted cases use the midpoint of the two medians and are intentionally
rounded.

| Workload | Before (µs) | After (µs) | Interpretation |
| --- | ---: | ---: | --- |
| Compile seed | 42.18–42.33 | 35.14–42.90 | Small-script control; no speedup claim |
| Compile 1,000 plain copies | 176.02–198.32 | 182.90–184.43 | Essentially unchanged control |
| Parse+compile 1,000 plain copies | 402.42–412.13 | 411.56–465.96 | No acceleration claim; midpoint +31 µs, noisy |
| Compile 100 repeated selectors | 2,970.77–3,023.17 | 64.95–74.01 | About 43× faster |
| Parse+compile 100 repeated selectors | 2,765.34–2,880.84 | 97.09–116.64 | About 26× faster |
| Compile 1,000 aliases + copies | 9,424.19–10,060.11 | 844.22–950.11 | About 11× faster |
| Parse+compile 1,000 aliases + copies | 10,530.63–11,206.91 | 1,401.47–1,675.11 | About 7× faster |
| REPL copy with 1,000 retained bindings | 95.35–101.19 | 0.499–0.513 | About 194× faster |

The plain parse+compile control is not evidence of a general speedup: its final
midpoint increased about 7.7%, while compile-only did not regress, and an earlier
unpinned pair was 378.80 → 380.88 µs. This remains a small absolute CPU cost for a
1,000-operation script; do not extrapolate the targeted gains into overall file
sync speed. The accepted trade-off is dramatically lower selector and retained-
environment latency, without a demonstrated plain compilation regression or
extra runtime copy machinery. Host timing noise and unchanged-parser/code-layout
effects remain unresolved at this scale.

An additional preallocation experiment required another AST pass and did not
demonstrate benefit; it was discarded rather than retained speculatively.

## Correctness checks

```powershell
cargo test --all-targets
cargo clippy --all-targets -- -D warnings
```

After the optimization these commands passed (57 tests at this snapshot; one
live-WSL test intentionally ignored). Compiler-specific regressions check
copy-on-write snapshot isolation after both maps mutate, repeated selector
validation and rejection, and slash/backslash component boundaries. Existing
tests continue to cover failed-submission rollback, the user's seed, full-name
matching, real recursive copies, REPL state/error recovery, formatting properties,
Unicode diagnostics, and end-to-end telemetry.

## Engineering and research context

- The [regex crate's performance guidance](https://docs.rs/regex/1.13.1/regex/#avoid-re-compiling-regexes-especially-in-a-loop)
  identifies repeated compilation as avoidable expensive work. A submission-local
  validation set follows that guidance without retaining arbitrary user patterns
  forever.
- The [standard `Arc::make_mut` contract](https://doc.rust-lang.org/std/sync/struct.Arc.html#method.make_mut)
  provides the required clone-on-write behavior directly, avoiding mutation logs,
  manual rollback, interior mutability, and a special read-only compile path.
- Davis et al., [Why Aren't Regular Expressions a Lingua Franca?](https://arxiv.org/abs/2105.04397)
  (ESEC/FSE 2019; arXiv version 2021) studies semantic and performance differences
  across regex engines. Its relevant practical lesson here is to preserve the
  established Rust-regex matching contract while changing validation frequency,
  not migrate selectors into shell/grep engines to chase speed.

Further work should start from real user traces. If copy/transport I/O dominates,
another compiler micro-optimization is not the right next investment.
