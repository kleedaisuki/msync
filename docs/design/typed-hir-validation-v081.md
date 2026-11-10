# Typed HIR acceptance and compiler cost

## Objective and baseline

The 0.8.1 change separates semantic analysis from bytecode generation. A dump
of already generated instructions, an AST wrapper, or an adapter retaining the
old emitter as a fallback does not satisfy that boundary. The published 0.8.0
tag `637bd091e62e013f4003a1fa7e2c8f0b5e917140` supplies the behavior and cost
baseline. Its release binaries remain immutable; candidate measurements record
their own exact images and source state.

The executable language contract remains the current one: mutable locations with
fixed storage constraints, shared captured cells and Scope identities, explicit
Null admission, on-demand Range arguments, eager explicit snapshots, concrete
generic callable identities, and selected-only external effects. Released format
4 `.mso` and `.mexe` inputs remain supported independently from a new HIR file
format. REPL and editor clients continue using the same semantic implementation.

## Architectural evidence

Acceptance inspects both the representation and actual consumers. Source-name
checks alone are not proof: replacing `Instruction` with `HirInstruction` while
retaining stack positions and patched jumps is still the same architecture.

| Requirement | Discriminating evidence |
| --- | --- |
| Semantic analysis owns meaning, not execution layout | Its result contains declaration/location identities, expression types, structural control flow, callable instances, capture modes and source identities. Semantic binding does not emit opcodes, allocate bytecode slots or patch branch offsets. |
| Bytecode lowering is a one-way consumer | Lowering consumes the finalized semantic model without syntax AST, `Info`, mutable inference frames or callbacks into binding. It assigns stack/local/capture layout and branch targets itself. |
| Export is a genuine semantic boundary | Write `.mhir`, delete the original source, read the file in a fresh process and lower it. The codec must not carry an embedded `.mso`, instruction tape, or source that is rebound to recreate the result. |
| Generic queries do not mutate committed semantics | Repeated successful and failing invocation/member queries leave binding constraints, instance identities and future writes unchanged. A read-only semantic query uses substitution-local state rather than cloning the live compiler graph and rolling back emitted code. |
| Delayed evaluation is explicit | HIR distinguishes value capture from location capture and records the observation boundary. Earlier non-null facts are not reused after a possible write; private unwritten locations retain valid refinements. |
| Finalized semantics are stable | Lower the same semantic module twice and compare normalized bytecode/artifacts. Inspecting or exporting a module cannot change the next lowering result. |
| Runtime stays source-free | Execution of lowered `.mso`/`.mexe` requires neither original source nor a HIR binder. HIR loading, checking and viewing perform no I/O effects or authentication. |
| Editor analysis is semantic-only | Hover, completion, symbols and diagnostics consume the semantic result without invoking bytecode lowering. Incomplete-source recovery preserves useful identities and source ownership. |

Architecture checks follow actual dependency directions and APIs, not arbitrary
text bans. A semantic operation may legitimately consult shared type/capability
metadata; the forbidden dependency is generating or repairing executable layout
while deciding meaning. Persistent-session commit/rollback is a separate
transaction boundary, not permission to retain speculative emission snapshots.

The post-cutover source inspection establishes the concrete boundary:
`Msync.Hir` references only Core, and its immutable module contains structured
nodes and typed identities, not compiler frames, syntax ASTs or instructions.
`SourceBinder` binds lexical bodies into that semantic vocabulary; the analyzer
does not construct bytecode operations, local-slot layouts or patched jumps.
`Compiler.Compile` calls Analyze → pure Lower (without retained layout) →
Optimizer. The lowerer alone translates the immutable module into bytecode.
EditorCompiler instead consumes SemanticEditorCompiler's lightweight observation
view and never invokes that lowerer.

Query isolation is enforced before sealing: `SemanticOutput` owns executable
table append/reservation, while the query's `SemanticArena` exposes read-only
tables and metadata interning. Query contexts have no output capability.
Ordinary branch flow maps, bounded generic workspaces and session transactions
remain distinct mechanisms; their existence is not evidence of the old emitting
compiler snapshot architecture. These source findings complement, rather than
replace, the query-ownership, editor-lifetime and source-free artifact journeys.

## Real user journeys

The focused `tests/Msync.EndToEnd/hir_semantic_journey.py` composes the previously
fragile boundaries in one ordinary program. It records real file bytes through
source, relocatable bytecode and context-bound executable bytecode at O0/O2/O3:

* guarded nullable locals and subsequent admitted Null writes;
* delayed argument/body execution exactly once;
* callee evaluation before a mutating argument;
* nested higher-order specialization and true recursive calls;
* uncalled generic values and deferred body checking, with errors when selected;
* late-written captured locations inside returned Ranges;
* original Scope aliases selected through indexed storage;
* independent empty/snapshot carriers and present-Null Option values;
* named singleton versus anonymous factory identity;
* same-shape arguments projected into independent results and closure captures;
* unevaluated metadata, including failed-query constraint isolation.

The source-free routes delete their source before execution; the executable route
also changes launch cwd. Checking, compiling and binding must create none of the
program's output files. Invalid fixed-type writes, unhandled Null and invalidated
member refinements remain errors rather than being made executable by a new IR.

The artifact owner additionally verifies `.mhir` round trips, deterministic
exports, source attribution and malformed-file diagnostics. The experience owner
verifies actual LSP, redirected/pseudo-terminal REPL, selected remote completion
and the production VS Code host. The existing stdin-gated Process tests verify
ownership across source functions, aggregates and REPL submissions; they are
reused for this changed boundary rather than replaced with fake completed values.

## Time and memory observations

Compiler measurements separate cold CLI launch from warm component work. Before
the new API exists, baseline compilation is measured as one actual operation;
there are no invented old-stage semantic/lowering timings. The candidate records
semantic analysis, finalized-HIR lowering, bytecode optimization and optional
HIR serialization separately, plus the total user-facing check/compile command.

The workload set is small and deliberate: no standard library, Bool, shell,
List, type metadata and a small tree program. It exercises both ordinary scalar
scripts and the rich generic standard library without forcing every check to
load a complete standard namespace. Full-module escape is a separate explicit
case if a measured question requires it.

Reports retain SDK/OS, exact Native image or managed assembly hashes, source
inputs, warm-up/calibration policy, raw observations and allocated bytes. Managed
before/after pairs use the same execution policy and are not described as Native
latency. Native AOT and actual terminal/editor observations establish the shipped
path. There are no CI timing thresholds, full-suite quality gates or benchmark
scores standing in for correctness.

Allocated bytes and retained state answer different questions. The separate
retention probe holds the returned module through a collection, releases it,
and verifies reachability with a weak reference; an instrumented run observes
managed heap peaks independently from timing. Native process lifetime RSS is
recorded separately. Candidate normal compilation, retained analysis/editor
results and explicit export need separate lifetime observations.

This follows a concrete lesson from
[Rust THIR](https://rustc-dev-guide.rust-lang.org/thir.html): body-indexed typed
expressions need not keep every completed body alive until compilation ends.
The semantic-query boundary also follows
[Roslyn's immutable compilation/symbol model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-semantics):
meaning and source identity remain inspectable independently of emitted runtime
layout. These are mechanism references, not requirements to reproduce either
compiler's entire pipeline or memory management strategy.

## Default-pipeline integration observations

The immutable `hir-editor-cli/frozen8` snapshot uses the new default
`Compiler.Compile` pipeline rather than an independently invoked analysis side
path. Compiler SHA-256 is
`0ac431334e98d6dec5255b358dda24e5c5aae0bf9153c72c03e958ce00c63993`;
the full six-assembly receipt accompanies the snapshot.

The semantic journey passed three O2 source/object/executable routes, **84 exact
file observations**, six invalid-program diagnostics, and absence of the query's
forbidden output. This includes the two same-shape wrapper/capture projection
witnesses, delayed execution once, late-written captured cells, and a failed
invocation query whose temporary concept obligations do not leak into later
real writes. Its report is `.temp/hir-semantic-journey/snapshot8.json`.

Earlier snapshots exposed distinct real defects rather than fixture timeouts:
nullable storage admission was omitted during assignment; Range-returning calls
executed eagerly; a late captured cell was modeled as its initial Null and then
as an unnecessarily broad domain; standard-module closures retained a replaced
initialization cell; and a query omitted a required callable-write obligation.
The small independent reproducers were used before restoring the composed
journey. Expectations and standard-library code were not changed to mask them.

These observations are not final Native or full standard-library acceptance.
The small B-tree cost input still stops at a mixed native/source call-domain
diagnostic in this snapshot, so it has no candidate timing or speedup claim.
Further measurements use a corrected producer after an actual execution result,
not merely a successful compilation. Default-pipeline proof does not by itself
establish every capability/retention boundary in the architectural matrix.

The following frozen9 snapshot (`471dee5698a4d8d64c3d7bfb36e8afbdcde10c0a3f8db71ff25780a5eae0089c`
Compiler SHA-256) passed that diagnostic boundary but exposed semantic-analysis
growth instead. A stage-marked host containing just `BTree::new`, **one** direct
`BTree::insert`, and `BTree::size` printed `analyze-start` but did not reach the
analysis result, lowering or optimization. At the last observation its own
process had elapsed 2 minutes 19 seconds, accumulated 2 minutes 34 seconds CPU,
and retained 9,859,396 KiB RSS. The process was terminated rather than risking
the shared development host; two/four-insert and warm measurements were not run.
The source, process observation and exact assembly hashes are under
`.temp/hir-stage-trace/`. This is a semantic-instantiation resource-growth defect,
not a completed cost measurement or an optimizer/runtime regression. Counts
available only on a completed HIR result cannot be inferred from this run.

A later source-version check matters here: successful analysis of an insert-only
mirror does not imply completion of the actual output-producing input. The
165-byte one-insert input also calls `BTree::size`, casts its result to String
and transfers it to `result.txt`; its SHA-256 is
`1c02eee6f28d1454c1f72e10e5b8dc56b0a10b8da42816ffb442d8e821e44fcb`.
On frozen13 (Compiler `9af6c5241cb9fd4221b2a8e812dca9e8581c93058e4595b90a91ec2125884844`),
a single direct-reference `--trace-once` observation prints only Analyze/start,
not Analyze/end. Its externally bounded five-second observation therefore
localizes the remaining growth before bytecode lowering or optimization. The
record is `.temp/hir-stage-trace/frozen13-exact1-stage.*`; it is not a completed
performance sample. Subsequent causal traces must use that exact source rather
than silently reverting to the shorter insert-only input.

## Tree integration after semantic growth corrections

Frozen14 (Compiler SHA-256
`1829c7cc29d0a63df96b71db8637f91ef514d068dc56308ec3559967a73565c4`)
completes the actual default Compile → `.mso` execution boundary. The exact
one-insert input above produces the single byte `1`, and the original 24-insert
and lookup program produces exactly `17`. Compilation itself creates neither
result file. Both executions use the compiled artifact rather than source replay.

After those functional results, a single phase trace of the 24-insert source
(`4570a1401bd98796088c1134674fe69d37aba0a2e04312b0959bf7c545328e60`)
completes Analyze, Lower and O2 optimization:

| Boundary | Completed result |
| --- | --- |
| Analyze | 38 functions, 50 generic templates, 4,193 nodes, 1,620 locations, 1,198 types, 38 Scope layouts |
| Lower | 88 bytecode functions, 2,926 instructions |
| O2 | 88 bytecode functions, 1,824 instructions |

The single cold managed trace allocates approximately 145.7 MB in Analyze,
0.45 MB in Lower and 22.3 MB in optimization. Its observed process RSS is
99,520 KiB; this is not a managed-heap retention measurement. Cold stage times
include JIT and initialization and cannot be compared with the recorded old
warm component times. No warm matrix or Native latency claim follows from this
functional gate. Exact frozen receipts and raw stage records are under
`.temp/hir-stage-trace/frozen14-*`; the completed runtime outputs establish that
subsequent cost measurements would concern functioning tree programs.

## Same-source allocation regression and one diagnostic trace

The tree above is **not** the original performance input. Its loop has one
static insert call site; the published baseline probe instead unrolls 24 calls,
uses `"item-N"` values and returns the nullable lookup result. That original
source has SHA-256
`3fc8333a4e8003c83495625dd59af655746af2bfb03075c10e040bebcc3626cb`.
Its old and new function counts cannot be compared against the loop's counts.
The unchanged no-stdlib/List/unrolled-tree inputs and exact image identities
are recorded in `tests/Performance/HirCompilation/README.md`.

Frozen14 does finish the original unrolled input, but its first Analyze
allocates 1,478,691,760 B, before 716,840 B of lowering. The saved old first
complete O0 Compile allocates 150,975,304 B. This approximately 9.79-fold
construction-traffic signal is not explained by comparing cold to warm work,
nor excused by a smaller residual program. Warm/retention batches were withheld
while its cause was investigated.

One `dotnet-trace` 10.0.750501 `gc-verbose` collection then runs that same
frozen14 input once, with `DOTNET_TieredCompilation=0`, a 64 MiB EventPipe buffer,
O0 and the one-pass phase host. There is no product instrumentation or new
profiler. The offline reader uses the tool's existing TraceEvent library and
weights `Microsoft-Windows-DotNETRuntime` GCAllocationTick samples by
`AllocationAmount64`. This is sampled attribution, not a per-object census or
retained-heap measurement; see the upstream
[diagnostic profile documentation](https://github.com/dotnet/diagnostics/blob/main/documentation/dotnet-trace-instructions.md).

The recording contains 13,481 allocation ticks, each with a recorded stack,
and 1,484,342,248 B of allocation weights. The observed Analyze allocation is
1,478,846,256 B with the same finalized graph counts. Assigning each tick to
its nearest compiler frame produces disjoint groups:

| Nearest compiler owner | Sample allocation weight, B |
| --- | ---: |
| FormalInput and its generated delegates | 864,857,680 |
| EnrichFutureDomains and its generated delegates | 332,282,392 |
| InvocationProjection | 174,674,216 |
| Other frames or no compiler owner | 112,527,960 |

The largest repeated stack is recursive `FormalInput.Abstract` through
`ImmutableArray.CreateRange` and LINQ Select/ToArray (530,936,712 B). A parallel
`EnrichFutureDomains.Visit` stack accounts for 177,453,776 B. `SemanticValue`
accounts for about 624.8 MB of type-attributed weights, String for 187.1 MB;
closure, delegate and iterator objects are also substantial. The CodeSchema
ancestor covers about 632.9 MB of the FormalInput work, **not an additional
disjoint amount**.

These stacks target repeated semantic graph transformation and role-path string
construction, not HIR sealing or bytecode optimization. Appropriate corrections
preserve graph sharing, avoid reconstructing unchanged scalar/array/callable
records and reuse call-local canonical views. They must not cache a first
caller's semantic environment or erase alias/carrier distinctions. A corrected
producer still needs its actual semantic result and the final matched cost
observation; this diagnostic is not a speedup claim.

The raw 8,288,346-byte trace has SHA-256
`588e72d73fad58edd96f698ca61587b1925ee3e529744c955e90a3b929817f0b`.
The exact command, producer receipt, parsed allocation stacks and offline reader
are under `.temp/hir-final-cost/allocation-trace/`. The original trace is parsed
multiple times without rerunning the product.

Frozen15 (Compiler `b890eef66fd87f886674933bb7afacbdc297ca8e2cfae1ccb058aaafe2efb26d`)
then receives one same-source, same-SDK, TieredCompilation=0 first-process
phase observation, without another GC trace or warm batch. Analyze allocates
941,771,288 B, down 36.3% from frozen14; all finalized HIR counts and the
716,840 B lowering allocation remain unchanged. This producer combines the
call-local FormalInput memo/no-op reuse with closed-callable checking and
current-fact corrections, but excludes the later EnrichFutureDomains work.
The total candidate change is therefore not attributed to one isolated hunk.
It remains substantially above the old first complete O0 Compile's 151 MB, so
matched final costs and publication are not inferred from this partial reduction.
The raw observation is `.temp/hir-final-cost/frozen15-original-tree-trace-receipt.*`.

The following combined performance attempt, frozen16, did not complete Analyze
within the externally observed interval, so it has no final allocation/count
result. It is not part of an improving numerical series. The corrected frozen17
producer (Compiler
`bb4456050510f8d81411f5c925bb5cf7c4cddd0aaff732b2cf9f1d07b0ecdfc0`)
removes the historical SemanticValue Join tree while retaining actual type and
semantic facets. Its one same-source cold observation completes with
143,736,064 B in Analyze, 606,632 B in Lower and 531,600 B in O0 optimization.
It contains 49 finalized functions, 49 templates, 5,156 nodes, 1,967 locations,
1,258 types and 39 layouts; lowering produces 98 functions/4,331 instructions.
The observed process RSS is 83,140 KiB, not retained heap size. This restores a
completed allocation control near the old first full O0 Compile's 151 MB;
the normal complete compiler's matched warm time, O2 allocation and retention
remain separate required observations. Raw records are
`.temp/hir-final-cost/frozen17-original-tree-trace-receipt.*`.

The graph-representation change is also checked through actual source execution
on frozen17: one O2 run of the existing composed program produces all 28 exact
outputs and no forbidden query effect. It includes independent Scope/capture
projection, shared Buffer object identity, first-write/snapshot behavior,
nullable flow, Some(null), delayed execution and query isolation. Three changed-
boundary negative cases still reject an unhandled Null, a member proof
invalidated through an alias, and a deferred Range/String source that changes
before observation. Only this source route is repeated, not the earlier three-
artifact or optimization matrix; its report is
`.temp/hir-semantic-journey/snapshot17-source.json`.

## Matched compiler cost and the remaining List construction work

The completed three-source O0/O2 cost-control observations and independent
module-retention results are recorded in
`tests/Performance/HirCompilation/README.md` and
`.temp/hir-final-cost/frozen17/matched-{timing,retention}.json`. Their source
bytes, SDK/runtime, TieredCompilation=0 policy, warmups and sample counts match.
They establish a large BTree improvement but also a real List regression:
O0 allocation grows from about 0.928 MB to 3.816 MB, and O2 from 4.432 MB to
7.375 MB. The BTree benefit does not cancel an independent ordinary-case cost.

A focused reuse of `SemanticAllocationProbe` on the same List source separates
554,368 B of binding from 3,102,016 B of semantic work. Public Analyze agrees
with the split to 24 B. Re-freezing and sealing cost only 42,376 B and 46,088 B;
the final result still has 623 nodes, six functions and 34 templates. Binding
and the final arena sizes are essentially unchanged from the earlier control,
while semantic construction has grown.

One allocation-only `gc-verbose` collection of the existing normal-Compile
probe then uses O0, one cold call, three warmups and 50 calls to obtain enough
ticks. These repetitions are a sampling mechanism, **not** another timing
measurement. The 1,970 recorded ticks all have stacks and carry 213,281,232 B
of allocation weights. The nearest compiler owners include FormalInput
(56.63 MB), EnrichFutureDomains (20.99 MB), and InvocationProjection (2.99 MB).
The largest individual frames are FormalInput.Abstract (36.58 MB),
EnrichFutureDomains.Visit (15.34 MB), FormalInput.AbstractCallable (7.25 MB)
and FutureCallables (7.24 MB). CodeSchema's inclusive 27.91 MB and Enrich's
inclusive 43.62 MB overlap their descendant frames and must not be added to
those nearest-owner totals.

This narrows the short List's remaining cost to formal class/callable schema
construction and future-callable enrichment rather than HIR sealing or the
bytecode optimizer. Sharing fixes still need the actual carrier/alias/capture
contract; a first-caller semantic cache is not a valid shortcut. Source/image
receipts, the phase results and parsed stacks are under
`.temp/hir-final-cost/list-attribution/`; the only raw trace has SHA-256
`df8a0ba4a29c3d66b36c7fe55addb3ec6147e08e51849b017dcd9120a176c76e`.

Frozen18's bounded List fast paths receive one cold phase observation and one
same-source frozen17 cold control, not a new benchmark batch. Analyze changes
from 7,689,152 B to 7,399,576 B (−3.77%); Lower stays at 80,160 B and O0
optimization at 68,936 B, for a total reduction of 289,576 B (−3.69%). The
two extra type entries aside, finalized function/node/location counts and
bytecode counts are unchanged. These first-process totals include initial
standard-source parsing and are not compared with the warmed friend probe.
This is a small improvement, not proof that the earlier warmed List regression
has disappeared. Further microprofiles are not warranted by this result alone;
the final corrected Native image still needs a targeted default-O2 user-latency
observation. Raw records are `frozen17-list-cold-control.*` and
`frozen18-list-trace-receipt.*` under `.temp/hir-final-cost/`.
