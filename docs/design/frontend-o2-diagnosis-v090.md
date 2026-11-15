# Frontend and O2 cost diagnosis at 9703b23

## Scope and reproduction

The user reported slow O2 compilation. This investigation uses the actual
`btree_bplus_journey.tree_program` and `heap_journey.property_program` fixtures,
not the smaller application benchmark fixtures. Product sources were unchanged
for the baseline measurements; the implementation follow-up below describes
the subsequent worktree changes.
Existing findings in [the benchmark investigation](benchmark-telemetry-9703b23.md)
remain useful Native AOT context; these new observations are **Release managed
.NET diagnostic runs**, not a paired comparison against that image.

Build: `dotnet build src/Msync.Cli -c Release --no-restore -m:12`.
CLI: `--no-config --trace FILE --telemetry-level profile compile SOURCE ...`.
Each process starts fresh. Diagnostic commands ran sequentially without CPU
affinity. Timings below are single observations, not stable benchmark estimates.
The BTree HIR-only probe has a 30-second diagnostic timeout and was terminated
by its supervisor, not reported as a completed compilation.

Evidence is retained in `artifacts/diagnostics/frontend-o2-9703b23/`: generated
source, original JSONL, process results, stack snapshots, image hashes, build log,
and typed reproduction/summary scripts. Scripts locate the repository through
`AGENTS.md`; their working outputs stay next to the script. The retained scripts
use `.mhir`/`.mso` output suffixes so replay enters the artifact reader. Original
probes used `.out`; an initial Heap replay consequently entered the source parser
and failed. Renaming the same bytes to `.mso` fixed that harness error.

## Observations

Inclusive compiler phases, seconds; nested intervals must not be added to parents:

| Fixture / route | Analyze | Native lower | Optimize | CLI wall |
| --- | ---: | ---: | ---: | ---: |
| B+ / typed HIR | 10.77 | — | — | 11.45 |
| B+ / O0 | 10.88 | 6.62 | 3.49 | 21.87 |
| B+ / O2 | 10.77 | 6.23 | 14.85 | 32.82 |
| Heap / O2 | 8.41 | 4.00 | 11.33 | 24.44 |
| BTree / typed HIR | unfinished | not entered | not entered | timeout 30.12 |

A separate **telemetry-off B+ O2 compile takes 29.70 s**, succeeds, and its
artifact replays in 7.84 s with every generated file oracle satisfied. The Heap
profile-produced artifact also replays successfully with all 18 file oracles.
Thus recording is not the origin of the delay, and these compiler invocations
cannot be explained as guest VM/JIT execution time.

### 1. BTree is already expensive before O2

The HIR-only timeout completes binding (339 ms) but never completes semantic
analysis or enters native lowering/optimization. Completed instantiate spans
before termination include 2,386 query invocations (465 misses / 1,921 hits),
plus 514 publish invocations. B+ completes with 128 query invocations.
These are counts of completed spans, not a complete BTree phase census.

A managed stack snapshot is in
`btree-hir.stack.txt`. Its active path is `SourceReturnShape -> ProjectReturn`,
reached through `ProjectCallShape/CallShape` during recursive instantiation.
This is independent of the previously reported Set witness-resolution crash.

`SemanticAnalyzer.CallPolicy.cs:146–154` constructs the return-scheme key before
looking up `returnSchemes`: arguments and captures each contribute recursive
`ValueKey` and `PolicyKey` strings. A cache hit therefore does not avoid graph
fingerprinting. `PolicyKey` sorts every reached scope's fields; its active set
cuts cycles but is not a completed-node memo for shared DAG subgraphs. On misses,
the path forks context, prepares flow and projects the source body again.

This identifies the query/projection path as the next target, not proof that
string-key construction alone consumes all BTree time. Instrument key creation,
return-scheme hits/misses, body projections, context forks and graph visits
separately before replacing the key. Preserve eagerness, deferred-read policy,
Boolean/type denotations, aliases and context mutation boundaries.

### 2. Native lowering performs repeated global location scans

`NativeHirLowerer.cs:209–211`, `FunctionLocations`, enumerates the entire module's
location table to select one owner's locations. `FunctionLowering` enumerates it
in `PrepareLocations` and again for ordinary non-session import initialization.
B+ has 1,146 HIR functions and 38,546 locations, so those two enumerations alone
inspect about **88.35 million table entries**. This is structurally O(F × L)
selection work before emitting the selected locations.

Build an owner-indexed location list once per immutable lowering input, retaining
original location order and the `TemplateOwner is null` rule. This changes
selection to O(L + selected locations), without changing slot numbering, alias
identity or persistent-session mappings. The 6.23-second lower interval is not
an attribution of all lower time to this scan; type/layout and emission work
remain inside it.

### 3. O2 amplifies work through repeated analyses and allocation

B+ enters optimization with 1,029 native functions and 60,953 instructions.
The function optimizer reports 2,881 total fixed-point iterations across those
functions and 2,136 explicit program-facts invalidation calls, not a single
function looping 2,881 times. It reduces the program to 31,034 instructions.

| B+ explicit pass | Inclusive seconds | Invocations |
| --- | ---: | ---: |
| Escape analysis | 4.003 | 2,881 |
| Inlining | 3.135 | 4,116 |
| Local values | 2.622 | 2,881 |
| Global memory | 1.217 | 2,881 |
| Dead results | 0.824 | 2,881 |

Analysis allocates 1.393 GB and optimization 4.027 GB cumulatively in their
recorded thread-local intervals; these are allocation traffic, **not peak RSS**.
The B+ optimizer stack snapshot catches
`NativeEscapeAnalysis -> ProgramFacts.SolveLocked -> NativeAddressAnalysis.Step
-> Observe -> NativeValueFlow` construction and `State.Copy`.

`ProgramFacts` sharing and reverse-caller invalidation already exist. On
invalidation, affected function analyses and transitive callers are removed;
their reaching-definition/value-flow graphs are then reconstructed. The function
loop reconstructs CFG/escape/local/liveness work after changes. `NativeValueFlow`
itself scans all blocks to a fixed point and clones slot-state arrays; liveness
uses per-instruction hash sets. This explains why the next useful optimization
is preserved-analysis reuse and representation/worklist cost, not adding another
global facts cache or simply parallelizing the loop over mutable shared state.

### 4. `check` pays for all of this

`NativeCommandPipeline.Compile` calls the complete compiler, and `Run` only tests
`options.Command == "check"` after receiving a compiled NativeProgram
(`NativeCommandPipeline.cs:95–103`). Therefore repeated source `check`, `compile`
and source `run` commands each enter compilation. HIR-only compilation is the
existing diagnostic boundary that excludes Native optimization.

Changing `check` to semantic-only work needs an explicit decision about native
lowering diagnostics currently surfaced there; do not silently remove those
diagnostics as a performance fix.

## Recommended order

1. Resolve BTree query/projection amplification and the separately known Set
   recursive witness context bug; changing O2 cannot repair either frontend path.
2. Replace per-function global location scans with one owner index. This is the
   simplest concrete complexity reduction, preserving existing ordering.
3. Measure analysis rebuild causes and preserve facts across rewrites that do not
   change their premises; separate reaching-definition/CFG identity from
   interprocedural summary invalidation where sound. Keep telemetry for actual
   hits/rebuilds, changed-function and caller counts, solver transfers and copies.
4. Revisit `check` ownership after its diagnostic contract is explicit.

This follows the [LLVM analysis preservation/invalidation model](https://llvm.org/docs/NewPassManager.html),
which also warns against repeatedly computing outer-scope analyses inside inner
passes. Recent [compiler-statistics-guided phase-ordering research](https://eprints.whiterose.ac.uk/id/eprint/224241/)
is relevant to deciding which passes earn their cost, but auto-tuning pass order
is not the first intervention for these identified graph/scan/rebuild costs.

No product repair or speedup is claimed by this diagnostic task.

## Follow-up: upstream expansion, late sharing and convergence

Further attribution reuses the completed recordings, without repeating expensive
probes. `artifacts/diagnostics/frontend-o2-9703b23/attribute.py` produces
`attribution.json` with grouped function costs, fold outcomes and iteration
histograms. The deeper explanation is multiplicative: **generated code volume ×
work per function × cost per analysis round**, with a separate return-policy
query path that can become expensive before O2 is entered.

### Data elements become independent function bodies

Of B+'s 1,146 lowered HIR functions, **745 are `<range-item>` thunks**, matching
the 745 literal mutation records in the journey. They account for 37,995 of the
75,282 instructions in the per-HIR-function, pre-fold census and **4.598 s** of
the **6.228 s** lower interval (73.8%). They then contribute another **1.962 s**
and **956.9 MB** cumulative allocation in `optimizer.function` intervals.
Those optimizer intervals are part of, not additional to, the reported 14.854 s.
The post-fold lower census (60,953 instructions) is a different boundary and must
not be used as the denominator for the pre-fold thunk instruction count.

Mechanism:

- `SourceBinder.Expressions.cs`, `RangeValue`, classifies anything not accepted
  by syntactic `PureValue` as deferred; Scope construction is not accepted.
- `SemanticAnalyzer.Aggregates.cs`, `RangeThunk`, reserves and seals a separate
  function, allocates its internal locations, and constructs a closure for each
  deferred element.
- Large literal record data becomes many individually compiled constructor
  bodies, magnifying global location scans and all per-function analyses.

Laziness and fresh Scope identity are real language contracts; replacing these
items with eagerly preconstructed shared scopes would be incorrect. The design
candidate is shared typed constructor code plus per-element data/descriptor,
still invoked on demand and still producing fresh storage, rather than unique
code for every literal payload. Evaluate real data-heavy workloads and preserve
artifact/lifetime contracts, rather than adding a collection-specific runtime
shortcut. This is not a claim that every thunk can be removed.

### Equivalent code is recognized after earlier costs have been paid

The first native identical-function fold reduces B+'s function table from
**1,240 to 1,029** (211 eliminated), and Heap's from **631 to 338** (293
eliminated, 46.4%). These tables include template entries, unlike the HIR-function
counts. Before this fold, Heap has already lowered **71 `_heap_value`** functions;
12 remain under that name at the function-optimizer boundary.

Native type canonicalization reduces B+'s 421 native type entries to 98 and
Heap's 416 to 98. Logical semantic types need not be identical merely because
their native ABI is equivalent. These observations identify an opportunity to
separate code identity from semantic/provenance identity earlier; they do not
justify merging caller aliases, effects or type witnesses. Post-lowering folding
saves later work but cannot refund already repeated inference and lowering.

### The fixed-point loop is not running away

The B+ per-function iteration histogram is:

| Rounds | Functions |
| --- | ---: |
| 1 | 1 |
| 2 | 23 |
| 3 | 842 |
| 4 | 37 |
| 5 | 32 |

There are 935 nonempty/non-template optimized functions, average **3.08 rounds**,
maximum **5**, not an extreme convergence tail. Heap's initial optimization also
has maximum 5 rounds (267 functions / 864 rounds); additional post-inline work
accounts for its remaining explicit pass invocations. Capping iteration counts
is not supported by these observations. The main issue is the volume and cost
of work being repeated even a small number of times.

### Return-policy inference overlaps ordinary semantic inference

`SemanticAnalyzer.Callables.cs`, `Call`, first asks `HasImmediateResult` whether
normal call evaluation is known to be valid. If that proof fails, `CallShape`
projects return/eagerness policy before ordinary callee/argument analysis and
`Invoke/Instantiate`. `HasImmediateResult` already caches syntactic template
proofs; this is not a recommendation to add that cache again.

The expensive fallback can run source projection and flow preparation to answer
the policy question, and normal inference subsequently analyzes the invocation.
A reusable summary should expose type/eagerness/deferred policy along with
formal effects, avoiding broadly overlapping source walks at different entry
points. The current query/publish separation prevents speculative effects from
leaking into execution; retain that contract while sharing inference results.
The BTree stack and query counts identify this as a concrete target, but current
telemetry does not isolate all return-policy time.

### Revised repair framing

For BTree, start at return-policy/query amplification, not Native O2. For
data-heavy B+, address thunk/code expansion and the owner-location index together.
For Heap, earlier code sharing and costly per-round analyses both matter. Then
refine pass invalidation and hash-set/slot-state representation using actual work
counts. Keep `check`'s full pipeline cost separate from a single compilation.

The [LLVM analysis-manager design](https://releases.llvm.org/22.1.0/docs/NewPassManager.html)
addresses the same class of scope/recomputation hazards. Research on
[reusing compiled code across contexts](https://www.cse.iitb.ac.in/~manas/docs/preprints/oopsla23.pdf)
is relevant to the distinction between code and contextual assumptions; adopting
that distinction here must preserve msync's formal alias/effect summaries rather
than directly importing a JIT cache architecture.

## Evaluation-boundary correction: initialization is not lazy traversal

The user challenged the premise that literal Range element initialization should
be lazy at all, and asked for comparison with Stream APIs and C++ ranges. That
changes the recommended design direction: the previous shared-constructor-thunk
proposal preserves the current implementation's evaluation boundary without
first justifying that boundary. Existing language notes document that behavior;
they do not establish that it is the intended model.

### Primary-source comparison

- C++ list initialization evaluates initializer clauses in source order and
  initializes storage; a container's participation in the Range abstraction does
  not turn its initializer expressions into deferred callbacks.
  [C++ draft, dcl.init.list](https://eel.is/c++draft/dcl.init.list#4).
- `transform_view` applies its callable on iterator dereference, not by deferring
  the expression that previously constructed the source container.
  [C++ draft, range.transform.iterator](https://eel.is/c++draft/range.transform.iterator).
- Java method arguments evaluate before invocation, left to right. Therefore
  `Stream.of(f(), g())` calls f and g when constructing the stream, even when
  a later `limit(0)` consumes no elements. `Stream.generate(() -> f())` explicitly
  supplies the delayed computation instead.
  [JLS 15.7.4](https://docs.oracle.com/javase/specs/jls/se25/html/jls-15.html#jls-15.7.4),
  [Stream factories](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/util/stream/Stream.html).

### Concrete current behavior

A focused actual-CLI reproduction at default O2:

```msync
let x = 1;
let direct = [x];
let computed = [x + 0];
x = 2;
for n in direct { (n as String) -> ./direct.txt; };
for n in computed { (n as String) -> ./computed.txt; };
```

Exit status 0; `direct.txt` contains `1`, `computed.txt` contains `2`.
The source and output files are retained under the evidence directory's
`range-boundary/`. The binder's syntactic `PureValue` test accepts a name but
rejects addition, despite the addition having no side effects. This makes a
simple arithmetic rewrite change the read time of a mutable input.

### Recommended boundary, superseding the earlier thunk-preserving proposal

1. Literal Range initialization evaluates element expressions once, in source
   order, and retains their values/aliases. No per-element implicit functions.
2. Enumerating the initialized sequence yields its stored elements; Scope identity
   is retained rather than reconstructed by each enumeration. Freshness applies
   when the Scope constructor expression executes, not whenever an existing
   container is traversed.
3. Lazy adapters/generators retain deferred traversal and callbacks. Constructing
   a pipeline is distinct from running it. A normal function returning a Range
   should not implicitly defer its arguments/body solely because of its return
   carrier; its body may construct a lazy pipeline without consuming it.
4. `take(0)` suppresses downstream traversal/generator callbacks, not prior source
   initialization. Explicit generator APIs express delayed production.

This removes the cause of literal-item thunk expansion rather than making the
wrongly placed thunks cheaper. It also removes the need to infer full return
shape merely to select eager versus deferred evaluation of an ordinary call.
Ordinary return typing, dependency/effect analysis, and lazy producer summaries
still remain; it is not a claim that all semantic query work disappears.

This is an observable language-contract revision, not a transparent optimizer
rewrite: read/effect/error timing and repeated-enumeration identity change.
The subsequent implementation updates the authoritative language contract and
affected real journeys together, rather than retaining syntax-sensitive behavior
as a permanent performance constraint.

## Context-summary ownership correction

The user challenged the implementation statement that contextual alias/effect
summaries must remain independently preserved. That statement conflated observable
program behavior with a particular compiler data structure. Runtime aliases are
shared storage addresses; their behavior does not require one compiler heap
snapshot per caller. An optimization's alias assumptions and a static typing
obligation are analysis premises, not reasons to duplicate the caller graph.

Inspection before the formal-plan migration gave a more precise ownership model:

- `SemanticAnalyzer.FormalInput.cs` already abstracts arguments and captures into
  formal input roles. `ProjectInvocation` binds them back to actual arguments and
  capture locations. This is not missing machinery that needs another parallel
  context-summary cache.
- `FunctionInstance` then coupled executable identity to result metadata,
  reachable objects/indexed regions/cells, storage/definition maps, alias views,
  member/callable contracts, write sets and write events. `FreezeSummary` walks
  reachable results **and arguments/captures** to retain those graphs.
- `InstanceKey` fingerprints input/capture schemas and alias relations before
  `BuildInstance` reserves and analyzes a new executable body. A new summary key
  therefore also creates code, even when the eventual body is equivalent.
- The effect machinery is not simply a flat post-state cache: `WriteEvents`
  already represents guarded/branching writes, and `InvocationProjection.Replay`
  replays branches against destination facts. Preserve/reuse that useful formal
  role; do not claim a new transfer-function engine must be invented from scratch.
- The new worktree `HirFunctionSharing.Publish` reduces Native lowering work but
  still runs after instances and HIR bodies have been constructed. It is an
  intermediate migration, not the final answer to avoiding duplicate generation.

The intended unit of reuse is a function's typed formal plan, including only
necessary return-origin/effect relations and assumptions. Call sites own the
actual argument/capture binding and current flow state. For example, a function
that stores `arg0.x` and returns `arg0` needs the relations `write(arg0.x)` and
`return_alias(arg0)`, not independent retained copies of a's and b's reachable
heaps when called as f(a) and f(b).

Specialization should distinguish type witnesses, callable targets, layouts or
alias premises **when they actually affect the typed body or a used proof**;
concrete caller location identities and irrelevant incoming state are not code
identities. Binding and applying a formal plan remain call-site work, not a new
persistent callee summary. The current packaging and complete key policy are
therefore open to replacement, not protected invariants. The end state must
avoid duplicate inference/emission at the producer rather than merely relocate
a whole-program deduplication pass from Native IR to HIR.

This is consistent with the established function-summary/transfer approach to
interprocedural analysis, but msync's recursive, mutable structural domains must
not be assumed to satisfy the finite distributive assumptions of IFDS wholesale.
See [Reps, Horwitz and Sagiv, POPL 1995](https://doi.org/10.1145/199448.199462).

## Implementation follow-up: eager sources and early formal-plan reuse

The baseline observations above describe the original revision. The worktree now
implements the corrected language contract rather than optimizing implicit lazy
items. `language-v080.md`, `ranges-v080.md` and `generic-specialization-v080.md`
are the current behavioral/ownership contracts; historical proposals in this
investigation are not additional required mechanisms.

Removed product paths include binder purity classification, literal-item and
ordinary-call thunk creation, the HIR range-mode arena, implicit lazy Native
opcodes and interpreter producer wrappers, and post-generation Native function
folding. The intermediate HIR sharing pass mentioned above was also deleted.
Remaining legacy enum names identify historical wire bytes only; they do not
provide execution support. HIR4 retains an empty reserved wire section and
rejects retired deferred modes without renumbering unrelated opcodes.

Early reuse is the existing specialization lookup, corrected at its producer:
by-value roots and Range elements do not carry source-binding storage constraints;
Scope plans use their structural row rather than reaching-definition alternatives;
alias keys use canonical passed-input relationships. Write-only captures carry
the declared storage schema, not the old callable/value graph. An incomplete
entry is the recursive promise; the redundant active-key table was removed.
There is one formal instance per key, not separate retained caller heap snapshots.
The instance still owns reachable *formal* object/cell metadata used for result
projection and callable/storage contracts; deleting that metadata indiscriminately
would not be the same as removing per-caller context dependence.

All functions now publish one branching write-event plan instead of choosing
between flat post-state write sets and event replay. Call sites bind this plan to
actual locations. The plan interpreter is shared with loop exit joining.
Return and break exits retain their value and event sequence together; normal
completion is another exit. No-write alternatives start from current destination
facts, not a previously retained caller state.

Two failures materially refined this implementation:

- `install(false)` incorrectly overwrote the current captured callback. Borrowed
  branch overlays were contaminated when the first branch was published into
  their common parent before the unchanged branch was read. Joins now read all
  branch facts for each key before publishing that key. The real journey checks
  `1 / 2 / 9` across unchanged and replacing invocations with **one** generated
  setter body.
- B+ churn reached `_Chain::remove` with a nullable successor incorrectly inferred
  non-null. The event plan had omitted paths terminating in explicit `return`.
  Keeping all return exits fixed full deletion/reinsertion. A separate local
  callback probe showed that collecting break events only for function return
  was insufficient: `select(true/false)` returned `6 / 6`. Loop exit application
  now uses the same transfer interpreter and produces `5 / 6` before subsequent
  statements in that function are analyzed.

Scope admission no longer recursively scans referenced heap graphs. It checks
immediate field compatibility; dependent member projection retains physical
layout and logical-type obligations. This is the existing Native model boundary,
not a new runtime check on trusted internal calls.

Removing item thunks exposes the real large source constructor as a single body.
Lowering therefore indexes locations once by owner and binds lexical fields into
frames only when executable uses outside their constructor need it. Native
liveness uses block-level gen/kill fixed points with bitsets, then fills per-PC
live-after sets in a reverse block walk. A focused generated-graph comparison
matched the prior HashSet solver at 4,374 observations, including word boundaries,
backedges, observable roots and escapes. Memory passes avoid invalidation scans
for the first definition of noncyclic, unexposed single-assignment temporaries.
These are representation/complexity changes, not concessions to implicit laziness.

### Validation evidence accumulated during implementation

- `2a747d06efd046b183ca3464c6595d68`: Release managed real Range, semantic,
  Heap and complete B+ source/object/executable journeys passed after return-exit
  repair. Later loop-plan extraction requires its own current-image receipt.
- `9731478451fb4a0b86f3e39b326f0a58`: Native AOT artifact and LSP journeys passed.
- `bfd7cfa746ef4c9d9ef3c183c856fed5`: Native AOT semantic, Heap and B+ journeys
  passed; the newly added break regression failed on this older frozen image,
  before break-plan application was implemented.
- `d33de24b3a0c4193b1101c723ecba50d`: Release managed Range journey passed with
  explicit early-return and break alternatives. A later local-in-function
  break regression motivated the shared plan-interpreter extraction.
- The selected existing REPL `ieee-feedback` journey passed. The
  `captured-callable-prefix` failure-recovery case reports `unreached` instead of
  `after` on both the new AOT image and the earliest retained implementation image
  (`483bb46aca8541f8b648ce755aa97733`). This is not evidence that Range semantics
  require that behavior; it remains a separately localized scheduling/recovery
  question, not a passing session contract.

Working probes and logs are in `.temp/range-rework/`; final reusable receipts and
reproductions should be retained under `artifacts/diagnostics/` with image hashes.


### Final delivered-state evidence

Private activation writes are pruned when the formal plan is exported. A
reachability closure starts at formal inputs, captures and returned values, then
follows values installed by exported writes. This retains escaping closure cells
without rooting unrelated private locals. Empty branches remain when another
branch writes, preserving the caller's unchanged state.

Current evidence is retained at `artifacts/diagnostics/range-evaluation-9703b23/`,
including image hashes, real-journey receipts, minimal regressions, liveness
property output, old-HIR rejection, sources and a fully typed profile reproducer.
The Native AOT payload is `artifacts/native/range-evaluation/msync` (SHA-256 in
`image.json`). Native core run `678fb378ccd74521b88988b9c2ca37ba` passed Range,
semantic, Heap and all three B+ routes. Range checks 28 file oracles on each of
six O0/O2 source/HIR-object/bound routes, plus exactly one published body each
for conditional setters, early-return setters, break setters, local callback
selection and calls on distinct Scope objects. Boundary run
`52cfdedcd696417dae03f6e0f4912f0c` passed artifact and LSP journeys. The focused
Native AOT REPL IEEE/capture/alias journey also passed. Managed core run
`91d2a5789d174aa4a1d980cf47f91e3a` passed the same core matrix. The last product
cleanup removes an unreferenced legacy deferred-description helper; the current
Release source builds with zero warnings/errors.

Sequential fresh-process **Release managed** profile observations on the same
original full fixtures (not Native AOT comparisons):

| Fixture | Phase | Original seconds | Current seconds |
| --- | --- | ---: | ---: |
| B+ | Analyze | 10.77 | 7.54 |
| B+ | Native lower | 6.23 | 0.74 |
| B+ | Optimize | 14.85 | 21.43 |
| B+ | CLI wall | 32.82 | 30.52 |
| Heap | Analyze | 8.41 | 3.84 |
| Heap | Native lower | 4.00 | 0.45 |
| Heap | Optimize | 11.33 | 11.04 |
| Heap | CLI wall | 24.44 | 16.00 |

These are individual observations, not a statistical speedup claim. B+ has 347
sealed HIR functions rather than 1,146; Heap has 261 rather than 631 before the
old late fold. There are no implicit item/call thunks or late function folds.
The overall compilation improves, but B+'s optimizer phase itself regresses:
the eager source now exposes the large constructor directly. Current exclusive
costs identify escape analysis (~7.07 s) and inlining (~6.12 s), rather than
liveness (~1.07 s), as the next backend investigation. Optimizer allocations are
5.62 GB cumulative versus the original 4.03 GB; this is not a claim that the O2
performance problem is completely solved. The requested semantic correction and
producer-side sharing do not justify hiding that remaining work.

### Standard modules use ordinary dependencies too

The final sharing audit found another unsound shortcut: a standard-module capture
was keyed only as `M<module location>` while nested family members remained
mutable. `replace-module-family` could therefore reuse a `read` body compiled for
the old `List` layout after installing a new family. Removing
`StandardModuleCapture` and both numeric-module shortcuts makes these captures
follow ordinary Scope/member/callable keys. It adds no per-caller summary or
special mutation-invalidation protocol. The selected replacement case passes all
nine O0/O2/O3 detached/bound routes; the complete captured-module matrix then
passed 93 replacement, retained-alias, argument-order, returned-closure, shadowing
and session contracts.

The current managed core receipt is
`0a7cb33cadae4ac182a68ec57441fa68` (all four journeys passed).
`tests/HirQueryOwnership` was moved from its obsolete portable execution helper
to Native lowering plus storage planning; 360 ownership/source-free checks pass,
including canonical alias properties and transaction/session isolation. Its
borrowed-world allocation observation is 1,144 bytes at both 10 and 100,000
bindings. Alias-key tests now assert relevant shared-vs-disjoint input relations
and allocation renaming, not artificial differences for a single input's
irrelevant external alias edge.

Retired range-arena/opcode references and implicit-laziness expectations were
also removed from affected C# fixtures. Some older fixture projects still use
pre-v0.9 `Module`/`HirLowerer` APIs or construct Native programs without required
storage plans; their attempted runs are not included in the passing acceptance
matrix, and no legacy execution fallback was introduced for them. The real
source/HIR/object/bound journeys remain the primary product evidence.

The phase table immediately above predates removal of the numeric-module key;
it is a retained intermediate observation, not a measurement of this last
change. Updated current-image observations belong below.

### Final module-unified image

Native core run `b0cfba250a0c4bec8cd61ba89e48f2c0` passes Range, semantic,
Heap and B+ journeys. Native boundary run
`e2d45e89c87948e4adb86d635a420971` passes artifact and LSP journeys. The selected
successful REPL mutable capture/alias/IEEE journey passes on the same payload.
The final Native AOT SHA-256 is
`5a432e87c30e059ee1eba022ea29310213eebde194aa9465a5d76ecba49a20da`.
Receipts are in the `unified-*` subdirectories; `image.json` identifies this
payload. Earlier receipts above remain historical migration evidence.

Sequential fresh-process Release managed observations for this last source,
with the profile recorder enabled as in the original baseline:

| Fixture | Analyze s | Native lower s | Optimize s | CLI wall s | Sealed HIR functions |
| --- | ---: | ---: | ---: | ---: | ---: |
| B+ original | 10.77 | 6.23 | 14.85 | 32.82 | 1,146 |
| B+ current | 7.99 | 0.89 | 24.32 | 34.12 | 379 |
| Heap original | 8.41 | 4.00 | 11.33 | 24.44 | 631 |
| Heap current | 4.75 | 0.54 | 12.22 | 18.13 | 271 |

The current rows are `*-unified-managed.json` / `.jsonl`; the phase observations
are not independent statistical estimates. There is **no demonstrated overall
B+ speedup**: the required semantics/sharing correction has reduced frontend and
lowering work, while O2 backend cost has increased. Current B+ exclusive costs
are escape analysis 7.85 s, inlining 6.89 s and local-value optimization 3.89 s;
liveness is 1.41 s. Its optimizer cumulatively allocates 6.07 GB. Heap improves
overall, but that does not establish performance for unrelated workloads.
Further O2 work should target those measured backend analyses over the large
eager constructor, not restore implicit producers, unsafe module-key shortcuts
or per-caller effect snapshots. This completes the requested evaluation-boundary
and early-sharing migration, not every remaining compiler performance problem.

### Why the final B+ wall time increased

Comparing the retained original/current `optimizer.function` records, `<main>`
has 3,177 -> 18,408 input instructions and 8,020 -> 12,803 input slots. Its local
optimization duration rises 3.33 -> 7.87 s and cumulative allocations rise
0.86 -> 1.64 GB. Both runs still use three local fixed-point iterations and four
program-facts invalidations for `<main>`: this is not evidence of runaway
iteration. Whole-program input instructions barely change (60,953 -> 60,451),
while input slots increase (52,371 -> 60,468). Function count alone was therefore
a poor proxy for backend work.

The measured phase balance is frontend/lowering savings of approximately
8.12 s versus an O2 increase of 9.47 s. Escape-analysis exclusive time rises
4.00 -> 7.85 s; inlining rises 3.13 -> 6.89 s. `NativeEscapeAnalysis` requests the
shared address-analysis facts, so its named interval also owns cold/recomputed
address analysis. `NativeInlining.Builder.Function` likewise requests caller
provenance and private-slot facts, and rounds invalidate changed function facts.
The existing backend now sees the eager constructors concentrated in the main
function rather than distributed over implicit producer bodies. This explains
where the regression manifests and the next causal investigation; the current
trace does not yet isolate a particular collection operation as the sole cause.
It is backend implementation debt, not a semantic requirement that eager sources
must compile more slowly.

### What the O2 analyses actually compute

`NativeAddressAnalysis` is a shared interprocedural abstract interpreter, not
just a local escaped/not-escaped bit. `Points` retains possible allocations,
locations, foreign regions, callable/capture origins, Range elements and retained
objects, plus symbolic formals, higher-order applications, projections and reads.
`ProgramFacts.SolveLocked` drives a function/caller worklist. A changed function
summary queues callers; each `Step` scans that function's complete instruction
array, then merges cell writes, returned/escaped values and effects.

`NativeEscapeAnalysis` first requests these address facts, then traverses roots
through fields, callable captures, retained values and Range elements. That
explains why the 7.85-second "escape-analysis" interval is not solely a simple
reachability traversal. Inlining also requests the same facts to check escaping
locations, the concrete closure/capture ABI and private/read-only argument
locations. Its 6.89-second attribution includes those requests, not just copying
instructions. These are sequential named-pass attributions, not double-counting
one elapsed interval.

`ProgramFacts.Invalidate` removes affected function analyses and their transitive
callers. The optimizer has ordinary local fixed-point rounds, compaction, a
second inlining stage and reoptimization of newly expanded functions. The cache
is real, but invalidation is at whole-function/caller scope rather than repair
of the affected memory constraints. Current fixed-point transfer scheduling is
at function granularity; the trace does not yet count the inner address solver's
node/edge growth or per-function `Step` work, so it does not isolate one exact
set-union operation as the sole bottleneck.

The final B+ allocation-plan census is 896 logical-heap allocations, zero frame
placements and zero eliminated allocations. These address facts have other
consumers (memory forwarding, call/capture legality, etc.), so this does not prove
zero benefit. It does show why spending seconds on whole-graph precision should
be evaluated against the concrete decisions obtained, rather than assuming all
of that precision is necessary for an O2 workload. IR construction already knows
many fresh allocation sites, direct field addresses and single definitions;
local proof paths should consume those facts before requesting general
higher-order heap analysis.

### Why the analysis exists, and the current mandatory coupling

Aliasing is represented by shared addresses; executing Load/Store does not need
an alias analysis to implement that sharing. The analysis is used when changing
those operations or their storage: forwarding/deleting loads and stores,
private-slot promotion, capture/frame lifetime decisions, borrowing arguments,
and removing layout checks.

The current implementation does not keep that distinction clean.
`NativeOptimizer.OptimizeCore(level: 0)` still invokes `NativeStoragePlanning.Run`.
When a function has no Storage plan, that method constructs NativeEscapeAnalysis
and its address facts. `NativeStoragePlanning.Plan` consumes them for Escapes,
ReadOnlyParameter, PrivateArgument, FrameAlias, FrameAllocation and NarrowLayout
metadata. Thus simply disabling O2 memory rewrites does not remove the general
analysis. Some of its results have become mandatory inputs to the chosen runtime
frame/storage protocol, not merely optional optimization hints.

Removing that cost requires uncoupling the execution/storage contract from
optional proofs: represent shared locations and escaping ownership explicitly;
retain ordinary memory operations by default; make shortened lifetimes,
borrowing, promotion and operation elimination transformations with local,
benefit-driven proofs. Deleting the solver without replacing its current
storage consumers would not perform that architectural change.

### Comparison with mature compiler analysis boundaries

Primary LLVM/GCC sources explain the engineering gap more precisely than saying
"alias analysis is inherently expensive":

- [LLVM BasicAA implementation](https://github.com/llvm/llvm-project/blob/main/llvm/lib/Analysis/BasicAliasAnalysis.cpp)
  implements a stateless query analysis using pointer/object identities and
  address expressions. It does not require a complete interprocedural heap
  interpretation to answer every local alias query. Its address decomposition
  and other searches have explicit compile-time bounds.
- [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) links memory definitions,
  uses and phis, allowing walkers to inspect related potential clobbers and cache
  answers. The documentation explicitly identifies quadratic behavior in the
  older memory-dependence approach and does not optimize all MemoryDefs by
  default because that can be quadratic. The representation and selective
  queries matter, not simply adding memoization to a full heap solver.
- [LLVM New Pass Manager](https://llvm.org/docs/NewPassManager.html) expressly
  prevents inner passes from triggering computation of outer IR analyses,
  citing quadratic compile-time behavior. Cached immutable outer results are
  accessible. Whole-function invalidation is not itself unique to msync; the
  problematic combination here is an inner optimization query, broad
  interprocedural solving, caller-transitive invalidation and repeated requests.
- [LLVM dead-store elimination](https://github.com/llvm/llvm-project/blob/main/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp)
  uses MemorySSA walks with scan/walker budgets; exhausting a search leaves the
  optimization unperformed, not source execution unsupported. Budgets bound
  optional work but do not replace fixing the dominant representation/scheduling.
- [GCC alias-analysis documentation](https://gcc.gnu.org/onlinedocs/gccint/Alias-analysis.html)
  also includes constraint-based points-to/escape analysis, together with memory
  SSA and query oracles. Thus the distinction is not that mature compilers never
  solve points-to constraints. They separate such precision from every ordinary
  local memory question and give transformations a sparse memory-dependence
  interface.

For msync the concrete recommendation is to separate mandatory storage/lifetime
semantics from optional proofs, expose cheap identity/base-offset/call-effect
queries, introduce sparse memory-dependence use/def relationships for candidate
rewrites, and schedule cross-function summary solving at an explicit outer
boundary. Retain/update the necessary results across transformations where
practical; do not replace this architecture work with an arbitrary global solver
iteration cap or a new caller-heap snapshot cache.

### Mid-end scope and profitability imbalance

The active pipeline in `NativeOptimizer` provides constant/global-value
propagation, local simplification, global-memory forwarding, dead-memory/result
cleanup, inlining, compaction, moves/register allocation and storage planning.
The active optimization project has no dedicated LICM, induction-variable,
loop canonicalization, unrolling or vectorization pass. `NativeControlFlow.Cyclic`
recognizes cycles for other decisions; this is not a loop optimization pipeline.
The main O2 loop repeatedly constructs control-flow/escape facts and uses them
for low-level simplification. This is a transformation/analysis imbalance, not
evidence that the optimizer spends all its time in artifact/source validators.

LLVM's [loop terminology and canonical forms](https://llvm.org/docs/LoopTerminology.html)
and [transform pass inventory](https://llvm.org/docs/Passes.html) illustrate the
missing connection: analyses support explicit loop transformations over canonical
loop structure. For msync, profitable priorities should be driven by its real
iterator/callback and mutable-container workloads, not by adopting every LLVM
pass name:

1. Keep structured loops/known iterator operations visible through the typed HIR
   optimization boundary; expose canonical loop entry/backedge/exit information
   for Native transformations when lowering is appropriate.
2. Start with loop-invariant pure computation/address calculations and known
   traversal/callback specialization that reduces per-element dispatch. Use
   actual bound callable identities, not standard-library name matching, and
   retain iterator disposal, callback/effect order and zero-trip behavior.
3. Promote truly private scalar locations and eliminate temporary aggregate/
   iterator machinery locally so later memory passes see smaller problems.
   Constructor-known addresses and layouts should be direct IR facts rather
   than facts rediscovered by general interprocedural analysis.
4. Introduce sparse memory dependencies only for transformations that consume
   them. Do not let optional load/store rewrites force all source paths through
   a symbolic heap solver or source-invariant revalidation.

Induction-variable and strength-reduction proposals must respect msync Number's
binary64 semantics: integer-style arithmetic identities are not automatically
valid. B+/Heap pointer-chasing workloads are not an automatic SIMD opportunity;
vectorization and aggressive unrolling should not displace the dominant work.
Measure actual executed operations/allocations eliminated and end-to-end runtime
benefit alongside compilation cost, not proof counts or analysis precision alone.
