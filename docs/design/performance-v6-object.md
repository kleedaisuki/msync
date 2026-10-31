# v0.6 borrowed object analysis

## Workload and question

Typed control introduced recursive object scans. The initial implementation reused
mutable traversal for read-only slot/path discovery by cloning the entire region,
control, expression, or range first. A module containing many dynamic statements
therefore pays for transient copies even though analysis never changes its input.
The context binder's actual program clone remains necessary: contextualization
must produce an independent bound module without mutating its relocatable input.

This work removes only analysis copies. It does not change serialized objects,
validation limits, contextual path substitution, process/slot freshening, or debug
provenance. Parent instruction attribution for recursively discovered paths stays
unchanged: a path under a nested instruction remains visible to the enclosing
control's table entries exactly as before.

## Design references and decision

The [Rust compiler MIR visitor design](https://rustc-dev-guide.rust-lang.org/mir/visitor.html)
separates shared-reference `Visitor` from `MutVisitor`. Analysis should receive a
shared reference, while rewriting explicitly receives a mutable one. We follow
that distinction rather than introducing a visitor dependency or cloning an IR
just to satisfy a mutable signature. LLVM's
[instruction visitors](https://llvm.org/doxygen/classllvm_1_1InstVisitor.html)
also use statically dispatched traversal, matching this repository's monomorphized
Rust visitors; no runtime trait object is necessary.

The shared `hir::walk` module owns borrowed recursive traversal over instructions,
controls, scalar expressions and ranges. Its public `instructions(&Region)` helper
returns lexical preorder references, including expression-owned loop bodies;
execution/UI indexing can reuse this rather than scanning the complete IR for
each active task. Internal callbacks also let analyses avoid building a node
vector when they only need a fold. The existing mutable walk remains the rewriting owner. Read-only
slot collection, source-module dependency discovery, and object table derivation
use borrowed traversal. Identity collection can gather process and slot IDs in
one pass rather than flattening all instruction references first.

## Reproduction

`benches/object_v6.rs` uses public compiler/object/context APIs. Build the same
harness against baseline and candidate optimized libraries. It constructs static
transfers and dynamic macro calls containing `exists`, a local bool snapshot and
a conditional transfer, at 100, 1,000 and 5,000 source entries. Compilation is
outside timing. Each object phase has one warmup and seven independent samples;
report the median in microseconds. `with_source_modules` receives a fresh clone
created outside its timed window, and contextualization receives a fresh session.
There is no runtime filesystem operation, process launch or transport in samples.

A separate untimed sample counts allocation calls and requested allocation bytes;
tracking is disabled during timed samples. Bytes are cumulative requested sizes
(including reallocations), not resident memory or peak live bytes. Output is one
JSON line per fixture/phase with a caller-supplied baseline/candidate label.
Disposable build products and raw measurements belong under `.temp`.

Filter fixtures with comma-separated `MSYNC_OBJECT_SIZES` (default
`100,1000,5000`), `MSYNC_OBJECT_KINDS` (default `static,dynamic`) and
`MSYNC_OBJECT_PHASES` (default `validate,source_modules,contextualize`). Begin
with `MSYNC_OBJECT_SIZES=100,1000` and three samples to discriminate allocation
costs inexpensively; only deepen to 5,000 after checking memory and runtime.
Static contextualization includes planner pair proofs, so large fixtures can
measure unrelated quadratic graph construction as well as object analysis.

## Results

The root froze the baseline release binary/library before production edits, on
Linux x86-64 (kernel 6.12.113), Rust/Cargo 1.99.1, optimized profile. Source commit
was `478cd9a476b50c1b5d9b19b61ed41f78f4248e0a` plus the frozen v0.6 working
changes; the complete source archive and hash are retained with baseline artifacts.
The baseline object harness SHA-256 is
`b6afaaec2b39ac83e15763f0da279dece633f551d622ce9f6af6fd2830c6cbea`.

The discriminating baseline uses 20/50/100 entries, three timed samples after
warmup, both fixture kinds and all three phases, serialized without competing
builds or probes. Raw rows are `.temp/performance-v6/object-before.jsonl`; any
`*-discarded-overlap` or interrupted rows are excluded. The smaller sizes are
intentional: static contextualization constructs dense planner proof output,
which is outside this borrowed-analysis optimization and dominates larger inputs.
At 100 static entries it requested 416,795,329 cumulative allocation bytes; blindly
running 1,000/5,000 would mostly magnify that separate graph workload.

| Kind | Entries | Validate (ms) | Source modules (ms) | Contextualize (ms) |
| --- | ---: | ---: | ---: | ---: |

| static | 20 | 0.0310 | 0.0322 | 11.6062 |
| static | 50 | 0.0769 | 0.1160 | 74.6361 |
| static | 100 | 0.1668 | 0.1886 | 211.6883 |
| dynamic | 20 | 0.0929 | 0.1037 | 0.2451 |
| dynamic | 50 | 0.2896 | 0.2680 | 0.4786 |
| dynamic | 100 | 0.6675 | 0.7937 | 1.1549 |

At 100 dynamic entries, baseline allocation counts are 4,846 validation, 5,553
source-module completion, and 9,180 contextualization; requested bytes are
586,465, 757,717 and 1,363,813 respectively. These are suitable causal allocation
controls for removing transient IR clones. Candidate latency and allocation rows
remain pending; baseline numbers alone are not a speedup claim.

## First paired candidate (7fc6ff1): bounded discovery

The same-source probes linked to frozen old and provisional optimized libraries
ran serially with 3 timed samples after warmup. Candidate ran before baseline
for this component; no concurrent benchmark or Cargo workload was admitted.
Files are `object-paired-{before,optimized}.jsonl` under repository `.temp`.

| Dynamic module, 100 entries | Before us | Candidate us | Before allocations | Candidate allocations | Before requested bytes | Candidate requested bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Validate | 546.818 | 490.421 | 4,846 | 4,346 | 586,465 | 481,485 |
| Add source modules | 597.494 | 489.926 | 5,553 | 4,352 | 757,717 | 485,517 |
| Contextualize | 1025.088 | 945.784 | 9,180 | 8,171 | 1,363,813 | 1,107,293 |

The allocation reduction directly supports eliminating scan-only IR duplication.
Small timing changes from three samples are not a tail-latency or universal
speedup claim. Static module validation still performs the same work and has the
same allocation counts. Static 100-entry contextualization measured
245.780 -> 218.520 ms, but requested allocation bytes were essentially unchanged
(416,795,329 -> 416,906,935), so this is not evidence that dense graph construction
has been solved by a borrowed visitor. That residual motivates the separately
measured private normalized-path sharing work. This first candidate also has an
unrelated runtime control-only regression and is not the final accepted image.

## Second candidate: normalized-path sharing

After the state/path repair gates, the second frozen library accompanies
executable SHA-256
`015645dc073986de9743c082ade38c6361cbd385ce754a8fed261065358a6b97`.
The unchanged object harness was rebuilt with the same standalone rustc flags.
The bounded static 100-entry case ran candidate then baseline, with one warmup,
three timed samples and a separate allocation pass. Both processes exited
successfully before any subsequent build; data are
`object-wave2{,-before}.jsonl`.

| Static module, 100 entries | Before | Second candidate |
| --- | ---: | ---: |
| Contextualize median ms | 223.073 | 98.450 |
| Allocation calls | 3,251,969 | 729,143 |
| Requested allocation bytes | 416,795,329 | 285,884,145 |
| Validate median us | 157.802 | 159.052 |
| Source-module scan median us | 164.109 | 158.584 |

This closes part of the dense dependency-reason copying cost rather than
pretending object validation itself became substantially faster. The normalized
path substructure is immutable and shared; public owned locations, required
complete proofs and validation remain. No whole-FileResource O(1)-clone claim is
made, because public location strings remain owned. Three samples establish this
bounded diagnostic comparison, not a confidence interval or a universal startup
improvement. Whole-image release acceptance remains with the integrating owner,
who is separately repairing a runtime-only control-path regression; these rows
measure the frozen second library, not an unmeasured later image.
