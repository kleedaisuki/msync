# Stack CFG dataflow optimizer (v0.8.0)

`GraphOptimization` works over the executable typed bytecode, not debug source.
The optimizer orchestrator alternates it with call specialization/inlining and
finishes with module reachability and literal-table compaction. Executable
binding reruns that pipeline after context-dependent path lowering.

## Representation and rewrites

Each bytecode instruction owns stable output tokens. `Dup` has two distinct
outputs; local stores have a non-stack definition token. A forward CFG worklist
computes reaching definitions for local slots and operand-stack positions.
Only block-entry states are copied: the state inside a block has one mutable
owner. Join propagation is monotone and queued blocks are coalesced.

Primitive constants propagate through those definitions. The evaluator does
not allocate mutable Scope objects into the constant table. It delegates
whitelisted native evaluation to `PureEvaluation`; filesystem observations,
authentication, arbitrary native calls and deferred enumeration are not
compile-time computations. Local path operations are folded only in executable
context; SSH/WSL path spelling retains its endpoint grammar.

Backwards demand starts at observable effects, possible failures, control flow
and the returned value. Known pure terminating calls consume the shared
interprocedural effect summaries. Captured mutation, recursion/divergence,
unknown calls, reads, writes and barriers remain roots. A discarded effect
keeps the operation and a `Pop`; a dead pure producer disappears.

Operand-stack joins require more than ordinary instruction liveness. Tokens
occupying the same joined stack column share demand. If one branch retains a
value while another discards it, the latter branch still needs its `Pop`.
Dead or constant-replaced operations therefore discard any operand retained
for another control-flow path. Constant `Dup` reconstructs exactly the demanded
number of values. These rules preserve short-circuit expressions without
special-casing their source syntax.

Rewriting removes unreachable instructions, remaps all jump and iterator
successor offsets, and removes fallthrough jumps. It repeats to structural
convergence, not a fixed pass budget: a folded condition exposes reachability,
which can expose another constant or dead definition.

## Mutable storage and object identity

A local slot captured by `LoadCell` is not a constant even if its original
initializer is known. Live captures demand the slot's stores. Entry captures
and inlined `GetCapture` aliases refer to another frame's storage: their writes
are observable without a local read, and stores retain the prior storage
reference as well as the assigned value. Raw `LoadCell`/`LoadStorage` transfers
can put one cell into multiple local slots; a storage-identity union propagates
constant invalidation, external ownership and write demand across those aliases.
Each observed cell group visits its indexed definitions only once, rather than
rescanning the function for every read. `InitializeLocal` creates fresh
storage, while `StoreLocal` updates an existing cell when present.

Scope alias analysis follows reaching allocation identities, never dictionary
value equality or source names. Writes to a proven private allocation can die
with the allocation. Unknown receivers remain effectful. Existing-field reads
are nontrapping; missing-field reads and writes preserve their runtime error.
A captured or call-exposed object is not assumed immutable. `LoadStorage`
preserves raw field cells, and a live escaped closure demands mutations of its
backing field even if the enclosing Scope would otherwise be dead.

## Focused acceptance and scaling

`tests/OptimizationGraph` compares optimized/unoptimized execution, artifact
round trips, bound execution, observable output bytes and selected errors.
Its mutable cases include external captured writes, shared Scope aliases,
private dead mutations and escaped field storage. Unknown-parameter
short-circuit cases exercise both actual edges rather than relying only on
constant branches.

A Release/JIT component probe on Debian Linux, .NET SDK 10.0.403, QEMU virtual
CPU generated independent `let valueN = N;` statements, parsed once at O0,
warmed the full O3 optimizer three times and measured nine synchronous runs.
The median full-optimizer measurements after block-entry snapshots and sparse
stack-join groups were:

| Statements | Input instructions | Time (ms) | Thread allocation (bytes) |
| ---: | ---: | ---: | ---: |
| 100 | 400 | 1.161 | 328,520 |
| 500 | 2,000 | 3.230 | 1,565,320 |
| 2,000 | 8,000 | 13.792 | 6,195,408 |

Every output was exactly `Null, Return`. These are component measurements, not
Native AOT startup or transfer-throughput claims. The setup is intentionally
linear and excludes parser time; it detects the former design's avoidable
per-instruction copying of the entire local-state array.

### Native callable ABI and call effects

Older and current objects can invoke `FunctionValue.NativeName` through the
ordinary `Call` stack ABI. `Call.B` is a specialization hint, not evidence that
the callee is nonnative. The graph resolves the actual reaching callable before
lowering a known native to `NativeCall`, propagating pure constants or consuming
an interprocedural summary. Unknown callable values retain their invocation.
The discarded callable expression can still contain effects; those remain
independent demand roots. If a joined stack column retains the callable on
another edge, lowering explicitly consumes it below the native arguments while
preserving the returned value. This keeps the historical stack ABI without
charging ordinary statically known native calls for a dynamic dispatch.

## Analysis applicability and lifetime

The module driver computes a compact opcode-feature summary before requesting
analyses. Predicate propagation needs a conditional or a type/null test; field
scalarization needs an allocation; loop analysis needs an iterator; directional
normalization needs a plain marker. A literal followed by Return is already a
fixed point. These are applicability proofs, not size quotas or reduced language
semantics. In particular a literal-return function and an already optimized
bound module do not allocate CFG/capture/field analyses a second time.

Within one optimization invocation, preparation is memoized by function identity
and index. Graph results are retained only while the interprocedural effect
vector is unchanged. A changed vector advances the graph-analysis epoch;
rewriting a function changes its identity. Constants are append-only until final
table relocation, so cached instruction operands remain valid. No cache crosses
the source/relocatable/executable boundary, context binding, REPL submissions, or
object serialization. This follows the analysis-preservation discipline of
[LLVM's new pass manager](https://llvm.org/docs/NewPassManager.html#using-analyses),
without importing a general pass-manager framework into the small bytecode VM.

Inlining performs one expansion wave before returning to dataflow/DCE. Its
multi-use body-cost budgets are 32/128/512 instructions at O1/O2/O3; single-use
inlining does not duplicate the body. These are profitability decisions, never
input restrictions. Interleaving folding prevents an acyclic repeated-call DAG
from materializing an exponential transient tree before its result is folded.
Private local slots are compacted after graph cleanup, retaining the captures
and parameters ABI prefix and all exported REPL slots.

The constant pool, dataflow meets and value-number keys use bit-exact
`ValueRepresentation`, distinct from language numeric equality. This preserves
negative zero and NaN payloads without changing range `contains`/`find`/`unique`
or ordinary comparison semantics. A successful `x == 0` predicate therefore
does not replace x by a particular signed-zero representation.
