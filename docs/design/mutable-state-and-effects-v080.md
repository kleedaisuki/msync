# Mutable state, optimization and scheduling in 0.8

## Decision and scope

The November 9 language requirement supersedes the earlier immutable-binding
decision in the 0.8 notes. `let`, parameters, named declarations and Scope fields
are mutable storage locations with immutable types: assignment changes a value,
never its storage type. A new declaration still shadows rather than
modifying an older lexical declaration. Null admission remains explicit and now
also applies to Scope fields:

```msync
scope SomeType {
    remote: ssh://forge,
    new: fn { scope { filename?: null } },
};
let object = SomeType::new();
object::filename = ~/.codex;
```

Without `?`, `filename: null` is a declaration error, even when the entire
program would subsequently be removed as dead code. Type checking precedes
optimization. Constructing an SSH Connection describes an endpoint; it does not
authenticate. Consequently the corrected example has no external effect and,
when none of its values escape, executable optimization can remove it entirely.

This document fixes the cross-component contract and implementation order. It
does not label unimplemented passes or unexecuted acceptance cases as complete.

## State ownership and identity

Keep these concepts separate:

| Entity | Identity and lifetime | Mutable state |
| --- | --- | --- |
| Lexical declaration | One compiler binding identity; each execution creates its storage | Current Value |
| Lexical environment | Persistent name-to-binding map for one source position | Replaced map root when declaring/shadowing |
| Closure | Callable identity plus references to captured storage | Shared captured payloads, not copied payloads |
| Scope instance | One allocation identity shared by aliases | Declared field payloads |
| Scalar/path/connection Value | Ordinary value representation | None |
| Flow fact | Binding/field identity plus reaching write version | Current inferred type, shape and known value |

`let alias = object` copies the reference to the Scope instance. Updating
`alias::filename` is visible through `object::filename`. Rebinding `alias` does
not rebind `object`. Capturing `object` captures its binding location: later
rebinding that location is visible to the closure. Shadowing with another `let
object` creates a different location and does not retarget the older closure.
Each constructor invocation creates a fresh Scope; equal field contents do not
make two instances interchangeable.

The VM may keep uncaptured locals directly in its local array and promote a
slot to a shared cell when first captured. This avoids a heap allocation for
every scalar declaration. A captured slot must then load and store through the
same cell in both its defining frame and all closures. Do not use names to find
that cell later. Alternatively an initial uniform-cell implementation is
semantically valid, but its normal-path allocation cost must be measured.

Scope fields have stable membership for a declared shape; mutation changes a
field value, not its name or the set of fields. Scope identity and field
nullability are distinct from its current field values. In particular mutable
Scope values must not be interned by structural equality, reused as live mutable
constant-pool objects across executions, or rebound by recursively cloning a
runtime instance. Constant data describes a construction recipe; executing the
construction creates its owned instance.

Runtime scalar evaluation stays on the interpreter's control owner. Independent
I/O jobs can execute concurrently after their operands have been evaluated.
Mutable source locations are not implicitly written by arbitrary worker tasks;
callbacks which execute source code use the same location/effect semantics.
This preserves ordinary language ordering without locks on every scalar access.

## Inference and nullable flow facts

A storage descriptor owns its fixed inferred/annotated type and Null admission. A flow
fact describes the current reaching value. Neither can substitute for the other:

* An annotation constrains every store, including executable Path/String
  conversion where the existing conversion contract requires it.
* Infer an unannotated storage type at compilation, then keep it fixed. Normal
  initialization provides that type; assignment checks compatibility and never
  widens it. A written union constraint admits its stated alternatives but is
  itself immutable. Sequential assignment updates known-value facts within that
  constraint; control-flow merges join incoming facts, not storage types.
  Loop headers and recursive summaries use a worklist for those facts.
* `?` permits a Null store. A currently non-null value does not remove that
  permission; a currently Null value does not prohibit a later non-null store.
* A nullable Null initializer supplies no non-null payload evidence. Represent
  its payload as a compile-time inference variable until stores or constraints
  supply evidence, then freeze the solved payload type. Check all relevant
  stores against that solution; this is inference, not runtime type evolution.
  Do not conflate the initial hole with the closed singleton Null domain
  or silently give every operation permission through Any.
* That payload/shape inference variable belongs to the relevant allocation
  instantiation and is shared by its aliases. Do not couple unrelated objects
  merely because they came from one constructor or use the same field name.

A flow-sensitive read can be more precise than the storage domain. After
`object::filename = ~/.codex`, that exact field is Path on the current edge.
After `if object::filename is null { ... } else { ... }`, the else edge removes
Null only until an overlapping write. An alias store or a called function whose
write summary reaches the object invalidates that refinement. Unrelated writes
must not invalidate all facts globally.

Compiler/editor snapshots must remain snapshots. Do not mutate a shared
`Binding.Info` object inside previously captured persistent environment roots.
Maintain evolving facts separately or replace immutable descriptors. The same
rule applies to speculative REPL analysis, which must not mutate accepted
session bindings or templates.

Generic specialization uses the fixed captured storage types and relevant
callable alternatives. A function that reads a mutable captured location must
not permanently bake in its definition-time payload or callable identity.
Type facts remain valid across compatible writes; constant and callable-identity
facts do not. Function-valued locations can have multiple checked callable
identities despite retaining type Function; those alternatives can use precomputed
runtime dispatch to monomorphic entries. Recursive identities are reserved before lowering. Returning a closure
keeps its cells alive; returning a Scope keeps its identity and field locations
alive. Across REPL submissions, live slots and accepted storage contracts are
authoritative; invalidate assumptions when a later submission updates them.

## One optimization model across stages

Keep the portable stack bytecode as the serialized execution contract. Build an
analysis representation with explicit produced values, operand uses, CFG edges,
local/field locations and effects. It may be transient; a second serialized IR
format is not required to obtain real dataflow analysis.

The graph must represent data, control and effect dependencies separately:

```text
typed bytecode -> CFG / reaching definitions / def-use graph
                         + call and escape summaries
                         + field/cell read-write dependencies
                                  |
                    propagation and direct-call inlining
                                  |
              executable edges -> liveness -> dead stores/values
                                  |
                compact bytecode + relocated references
                                  |
                contextual binding -> repeat with context facts
```

Use one effect vocabulary for compiler optimization, executable lowering and
runtime scheduling. Distinguish reads/writes of lexical cells and Scope fields,
filesystem reads/writes, process launch/join, terminal/authentication interaction,
and unknown native effects. Callable summaries propagate over the call graph;
recursive strongly connected components require fixed points, not recursive
source expansion. Unknown effects constrain the transformations that actually
need independence; they do not make every unrelated pure definition live.

An effect-free operation is not automatically safe to speculate. A potentially
throwing operation, nonterminating call or lazy iteration may change observable
behavior when introduced, erased or moved. Treat speculatability/termination
separately from memory effects. Compilation must not query existence, enumerate
live directories, start endpoint helpers or execute native commands to discover
a constant.

The concrete pass order is:

1. Discover reachable call identities and summarize reads, writes, escape,
   external effects and potential exceptional/nonterminating behavior.
2. Propagate constants and reachable CFG edges together. Join only executable
   incoming edges; use worklists rather than repeated whole-function rescans.
3. Inline selected direct finite callees with remapped parameters, captures,
   locals, returns and spans. Keep recursive graph edges; code-size control is
   an optimization heuristic, never a source-language validity limit.
4. Recompute affected facts, forward nonescaping field values, and eliminate
   dead stores to locations which cannot be observed. A local allocation and
   all its dead stores can vanish together.
5. Mark live results/effects backward through def-use, simplify branches, remove
   unreachable blocks, and compact instruction/local/constant/function tables.
   Relocate jumps, closure identities and dispatch tables consistently.
6. After contextual binding, repeat the useful passes with known cwd/home/source
   projection and resolved host identities. Partition and schedule only the
   selected interpretation block using current resource observations.

The corrected example disappears through this general pipeline: direct
constructor resolution/inlining exposes fresh Scope construction; the object
does not escape and its last field store has no reader; removing that store
makes the object, constructor and enclosing declaration unused. Detecting the
source example by spelling or retaining all calls as effects is not a solution.

Relocatable compilation may preserve definitions with a real linking/export
contract. Do not invent invisible exports for every unused declaration.
Executable reachability roots are the entry result, actual external effects and
declared exports, not all historical function-table entries. In a REPL, accepted
bindings are additional live roots because subsequent submissions can read them.
An optimizer used for standalone artifacts must therefore not silently delete
the REPL's persistent state. Debug spans are never semantic roots.

Contextual optimization is Native AOT-compatible partial evaluation, not runtime
machine-code generation. Persist fixed context, not credentials, handles or
filesystem observations as reusable proofs. Every actual execution observes
current external state at its selected block boundary.

## Directional barriers and work ownership

The Rust baseline defines these concepts in
[`barrier-execution.md`](barrier-execution.md),
[`barrier-process.md`](barrier-process.md),
`legacy/src/language/parser.rs`, and `legacy/src/structured/jobs.rs`.
Its old async/await spellings remain legacy-only; the current surface exposes
`release`, `acquire` and `acqrel` as control-flow keywords.

| Form | Required meaning |
| --- | --- |
| `release operation` | Before fence, then admit operation without its default immediate process join |
| `acquire operation` | Execute operation, then its after ordering/join requirement |
| `acqrel operation` | Both directional requirements |
| standalone `release` | Before fence, not a request to join all background work |
| standalone `acquire` | Join pending work owned by the current execution context |
| `acquire process` | Join that Process, not unrelated processes |
| unmarked external call | Launch and wait, preserving ordinary sequential command use |
| function return | Join outstanding programs owned by that invocation |

A Before/release fence prevents earlier work moving forward across it; later
work may move earlier if all other dependencies permit. An After/acquire fence
prevents later work moving backward across it; earlier work may move later.
An explicit process launch or join is also a temporal observation anchor.
Directional ordering is not equivalent to a blanket session-wide drain, and a
generic after fence is not the same operation as a standalone join-all.

This distinction permits a released server's output flow to remain live while
a later released client starts. Joining the server's output EOF before admitting
the client would introduce an artificial deadlock. A released process therefore
needs a retained completion/output owner, not an already-completed Process
snapshot. Reading a result member joins the necessary result; output streams
retain their actual ownership and completion boundary.

Within a selected block, evaluate scalar operands according to value/location
dependencies, derive concrete resource conflicts, and admit ready independent
I/O operations up to the existing resource budget. Resource conflicts include
read-after-write, write-after-read and write-after-write, endpoint/path overlap,
and relevant ancestor/tree operations. Do not assume distinct path strings imply
independence. Unknown program filesystem effects remain ordering anchors unless
the language has a real narrower contract. Do not authenticate for an unselected
branch or turn possible endpoint metadata into an executed request.

Own each pending operation once. A ready-node queue and unresolved predecessor
counts avoid rescanning all pending operations after each completion. Preserve
result/failure attribution by source identity even when completion order changes.
Ordinary cancellation does not silently become destructive child termination.
The current normal-copy/retry contract supersedes old persistent recovery
paragraphs in historical barrier notes; scheduling must not reintroduce them.

## Binary compatibility and integration order

Keep version 0.8.0. Bytecode encoding versions are independent of that product
version. Preserve old opcode numbers and append new operations. If an existing
opcode changes capture or constant ownership meaning, encode the new semantics
explicitly in a format/feature boundary: old artifacts retain their original
snapshot-capture behavior; new mutable captures use reference semantics.
Do not infer old/new intent from debug source or whether a mutation happens to
appear. Source-free read/execute journeys must cover both generations.

Implementation order follows the actual dependencies:

1. Storage identity, nullable field AST/contracts, assignment and capture IR.
2. Matching codec/validator/interpreter ownership semantics.
3. Shared dataflow/effect analysis and static optimization.
4. Contextual executable cleanup and finite dependency scheduling.
5. Process owners and directional barriers integrated through the same effects.
6. Editor, help/skill, formatting and diagnostics use the final source contract.

Discriminating acceptance uses real source-to-artifact-to-execution journeys:
alias field mutation; capture mutation versus shadowing; nullable initialization
then store; branch/loop joins; alias/callback invalidation; independent constructor
instances; recursive calls and annotated returns; dead pure declarations versus
live native effects; untaken SSH paths; source removal before `.mso`/`.mexe`
execution; two executions of one module without shared mutable state; persistent
REPL bindings; released producer/client and targeted joins. Compare optimized
and unoptimized observable values/effects using generated finite programs.

Measure fixed startup and submission latency separately from compiler/optimizer
allocation and scheduler scaling. A no-work executable should not allocate
transfer/process resources. One ordinary copy must not pay a large task-graph
setup cost. Many independent copies should not produce quadratic readiness
visits. Native Linux and Windows acceptance must execute actual bytes/processes,
not merely compile different runtime identifiers.

## Engineering and research basis

* [Wegman and Zadeck, 1991](https://research.ibm.com/publications/constant-propagation-with-conditional-branches)
  motivates propagating constant facts together with feasible branch edges.
  Here the practical representation can remain transient over stack bytecode.
* [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) provides the production
  model of explicit memory definitions/uses/joins and analysis invalidation.
  Adopt location/effect dependencies, not LLVM as a product dependency.
* [MLIR effects and speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
  separates resource effects from legality of speculative execution. This
  directly rules out treating an unused native query as a constant expression.
* [LLVM acquire/release motion](https://llvm.org/docs/Atomics.html) explains the
  one-way movement analogy used by the preserved Rust design. msync barriers
  are scheduling semantics, not C++ atomic synchronization or paired fences.
* The 2025 staged-compilation and relocatable-specialization work cited in
  [the architecture contract](architecture-v080.md#engineering-and-research-basis)
  motivates one semantics across portable and contextual stages. It does not
  justify persisting live filesystem facts or adding a JIT to Native AOT.
