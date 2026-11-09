# Typed HIR and compiler phase ownership (0.8.1)

## Required boundary

The 0.8.1 migration separates semantic analysis from bytecode production. A
structured, typed program is the boundary, not a renamed list of VM operations.
The published 0.8.0 implementation and its source, artifact, REPL and editor
journeys are the behavioral baseline. This note records the implementation
contract; acceptance remains pending until the new pipeline exercises it.

```text
source -> syntax -> bound generic bodies -> semantic constraints/signatures
                                      -> finalized typed HIR bodies
                                      -> bytecode lowering -> optimization
                                      -> .mso -> context binding -> .mexe
```

`Msync.Hir` depends only on Core. It owns the immutable semantic model and a
pure lowerer. Compiler owns binding, inference, specialization and semantic
queries. Optimization and Runtime continue to consume the existing bytecode.
Serialization/printing consume HIR, not Compiler implementation objects.

## Information ownership

| Phase | Completes here | Retains for the next phase | Does not retain |
| --- | --- | --- | --- |
| Syntax | Token spelling, syntax structure, recovery ranges | Source identity, spans, syntactic declarations and expressions | No physical slots or executable assumptions |
| Lexical binding | Declaration identity, visibility, captures, native/concept references | Bound generic bodies with type variables and source-owned identities | No unresolved source-name lookup in later lowering |
| Semantic analysis | Fixed storage constraints, flow facts, conversions, call domains, generic instance signatures, effects and evaluation policy | Final typed nodes, signatures, locations, captures, effect/ownership annotations | No emitted bytecode, branch offsets or speculative instruction streams |
| Bytecode lowering | Frame/capture layout, evaluation-stack operations, control-flow offsets, iterator unwinding | Existing typed bytecode and diagnostics origins | No syntax tree, mutable semantic graph, type-checker callbacks or inference |
| Optimization | Existing target-independent transformations | Relocatable bytecode and required semantic flags | No source replay or live resource proofs |
| Context binding/runtime | Captured cwd/home, selected native work and actual failures | Executable bytecode and live resource ownership | No static filesystem existence/credential proof in reusable files |

A source range always includes its `HirSourceId`; declaration spans are never
paired with the current caller's text. `HirLocationId` denotes a logical
location, not a local-slot number. Its identity survives shadowing and includes
the source/generation identity in a persistent session.

## Model

The concrete model lives in `src/Msync.Hir/Model`. Dense integer ID types refer
to source, type, location, function, template and node tables. Nodes retain
`Block`, `If`, `Match`, `For`, `Loop`, calls, closures and deferred expressions.
Child edges and semantic payloads use arenas rather than a dictionary per node.

Types separate the closed Core runtime kind from structural rows, callable
signatures, Range elements and generic type-parameter references. Locations
carry their immutable storage type and null admission. A current flow type is
an analysis fact, never a mutation of the storage type. Effects identify
location/memory/external work, invocation, enumeration and synchronization.

Calls are resolved semantic operations: their argument conversions, native or
function identities, dispatch alternatives and evaluation policy are decided
before lowering. The lowerer does not rediscover these from a VM stack.
Closures distinguish captured values from captured locations explicitly.

Literals contain portable semantic data (including raw path/connection
spelling), not live Scope instances, cells, process handles or credentials.
Named Scope identity belongs to its declaration; anonymous instance identity
belongs to execution. Sharing a function body must not merge its objects.

## Generic bodies and semantic queries

An unannotated function remains generic. Its source body is lexically bound
once to an immutable semantic body with type variables and constraints.
Specialization substitutes types, structural shapes and callable definitions
into that body. Recursive instances reserve a semantic function identity before
their signature fixed point is solved. Recursion is not repeated source
substitution or a reason to impose an iteration quota.

Code identity does not include ordinary captured cell/object instance identity.
Two closures with one definition and the same semantic capture types can share
code while their capture descriptors still reference different runtime values
or locations. Associated Scope field contracts and declaration-owned singleton
constraints remain separate from this code-sharing choice.

`decltype`, invocation predicates and concept checks inspect bound bodies and
type schemes using a local substitution/constraint workspace. A failed query
must not mutate committed generic instances, alias contracts, Buffer carriers
or session declarations. It does not clone the whole compiler graph, emit a
scratch bytecode function or roll back an instruction stream.

Ordinary control-flow joins and recursive signature fixed points remain real
analysis. The goal is to remove speculative *compilation* and later repair,
not to pretend that type inference never needs a worklist.

### Instance code versus call-site provenance

`SemanticAnalyzer.Instantiation` stores code promises in formal location and
region space. A completed promise retains its typed result scheme and an
immutable, reachable-only graph summary, not the first caller's context. Every
invocation binds its arguments and captures to that formal graph, projects input
aliases back to caller objects/cells, and gives fresh returned allocations their
own identities. Fresh Scope fields and returned closure captures use one shared
location projection: `object::field = value` and a getter capturing that field
must observe the same cell, without sharing cells with another constructor call.
Scalar-only results bypass graph projection allocation.

The code key includes semantic types, callable definitions, denoted types,
evaluation policy and canonical alias partitions. An alias partition uses
encounter-order ordinals, never concrete allocation/location IDs. The partition
matters when a write through one parameter can invalidate a nullable-field proof
through another; unrelated objects with the same topology still reuse code.
Runtime scalar literals are not code specialization facts, so a first `true`
argument cannot remove the `false` caller's branch.

Type-only calls reserve promises in a query-local workspace, importing only the
callee/argument-reachable semantic graph. They neither consult nor populate the
published code cache. Unannotated recursive calls establish a type-only signature
before executable typed nodes are emitted; annotated recursion can immediately
use its declared result. No completed function body is patched after another
call. Boundary Path/String conversions are explicit typed prologue operations:
actual incoming parameters and converted lexical storage are distinct only when
conversion is necessary, including native callbacks and returned captures.

## Deferred reads

Three types are distinct:

1. the immutable type admitted by a location;
2. its current type on a particular control-flow edge;
3. the legal type when a deferred read eventually occurs.

For a stable private location, the third may use the current refinement. A
writable capture, open REPL root or mutable field must retain its legal future
domain. Scope field mutation is not ruled out merely because the Scope-valued
parameter itself is unwritten.

A generic return-type scheme provides a cheap shape projection before a call
is materialized. Evaluation policy is resolved against the relevant read
domain, then a single typed call/defer operation is published. The old sequence
of emitting a current-value call, probing a second AST compilation in deferred
mode, and repairing instructions is not the architecture.

The established cases remain discriminators: a `lines` function mapping every
Path/String/Range alternative to Range stays lazy; an identity over a writable
Range/String capture does not promise Range and later fail inside the VM;
guarded stable nullable locals retain their proofs; genuinely writable future
Null remains admitted. Caller argument order and once-on-demand evaluation do
not change to recover a type proof.

## Sessions and editor views

REPL submission is a semantic generation plus a pending declaration/fact delta.
Committing or reconciling a partially executed submission is a real session
transaction, distinct from a type-query probe. The backend owns the mapping
from semantic location/function identities to physical slots/code indices.
Runtime value feedback crosses that mapping; the binder does not infer identity
by comparing a new value against a template's old AST or prototype location.

Editor queries project immutable semantic observations, lexical visibility,
receiver/callable/type views and source links. Error nodes retain usable types
and context. Uncalled generic bodies use their bound constraints/type variables,
not a bytecode-producing `ProbeUncalledFunctions` pass. Hover/completion never
invokes the bytecode lowerer or submits a session generation.

## Retention and export

The default compile path need not retain every representation for the entire
program. Source syntax can be released after lexical binding; fixed-point and
query scratch state is released before finalized bodies leave semantic analysis.
Typed bodies can be lowered and released by body when the driver does not need
an exported full graph. Editor requests keep the projections they need, rather
than mutable frame/callable/storage clones. Export explicitly retains the
self-contained semantic graph required by the file.

The new `.mhir` container has its own format version. It supports binary export,
read-only text/JSON inspection and source-free lowering from a loaded finalized
module. It is not an AST cache and contains no bytecode payload to replay. The
existing `.mso`/`.mexe` format 4 and language-semantic version remain independent
of product version 0.8.1. All required type/shape/capture/source tables travel
with the HIR file; unsupported or open entry bodies cannot be silently dropped.

## Engineering basis

Rust's [HIR](https://rustc-dev-guide.rust-lang.org/hir.html) motivates explicit
definition identities and a compiler-friendly form after lexical work. Its
[THIR](https://rustc-dev-guide.rust-lang.org/thir.html) motivates typed,
body-oriented arenas and releasing temporary typed bodies after their consumers
finish, instead of permanently retaining all compiler representations.
Roslyn's [bound nodes](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/BoundTree/BoundNodes.xml)
and [nullable flow analysis](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/FlowAnalysis/NullableWalker.cs)
motivate keeping bound operations, storage annotations and current flow facts
separate. These are mechanisms to adapt to msync, not new runtime dependencies.
