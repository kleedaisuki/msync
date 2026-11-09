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
