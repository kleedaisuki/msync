# Performance and maintainability workstream

## Active v0.6 optimization objective (2026-10-31)

This workstream now includes I/O, machine-independent compiler algorithms,
machine-dependent interpretation/preparation, compiler/interpreter/scheduler
overhead, editor tooling, configuration/diagnostics, user-visible millisecond
latency, and SQLite-backed end-to-end observability. The original objective is
not reduced to whichever isolated benchmark improves first.

| Requirement | Observable completion evidence |
| --- | --- |
| Platform I/O | Paired real-byte transport probes, measured mechanism selection, safe cancellation/fallback and unchanged publication |
| Compiler and interpreter algorithms | Explicit rewrite/proof boundaries, static type/provenance preservation, scaling/allocation and real-script comparisons |
| Execution machinery performance | Measured lowering/object/planner/control/scheduler costs with unchanged semantic results |
| Highlighting and toolchain | Full 0.6 token coverage, UTF-16-safe LSP diagnostics/navigation/formatting/completion, real stdio editor journeys and installable editor integration |
| Configuration and diagnostics | Conventional `config.toml`, defaults < config < compatibility environment < explicit CLI, actionable keyed diagnostics and effective-value provenance |
| User-visible latency | Absolute startup/submission/publication/idle-completion measurements; event-driven idle waits rather than periodic polling |
| SQLite logs and E2E telemetry | Queryable causal sessions/spans/events/errors/metrics across phases and tasks; bounded asynchronous ingestion, visible failures and normal-shutdown drain |

The baseline is the actual uncommitted 0.6 implementation, not HEAD's older
0.5.2 source. The optimized baseline image is
`target/performance-v6/baselines/msync-v060-before`, SHA-256
`ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca`.
An exact source archive (including untracked 0.6 modules), environment record and
frozen release rlib/probe images are retained beside it. Source archive contents
are authoritative for reproduction; a Git commit alone does not identify this
baseline. Transient logs and fixtures stay under `.temp/performance-v6/`.

Use disjoint implementation owners, at most root/lead/leaf depth, one Cargo
window, and one quiet measurement window at a time. Do not run all tests or
repeat successful gates without an affected contract or failure motivating it.
The historical full-suite commands below describe the earlier workstream, not a
requirement to ignore the current user's explicit incremental-testing direction.

Detailed cross-domain acceptance is maintained in
[performance-acceptance-v6.md](performance-acceptance-v6.md). The active goal stays
unfinished until every row has current evidence, including the practical
configuration/tooling/observability journeys rather than only microbenchmarks.

### Current v0.6 completion map: functional and measured acceptance closed

The final executable is `target/performance-v6/baselines/msync-v060-final`,
SHA-256 `bd836d6750c855f5f4982b355ab37855f32efff4e971c6b50fe3b83f617d50c3`.
Functional, component-performance, actual editor-host and final user-journey gates
are accepted, including the small-command cost and opt-in SQLite overhead below.
**Acceptance is not an all-scenes acceleration claim.** The earlier pure-control
regression and multi-second preparation residual were repaired rather than
waived. The integrating owner retains the final repository/artifact audit and
goal-status decision; no functionality or performance measurement row below is
left open. The rows retain the entire objective rather than replacing it with a
narrower compiler or I/O milestone.

| Requirement | Implementation and durable artifact | Current evidence | Accepted disposition and scope |
| --- | --- | --- | --- |
| Platform I/O | Linux file-to-TCP uses the measured 128KiB dispatch boundary; the same-handle buffered fallback and large-transfer kernel path remain. [Final mechanism/results](io-linux-v3.md), [final dispatch CSV](io-linux-v6-dispatch-final.csv), [final large-flow discriminator](io-linux-v6-dispatch-final-large.csv). | Final-image production probes verified 740 complete byte/EOF transfers. Four-size aggregate medians improved 11.7%/13.1%/10.5% at 4/64/128KiB. Boundary/offset units, real-socket boundary journey and selected Linux I/O cases passed. | Accepted. The 1MiB repeats are mixed (-2.7% then +3.6%, opposite-direction halves), not a generic large-file gain or strict no-regression bound. Final whole-CLI copy/EOF controls also passed; no Windows/WSL throughput improvement is claimed. |
| Machine-independent compilation and graph algorithms | [Compiler specialization/persistent captures](performance-v6-compiler.md), [logical conflict index](performance-v6-graph.md), [borrowed recursive object analysis](performance-v6-object.md). Public objects and proof output remain compatible. | Compiler/map/origin properties, graph completeness/sharing tests, selected HIR/plan (32), pure functions (9), mutations (5), prior symbols (18), object analysis (2), v0.6 objects (8) and legacy linker (9) passed. Accepted dense N=500 component: 949.447→455.955ms, 4,750,955→1,779,126 allocations, retained heap 282.015→179.169MB. Static object contextualization N=100: 223.073→98.450ms. | Accepted component results retain their exact measured image. Final larger shared-source/independent CLI plans improved 879.555→329.693ms and 1541.247→423.755ms. Small-script fixed setup costs and public quadratic proof-output limits remain explicit; universal speedup is not claimed. |
| Machine-dependent interpretation/preparation | [Indexed history/rewrite oracle](performance-v6-preparation.md), preparation-scoped observations, shared immutable logical paths and bounded observation-state representation. | Both 128-case oracle tests, the complete 37-case legacy preparation target, symlinked-ancestor key and affected publication/metadata regressions passed. Fresh N=500 controls: independent 3697.343→330.495ms (11.2×), overwritten 1691.392→104.515ms (16.2×). Missing-overwrite discovery is about 20×, explicitly not a fresh same-window control. | Accepted. The former unexplained 3.64s residual is resolved. Final integrated CLI and trace-overhead measurements are complete, with required phase/counter attribution and complete public proof checks intact. |
| Interpreter/scheduler machinery | [Indexed event frontier](block-frontier-performance-v6.md), [borrowed control plans and hot/cold future split](performance-runtime-v6.md), [runtime activity/provenance](runtime-activity-v6.md). | Frontier 13, runtime activity 4 parent journeys and runtime-control cases passed; final affected gates passed again after the hot/cold repair. Final 2,000-iteration pure control: 7.647→3.502ms, fresh selector: 332.032→125.767ms, each winning 7/7 alternating pairs. Requested allocation traffic fell 35.005→12.496MB and requests 42,164→30,166. | Accepted. The boxed-guard-only optimized-2 variant regressed about 25% and was rejected; the final synchronous future factory removes ordinary-path frame reservations from control execution. Real CLI/submission/idle and exporter-cost gates are now measured; frontier-only ratios are not copy throughput. |
| Highlighting and complete usable toolchain | [Native grammar/editor integration](editor-tooling-v6.md), [stdio LSP](toolchain-v6.md), [lexical index](tooling-index-v6.md), actual `dist/editor/msync-language-0.6.0.vsix`. | Two real stdio LSP journeys passed across targeted gates; all 7 LSP units, 11 lexical-index tests and 5 native editor units passed. The real TextMate/Oniguruma and client package tests passed 11. A real automated VS Code/Electron 1.144.0 host activated the production extension, repaired diagnostics, applied 14 formatting edits, returned range completion and 29 UTF-16 semantic tokens, and left all DSL effect targets untouched. | The native host gate is complete for its identified frozen server/bundle, not merely a packaging or mocked-API claim. Keep the final bundle/source alignment; run a new server integration gate only if the repair wave changes an affected compiler/tooling contract. No human visual-inspection or other-editor/platform claim is implied. |
| TOML configuration and actionable diagnostics | [Configuration/discovery/provenance](configuration-v6.md), `msync config`, `--config`, `--no-config`; shared resource/key validation before effects. | All 20 distinct config CLI cases passed across the initial target and narrow additions/reruns. They cover discovery/override/bypass, side-effect-free inspect/help/version, config-relative paths, real execution/spool policy and exact keyed diagnostics. Two real CLI newline/provenance tests and shared resource-limit units passed. | Accepted. Final small-command measurements report an approved sub-millisecond fixed setup cost, not a startup speedup. Help/version retain their no-discovery/no-exporter contract; only about 0.11ms was directly attributed to configuration discovery on a separate typed profile. |
| User-visible latency and practical REPL | [Latency/idle completion](latency-v6.md), worker-owned non-consuming readiness, native external-printer redraw and runtime job identity. | Six distinct real CLI/PTY latency journeys passed across incremental gates, 12 existing PTY cases passed, and all 18 REPL-job cases passed (16 initially plus the two affected targeted reruns). Five job-identity units passed. Cross-domain idle completion and cancellation journeys also passed. | Accepted unchanged optimized-3 cohorts: pure-binding command 4.692→3.728ms; persistent bindings 0.1276→0.0911ms. Final-artifact independent idle-notice p50/p95 is 0.636/0.731ms (9 observations); baseline 9/9 notices were censored at 150ms under a different method, so no ratio is computed. The 202-pair crossed-workspace copy discriminator is retained below, not described as universal no-regression. |
| SQLite logs and end-to-end observability | [Normalized storage/retention/failure](telemetry-sqlite.md), [causal phase API](telemetry.md), read-only session/span/event queries and JSONL export. | SQLite API 11, SQLite CLI 6 and existing JSONL 5 passed. Independent review passed 3 cases, including 24 actual CLI writer processes across 3 fresh databases. Five cross-domain configuration/runtime/SQLite journeys passed. The final runtime repair reran all 3 task-telemetry, 5 JSONL and 11 SQLite API cases successfully, retaining typed causes, ancestry and cancellation. | Accepted, with explicit opt-in cost: check100 off425.663→warmSQLite532.631ms; diagnostics100 off10.558→26.064ms; copy64KiB off38.039→48.120ms. Cold exporters cost more. Active coverage, flush/errors and read-only query/export were not removed to obtain the disabled-path gain. |

Counts above describe distinct selected cases, **not a full-suite rerun or one
single source-identical gate**. The final affected runtime gate and strict
all-target Clippy passed after the repair, without blindly repeating unrelated
feature targets. Failed assertions were followed by their affected
targeted reruns rather than repeating already-passing targets. The durable domain
notes record semantic corrections, especially lazy range preparation, canonical
formatter/display expectations and child-process error attribution. Their tests
and named native causes are part of the contract, not performance exceptions.

The root-owned command records supporting this map are
`.temp/performance-v6/{features,features-remaining,storage-tooling,unit-gates,
algorithms-remaining,final-functional,final-regressions,last-regressions,
freeze-gates,wave2-tests-repair,wave2-clippy,wave3-tests,wave3-fmt,
wave3-clippy}.log`. These are useful local audit pointers; the table and linked
domain notes preserve the conclusions if disposable logs are cleaned. Individual
ignored child fixtures are launched by their parent tests and are not described
as separate manually executed platform cases.

### Candidate identities and repair disposition

| Artifact | Identity and interpretation |
| --- | --- |
| Original v0.6 release baseline | Executable SHA-256 `ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca`; source archive SHA-256 `f325ae61d927fefc8bbbca24cc9fc59c2087319c3b9d7e4406fcf1f29a14c1a7`. |
| First integrated candidate | Executable SHA-256 `7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`; source archive SHA-256 `9591bdf06dad3d0ae2a24bb3ddbc2a4ba94df813e6a4b4c096c4378fc55cfbb9`. Component/whole-stream results name this image or its linked library explicitly; it is provisional. |
| Optimized-2 component snapshot | Executable SHA-256 `015645dc073986de9743c082ade38c6361cbd385ce754a8fed261065358a6b97`. Preparation/graph/object components were accepted for their named workloads, but the boxed-guard-only pure-control variant remained slower and was rejected. Those component sources remain unchanged in optimized-3. |
| Accepted repaired runtime/component image | `target/performance-v6/baselines/msync-v060-optimized-3`, executable SHA-256 `aa4749e3759d1cb55f898077e19f32a6cb8d37e8867c0c271f37da9af9adedcf`; matching frozen rlib SHA-256 `b36020ff901b728f1ea9914f4704d5300ead3c49f6cb751afcb4fff043c4b9b2`. Larger CLI and telemetry-cost measurements retain this exact image identity; the final patch only skips an unused last-index insertion. |
| Final accepted end-to-end image | `target/performance-v6/baselines/msync-v060-final`, executable SHA-256 `bd836d6750c855f5f4982b355ab37855f32efff4e971c6b50fe3b83f617d50c3`. Final startup/copy/EOF/idle and crossed-workspace interval cohorts identify this image explicitly; unchanged earlier component results are not relabeled as new runs. |
| Actual tested editor host | VS Code **1.144.0**, packaged `product.json` commit `2c33b50b16c472efe58357d1d2a8a5414bc23fbf`, dated 2026-10-27. Electron binary SHA-256 `d1c6fe7cec1e285f1210cd3c0472099c25bb56e94f0c8ef7384ee3229ed0b518`. CDN header `x-source-commit` is a different identity and is not the product commit. |
| Source-aligned native editor package | `dist/editor/msync-language-0.6.0.vsix`, 121,353 bytes / 10 files; SHA-256 `66271147afb0842b8f97deb7c31ecf9edbf0fb8cd23483820295905182a09adc`. Its bundled entrypoint SHA-256 `33cd1ffbd339f1c4a0ebc9fb8e10da996584c257040160c4e6778b2949089fa2` exactly matches the real-host-tested production bundle. |

The first candidate's roughly 58% disabled-telemetry pure-control regression and
3.64s preparation residual were rejected, not waived because another benchmark
improved. Moving `LinkedSpan` recording state out of line reduced the guard from
160 to 8 bytes but still left a roughly 25% runtime regression. A thin guard alone
did not remove the ordinary interpreter branch's physical async-frame reserve.

The accepted optimized-3 repair selects the control or ordinary path in a
synchronous future factory before constructing the corresponding future. The
4–16KiB allocation class fell from 8,006 requests to 2 startup requests over the
2,000-iteration pure workload, removing four giant frames per iteration. Pure
control now improves 7.647→3.502ms and fresh selectors 332.032→125.767ms, each
winning every one of seven alternating pairs. Total requested allocator bytes
fell 35.005→12.496MB. These are allocator traffic and component times, not RSS,
whole-CLI latency or storage throughput. The independently accepted shared-path
and observation-state repairs reduce the preparation residual as recorded above.

### Final user-visible acceptance and approved trade-offs

The [latency record](latency-v6.md) now closes the final integration matrix with
alternating frozen-image samples, actual payload checks and the final image
identity above. It records the exact harness/environment, p95 and paired spread;
the summary here is not a replacement for those boundaries.

| Final tiny-command control | Before p50 ms | Final p50 ms |
| --- | ---: | ---: |
| version | 2.802 | 2.982 |
| help | 2.740 | 2.826 |
| check one statement | 3.352 | 3.759 |
| plan one statement | 3.527 | 3.849 |
| REPL help then exit | 3.061 | 3.320 |

These approximately 0.09–0.41ms fixed increases are **explicitly approved
trade-offs** for the required conventional configuration, portable SQLite and
usable LSP/tooling capabilities. They are not hidden behind larger benchmark
wins, nor blamed entirely on config discovery: a separate typed startup profile
attributes only about 0.11ms to that phase. The rest of combined image/setup cost
was not causally isolated. Help/version still bypass discovery and exporter
initialization. Small-command startup is not uniformly faster.

Final 64KiB CLI copies improved at p50: missing destination 41.016→38.220ms;
existing destination 38.699→37.523ms. The first 32 persistent overwrites showed
26.652→27.724ms, so a bounded **202-pair crossed-workspace** discriminator was
required rather than discarding that negative point. Its paired mean differences
were −0.133ms normally and +0.236ms after swapping image/workspace placement,
overall +0.051ms; the complete paired range is −21.788 to +11.028ms. This does
not reproduce a stable 1ms penalty, but neither proves equal distributions nor a
universal zero interval cost. All pairs verify exact output bytes.

Two independent TCP streams retain approximately unchanged fast-leaf latency:
EOF→file 1.148→1.147ms and EOF→acknowledgment 1.387→1.371ms, while the first peer
remains held open. Leaf bytes, inode/device/mode, portable permissions and mtime
are checked. The obsolete parent-directory-mtime assertion failed on the frozen
baseline and was replaced according to the current statement publication contract;
it was not waived only for the candidate. Both images retain three private files /
493 bytes in the checked fixture, with absence of unrelated payload retention.
Final independent idle notices measure p50/p95 0.636/0.731ms over nine actual
EOF→notice observations with no input line. Baseline 9/9 notices were censored
at 150ms under a different probe method; no cross-method speedup ratio is invented.

SQLite recording is deliberately opt-in and not free. Warm/cold SQLite p50s are
532.631/551.673ms for check100, 26.064/31.683ms for 100 diagnostics, and
48.120/53.103ms for a real 64KiB copy. Corresponding off controls are respectively
425.663/430.588ms, 10.558/11.015ms and 38.039/38.606ms. The retained JSONL controls,
queryable sessions/counts, error outcomes and exact bytes are in the latency
record. Negative JSONL differences are variability, not exporter speedups.

The final functional and strict quality gates passed; source/harness formatting
and artifact hygiene remain part of the integrating owner's last repository
audit, not an open product feature or performance experiment. No remote CI or
cross-platform execution is inferred from a newly authored editor workflow.
The full objective is covered by the map above, while the root alone performs
the final goal-status transition. Historical blanket full-suite commands below
remain historical rather than an instruction to repeat successful gates.

### Practical entry points

- [Configuration commands and precedence](../../README.md#configure-and-inspect-settings):
  `msync config`, explicit TOML selection, CLI overrides and discovery bypass.
- [Recording, read-only query and export](../../README.md#diagnostics-and-verification):
  `--telemetry-db`, `telemetry sessions|summary|spans|events|export`, trace/span filters.
- [Native extension installation and stdio server](../../README.md#editor-and-language-server-workflow):
  install the identified VSIX, configure `msync.serverPath`, open a `.msync` file;
  other LSP clients launch `msync --language-server` over stdio.
- [Cross-domain functional evidence](performance-acceptance-v6.md): actual
  config/SQLite/cancellation/idle-completion and Unicode editor journeys,
  separate from component timing and the final release-image measurement record.

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

### Documentation rebuild and exact delivery identity

The final documentation refresh changes Rust crate metadata because `lib.rs`
embeds README documentation. Rebuilding that complete tree produced
`target/performance-v6/baselines/msync-v060-delivery`, SHA-256
`1fc660d436c8c3d733f651a14a3eda89066aa0336a5f5294c79dc637fb815d8e`.
This is also the checked `target/release/msync`. It must not be relabeled as the
previously measured `bd836d...` image. Production Rust sources and the dependency
lock were unchanged by the refresh, but the ELF layout differs, so the exact
new image received a targeted end-to-end control run rather than assuming a
byte-identical artifact.

That delivery run verifies all output bytes, private retention and normal exits.
Tiny-command costs remain explicitly mixed: version 2.513→2.577ms, help
2.454→2.612ms, check1 3.654→3.996ms, plan1 3.717→4.089ms, REPL help
2.797→3.226ms (30 alternating samples). Fresh 64KiB copy is a retained negative
point, 38.264→41.133ms; existing copy is 42.546→40.778ms (15 samples each).
Persistent overwrite is 27.297→27.397ms (32 samples, paired mean +0.100ms,
range −7.74 to +10.23ms). These observations do not establish a universal file
copy speedup or an absence of all tail costs. The earlier 202-pair crossed
experiment remains separately identified evidence rather than being substituted
for this image's rows.

Nine independent idle-completion observations on the exact delivery image give
p50/p95 0.606/0.689ms, with no further input after EOF, no destination before EOF,
exact file bytes, a still-live REPL and clean termination. Start/end image and
probe hashes matched. Detailed rows and method remain in
[latency-v6.md](latency-v6.md).

The repository audit records 339 distinct selected Rust tests with latest passing
evidence across incremental gates, zero remaining failures and eight separately
identified ignored fixture/performance entries. Generated property cases are not
counted as additional test functions. Eleven Node package cases and the actual
VS Code extension-host journey passed. Final all-target strict Clippy, rustfmt,
Python annotation/syntax and local-link/VSIX-entry alignment checks passed.
The focused editor workflow was validated as YAML/Bash and reproduces locally
executed commands; remote GitHub Actions were not launched. No Windows/WSL/SSH
performance claim or installer publication is implied.
