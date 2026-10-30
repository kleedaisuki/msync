# Performance and maintainability workstream

## Scope and ownership

This workstream starts at `c5696a8` (0.3.1). The objective is measured improvement
of useful synchronization journeys while making execution policies easier to
change safely, not a rewrite or a file-size reduction exercise.

Coordination has at most three levels: an integrating owner, an engineering lead,
and bounded specialists. The integrating owner owns acceptance and this roadmap;
the lead owns implementation boundaries and measured results. Specialists must
have disjoint edit ownership and cannot delegate further. The shared design
notes are the durable handoff; transient messages and `.temp` logs are not the
only record of a result.

## Contracts that must survive

- Preserve DSL, CLI, public library APIs, diagnostics/provenance, machine output,
  default resource limits, and disabled-by-default telemetry.
- Preserve the distinction between exact assignment and container insertion,
  conservative path-alias conflicts, and regular-file-only rewrite proofs.
- Keep preparation caches scoped to one preparation; never promote observations
  to process-global filesystem truth.
- Keep file publication owned by one layer. Cancellation must not publish an
  incomplete stage or obscure earlier committed leaves. There is no whole-tree
  rollback or crash-durability promise.
- Keep consuming streams distinct from repeatable file reads, retain co-start
  admission, and preserve buffer accounting and idle-destination-close behavior.
- Keep OS errors and useful operation context observable; performance work must
  not silently remove tracing or replace errors with generic messages.

See [implementation](implementation.md), [planning](planning.md),
[planner proof obligations](planner-proof.md), [runtime](runtime.md), and
[streams](streams.md) for the detailed contracts.

## Work queue and decision gates

| Order | Workload or seam | First question | Delivery gate |
| --- | --- | --- | --- |
| 1 | Repeated selector/tree execution and preparation | Is repeated policy/regex or path work still paid per leaf or operation? | A release-mode comparison, unchanged selection/errors, and real copy results |
| 2 | Finite-copy versus remote transport policy in `runtime.rs` | Can a cohesive private policy be isolated without widening internal APIs? | Reduced policy duplication and existing local/remote safety regressions |
| 3 | Scheduler readiness and stream admission | Does scheduling overhead matter after transfer costs, and under which graph shapes? | Independent, chain, fan-out and co-start controls; bounded resource assertions |
| 4 | Trace-enabled execution | Does event formatting/locking materially affect useful traced workloads? | Trace-on/off controls and preserved causal, progress and commit information |
| 5 | Platform I/O | Is there a new measured workload not covered by the previous kernel-path work? | Actual native platform data, cancellation/fallback checks; no capability-only claims |

These are investigation priorities, not claims that every item is a bottleneck.
Choose the first implementation wave from evidence. Do not redo indexed planning,
compiler copy-on-write bindings, metadata batching or OS fast paths already
documented in the performance notes without a new discriminating measurement.
Defer unrelated transport expansion and public API changes.

### Selected first wave

Inspection of the current executor identified a more direct first target than
selector work: every completion rescans the remaining ready-node queue, including
when its lane cannot admit another operation. Independent small copies with a
small job limit can therefore pay quadratic admission work. The implementation
wave will measure and address that path with precomputed cohort resource demands
and aggregate readiness. Preserve the earliest eligible enqueue order across
resource classes; optimizing admission must not become shortest-job scheduling
or head-of-line blocking of an otherwise runnable stream or smaller cohort.

The maintainability change isolates metadata-only preparation observations from
copy execution in a private `runtime/inspection.rs` module. Its cache lifetime,
remote grouping, symlink/ancestor checks and error behavior remain unchanged.
This is a state-ownership boundary, not a new transport abstraction. Subsequent
runtime transport decomposition remains evidence-gated follow-up work.

## Measurement and acceptance

Preserve a baseline release binary or benchmark before changing the measured
code. Compare the same fixtures, lockfile, toolchain, filesystem, build profile
and telemetry mode. Run before/after samples serially, preferably alternating
order; separate compilation from measured execution. Include a small-script
control, targeted repeated work, and a correctness assertion on resulting bytes.
Report absolute time and repeatability, not just an attractive ratio. A CPU
microbenchmark is not evidence of network or disk throughput improvement.

The existing `pipeline`, `planner` and `startup` custom harnesses remain useful.
Add a maintained focused harness when they do not model the selected execution
path. Keep fixture data and disposable logs under repository `.temp`; keep the
setup, useful measurements and interpretation in a tracked design note so cleanup
does not erase the result. Avoid concurrent benchmark runs and cargo builds.

Required integration gates for a code wave:

```sh
cargo fmt --check
cargo clippy --locked --all-targets -- -D warnings
cargo test --locked --all-targets
cargo test --locked --doc
```

Also execute the affected CLI user journey: check, plan, offline preview with no
target write, actual transfer, and output-byte comparison. Existing PTY, localhost
network, cancellation and property tests cover important interactions beyond
individual functions. Add a discriminating regression for changed behavior.
Linux results do not certify Windows/WSL/authenticated SSH; record platform
coverage explicitly rather than silently counting gated-out tests as exercised.

## Starting evidence

On 2026-10-30, baseline all-target tests, doctests and formatting passed on native
Linux x86-64, kernel `6.12.113+deb13-amd64`, ext4, Rust `1.99.1`, with the checked-in
lockfile. Default all-target runs intentionally exclude environment-specific and
performance experiments. This is a correctness baseline, not a new performance
measurement. Implementation-wave measurements and final integration results are
recorded below once available.

## First-wave result and integration acceptance

The first wave is implemented. See [scheduler performance](performance-scheduler.md)
for the mechanism, benchmark code, baseline provenance, repeatable commands,
sample ranges and state-size trade-off. A real 10,000-small-file warm-overwrite
CLI journey at four jobs improved from 4,353.247 to 2,198.687 ms median over seven
alternating samples per executable on this host. Separate 31-sample one-file and
100-file controls do not establish a material speedup or regression. Do not
extrapolate these results to cold storage, remote clients or large-file throughput.

The metadata-only subsystem now resides in private `runtime/inspection.rs`, with
the same crate-level entry points and preparation-owned cache. The scheduler uses
cached cohort demands and fully-ready resource classes rather than repeatedly
scanning all waiting members. Differential generated tests and a shared-predecessor
cohort regression accompany the change.

Final root integration on the baseline host passed formatting, strict locked
all-target Clippy, locked all-target tests (252 passed, 5 explicitly ignored),
one doctest, and `git diff --check`. Windows-specific targets are gated out on
Linux; Windows, WSL and authenticated SSH were not run in this wave. No dependency
or lockfile changes were needed, and no commit was created.

The release CLI was also exercised directly with real files under repository
`.temp/optimization-wave/journey`:

1. Check and plan a script containing exact assignment and a regex selector.
2. Dry-run it and assert both destinations remain absent.
3. Run with tracing, compare both copied outputs and confirm the nonmatching
   child was excluded. Inspect `file.commit` and committed-byte trace records.
4. Change the source and run exact assignment again; compare fresh output bytes.
5. Change a selected source and rerun container insertion; require a failure and
   verify the prior destination bytes were not overwritten.
6. Pipe a binding, `/bindings`, `/dry-run on`, a transfer and `/quit` into the REPL;
   verify no preview destination was created.

Actual `strace -f -c` on the release overwrite journey observed two
`copy_file_range` calls and one `rename`, consistent with the retained staged
native-copy path. This is a diagnostic check, not the timing comparison.
`perf stat` with task-clock/cycles/instructions was attempted but denied by the
host's `perf_event_paranoid=3`; host security settings were not changed and no
hardware-counter result is claimed. Disposable gate logs and tool output are
under `.temp/optimization-wave`; the conclusions above survive cleanup.

Next selection should follow the queue above, using actual workload frequency.
In particular, profile trace-enabled or mixed-demand execution before enlarging
the scheduler machinery, and separate runtime transport policy only along
demonstrable ownership/duplication seams. The present result does not justify a
custom executor, a new global cache, or an io_uring rewrite.

## Method references

- [The Rust Performance Book: Benchmarking](https://nnethercote.github.io/perf-book/benchmarking.html)
  motivates representative workloads, user-visible metrics and caution about
  wall-time variation.
- [Cargo benchmark command](https://doc.rust-lang.org/cargo/commands/cargo-bench.html)
  documents optimized benchmark profiles and custom harness integration.
- [Multi-resource backfilling research](https://arxiv.org/abs/2111.10200) provides
  related context on scheduling with both compute and buffer requirements. The
  relevant lesson here is to model each constrained resource explicitly. This
  project has a finite dependency graph and no runtime estimates; it does not
  adopt HPC reservation policies or claim their utilization guarantees.
- Existing local measurements: [compiler](performance.md),
  [planner](performance-planner-v3.md), [startup](performance-startup-v3.md),
  [Linux I/O](io-linux-v3.md), and [Windows I/O](io-windows-v3.md).
