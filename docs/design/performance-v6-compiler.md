# v0.6 compiler work reduction

## Baseline and scope

The source of truth is the root-frozen v0.6 release snapshot, not prior green
reports or an old version tag. The frozen executable is
`target/performance-v6/baselines/msync-v060-before`, SHA-256
`ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca`.
Its source archive, toolchain and rlib are preserved alongside the executable.
Compiler changes begin only after that baseline was frozen. Root coordinates
serial measurements/build windows; fixture creation and compilation are outside
the measured interval.

This note owns pure lowering work. Indexed static graph/planner work,
metadata-informed preparation sweeps, and borrowed object analysis are recorded
in their own `performance-v6-*` notes. No observation of live files is speculated
through a bool expression, conditional, loop or synchronization boundary.

## Chosen mechanisms

### Specialize one transfer operand once

The initial v0.6 implementation resolved both transfer operands to decide whether
a dynamic control node was needed. If both were static, the flat transfer lowerer
resolved them again. This repeated complete lexical function instantiation,
argument substitution, path joining, provenance expansion and work-budget usage.

The dispatch now returns the already typed operand pair to flat lowering.
Dynamic transfers keep their existing typed control path. No memo table, cache
invalidation policy or second evaluation model is introduced. Both operands
still typecheck, errors retain expression provenance, and compile-time function
calls are charged once per actual specialization.

### Branch debug side tables are append-only candidates

Instruction IDs are allocated monotonically within one compilation. Therefore
all debug origins produced while checking an arm are a suffix of the ordered
origin map. The initial implementation cloned the entire prefix before and after
each conditional, although that history was immutable. A script with N sibling
conditions copied O(N²) prior origin records.

Each conditional now checkpoints its next instruction ID, splits off only newly
added records, checks the other arm against the retained prefix, and extends the
prefix with the selected arm or both dynamic arms. Small suffix insertion uses
`extend`, not a whole-map bulk `append`. Dead-arm relocations/linear capabilities
and both-arm static checks keep the existing semantics. This changes the amount
of metadata copied, not which branches execute or which errors are diagnosed.

### Read-only REPL submissions do not rebuild legacy aliases

The structured Session retains a compatibility Compiler with path/space maps.
Previously every successful submission rebuilt those maps by scanning all
historical bindings, even a single read-only transfer. The session's value map
is immutable behind Arc until a binding changes. Pointer identity consequently
proves when the compatibility maps remain exact: if the candidate value map is
the same Arc, retain the existing compatibility maps. A changed map still takes
the normal full refresh path. Macro definitions, source provenance and context
updates are not incorrectly treated as filesystem state.

## Measurement harness

`benches/compiler_v6.rs` is a maintained release-only public-API harness. Build the
same harness source against both frozen-baseline and candidate rlibs using the
same rustc optimization flags. Environment controls:

- `MSYNC_COMPILER_SIZES=10,100,1000`
- `MSYNC_COMPILER_CASES=transfers,symbols,conditions,captures,macro-captures,bindings`
- `MSYNC_COMPILER_SAMPLES=7`

Parsing/source construction is outside the timer. Each sample creates/destroys
a fresh compilation candidate and Lowered result. `bindings` instead preloads N
path bindings outside timing, then measures one transfer against a session clone,
representing growing REPL history without input changes. Other cases respectively
exercise flat file transfers, symbol/path substitution, dynamic two-arm
conditionals, repeated symbol captures and repeated macro captures. Both template
kinds use one generic snapshot helper: absent parent environments retain the
original Arc maps directly, while nested declarations merge the nearest locals
onto structurally shared ancestor maps. This avoids rebuilding all top-level
locals merely to manufacture an independent snapshot wrapper.

One warmup precedes repeated samples. Report median/min/max, and separately count
allocation calls and allocated bytes in an untimed build with the same allocator
on both arms. Those are total allocator traffic, not peak RSS or filesystem IO.
The harness was revised to model REPL reads after its first baseline executable
was built: rebuild both arms from the current same source, do not compare the old
`bindings` setup measurement to the new read-only measurement.

## Invariants and validation

New unit tests assert that two static transfer operands instantiate their symbol
exactly twice total and that a read-only submission preserves both legacy table
identities. A generated test compares recursive live instruction IDs with exact
origin keys across arbitrary prefix lengths and nested constant/dynamic arms.
Existing macro/symbol/IR/object/provenance and end-to-end control journeys remain
the compatibility gate. Test duration is not benchmark evidence.

The `compile.lower` telemetry stage records instruction allocation, runtime-slot
allocation, function specializations and failures. Disabled tracing does not
format dynamic labels or allocate instrumentation payloads.

## Engineering and research context

[Rust's BTreeMap](https://doc.rust-lang.org/std/collections/struct.BTreeMap.html)
provides ordered range splitting directly; a bespoke checkpoint map is not
needed. [Arc pointer identity and copy-on-write](https://doc.rust-lang.org/std/sync/struct.Arc.html)
make unchanged-state detection exact rather than heuristic.

[Partial Evaluation, Whole-Program Compilation (PLDI 2025)](https://cfallin.org/pubs/pldi2025_weval.pdf)
frames specialization as eliminating repeated interpretation while keeping the
runtime as the semantic authority. Here the smaller practical application is to
retain already-specialized typed operands rather than interpreting pure function
bodies a second time. No claim about weval-level speedups applies to this DSL.

[imbl persistent maps](https://docs.rs/imbl/latest/imbl/)
were an evidence-gated option at the start of this wave. Its own documentation
reports slower ordinary ordered-map operations; the measured capture cost below
justified trying the maintained library for compiler-private snapshot tables,
rather than changing public maps or writing a persistent tree. Ordinary small
cases and non-snapshot transfers remain explicit adoption controls.

## Results

Root-owned unit gates passed the 3 new lowering tests (including generated origin
coverage), 2 typed conflict-index properties, and 2 preparation slow-oracle
comparisons. Relevant lexical, object and planner targets also passed before the
old selector expectation was isolated below. Final candidate paired measurements
remain pending. Source-level complexity reduction alone is not reported as a
measured user-visible speedup.

### Evidence-gated persistent lexical environments

The first frozen-baseline probe measured 1,000 interleaved value/template
captures at 787.950 ms and 3,335,630 allocation calls (313,989,065 allocated
bytes). A template must retain earlier bindings and registries, so repeated
`Arc<BTreeMap>` detach clones grew with the entire captured prefix. This is not
avoidable by weakening definition-time lexical snapshots.

That measurement justified the root-approved `imbl` 7.0.2 dependency candidate.
Compiler-private captured ordered maps and sets now use persistent B+trees;
linear-consumption snapshots use its persistent hash map. Existing Arc ownership
and definition-time capture remain intact. Public compatibility maps, exported
BTreeMaps, source tables and the serialized object schema are unchanged, and
ordered iteration remains deterministic. Mutation copies only the changed tree
path rather than every old binding. Ordinary tiny-map controls are measured
because structural sharing is not automatically faster for non-snapshot work.
The dependency's documented ordered-map basic-operation trade-off remains a
reason to measure, not a reason to retain demonstrated quadratic capture cost.

### Accepted baseline procedures so far

Compiler baseline: 7 samples per case at sizes 10/100/1000, current same-source
harness including read-only REPL case. Clean results are in
`.temp/performance-v6/compiler-before.txt`. Object baseline was bounded to
20/50/100 entries with 3 samples: static context construction at 100 entries
already produces dense unknown-destination dependency proofs, so the default
1000/5000 cases would conflate a much larger public proof output with borrowed
analysis. Graph baseline independently measures independent/shared-source sizes
10/100/500/1000 and dense sizes 10/100/500 (7 samples). Preparation uses the
existing `planner_v4` harness linked against the frozen rlib at 100/500 (3
samples), including real local input/output fixture setup outside timing.

An early object/graph attempt overlapped after a polling call returned a live
session; its partial files are explicitly named `*-discarded-overlap` and are
excluded from results. All clean before runs were serial and completed before
any candidate release build. Final comparisons must run both frozen images
alternating order with current same-source harnesses; standalone before medians
alone are not an accepted speedup claim.

### Inherited selector-preflight expectation

The focused old preparation suite initially failed its assertion that
`file/<.*> -> target/` must be rejected during preparation. A bounded probe linked
against the frozen v0.6 rlib reproduced successful preparation with one typed
Control::For and no eager range enumeration. After normal object
contextualization, that same frozen version failed at execution with native
`Not a directory (os error 20)` and left both source bytes and the empty target
directory unchanged. The first raw-IR execution attempt was correctly rejected
as uncontextualized; only the subsequent context-bound execution establishes the
not-directory behavior. Probe source/output remain in repository `.temp`.

The test now distinguishes immediate plain file/directory mode errors from lazy
range evaluation: preparation preserves the range even for a missing root,
while a context-bound actual run over a regular-file root must fail without
output. This preserves current v0.6 semantics; the optimizer was not changed to
speculate IO or suppress the wrong-selector error.

The final capture closure adds one focused test checking actual top-level macro
value/site/provenance Arc identity plus unchanged ancestor precedence. The
`macro-captures` case was added after discovery measurements; frozen-baseline and
candidate probes must both be rebuilt from the same final six-case harness.

## First paired candidate: discovery, not final acceptance

The first candidate is frozen executable/library SHA-256
`7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`
(executable hash). Its four component probes use the same current harness sources
and standalone rustc `-O --edition=2024` as the frozen old rlib. Compiler cases
were run serially baseline then candidate with one warmup, three timed samples,
and a separate untimed allocation pass. Object order was candidate then baseline;
graph order baseline then candidate. No Cargo builds or other benchmark workloads
ran in the accepted component window. Three samples are discovery evidence, not
confidence intervals or tail-latency estimates.

| Lowering case | N | Before median ms | Candidate median ms | Before allocations | Candidate allocations |
| --- | ---: | ---: | ---: | ---: | ---: |
| Flat file transfers | 1000 | 4.836 | 4.502 | 19,223 | 13,221 |
| Symbol/path calls | 1000 | 8.297 | 6.020 | 75,244 | 41,242 |
| Dynamic conditionals | 1000 | 463.997 | 16.454 | 534,082 | 31,495 |
| Symbol captures | 1000 | 675.311 | 74.852 | 3,335,630 | 180,701 |
| Macro captures | 1000 | 1410.386 | 67.278 | 3,359,870 | 181,701 |
| Read-only REPL with aliases | 1000 | 0.507 | 0.210 | 2,204 | 28 |
| Symbol captures: tiny control | 10 | 0.100 | 0.128 | 642 | 640 |
| Macro captures: tiny control | 10 | 0.114 | 0.113 | 708 | 650 |

Conditional allocator traffic fell from 723,563,754 to 12,567,798 bytes. Macro
capture traffic fell from 333,292,423 to 21,287,160 bytes. Tiny symbol captures
cost about 28 microseconds more in this run; neither the persistent library nor
this wave is a universal speedup claim. The large gains come from eliminating
repeated historical work and copy-on-write prefix duplication, not from making
every tree operation faster. The read-only REPL still traverses some compilation
metadata; constant allocation does not prove constant CPU time with history.

This candidate is deliberately **not accepted as the final release**: the root
runtime control-only workload has a stable disabled-instrumentation regression,
and dense graph/static-context cloning plus observation-state preparation remain
dominant. A second candidate addresses those measured residuals. These lowering
numbers describe the frozen first candidate, not an unmeasured later image.
