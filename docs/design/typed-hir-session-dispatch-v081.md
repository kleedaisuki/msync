# Open session callable signatures

A closed source unit has a finite callable-write domain. A persistent REPL does
not: a later accepted submission can assign a new function definition to a cell
already captured by a deferred Range. The earlier expression must still read
that cell once, when consumed, rather than freeze the function that occupied it
when the Range was created.

## Ownership and representation

| Layer | Owns |
| --- | --- |
| Semantic analysis | A code-owned call-signature slot, its parameter/result domains, formal binding obligations and ahead-of-execution specialization of newly assigned definitions. |
| Invocation projection | Mapping a formal captured cell or field path to each actual caller's location; no object identity enters the shared-code key. |
| Session generation | A fork of signature registrations and target additions, committed alongside the accepted/pending semantic generation. Failed compilation publishes neither code nor registry updates. |
| HIR lowering | Mapping semantic callable/function IDs to physical IDs and constructing the runtime-only dispatch tables. |
| Runtime session image | Looking up the actual Function value in the latest published signature table. It never calls the source compiler. |

`HirCall.SessionSlot` and `HirFunctionAdaptation.SessionSlot` reference
`HirSessionDispatch` entries. These contain only type and code identities and can
be inspected in semantic HIR. Lowering them requires an active HIR session.
The lowered `Module.SessionDispatches` table belongs to the runtime session and
cannot silently disappear into a standalone format-4 object file.

The existing bytecode representation uses negative `Call.B` and negative
`SpecializeClosure.A` for session slots. Nonnegative operands retain their
existing meaning. Ordinary closed programs have no table and perform no
registry lookup. An old deferred interpreter refreshes its shared SessionImage
before resolving a slot, while keeping its original physical path context.

## Constraints and effects

A new assignment must satisfy the parameter/result obligations of existing uses
before execution. The compiler adds the required concrete targets during that
submission's semantic analysis. It does not patch an earlier instruction stream
or replay a generic body's syntax tree.

The slot is code-owned; the captured cell is not. Returned Range values retain
formal callee/argument binding descriptors, which invocation projection maps to
actual locations. Sparse cell obligations are projected independently from the
cached function body. Imported caller obligations must not be frozen as if they
were newly established by a shared body.

Open calls and callback adaptations are unknown calls for optimization. A
currently pure target cannot justify constant evaluation, inlining or a
no-Process proof about definitions introduced later. This does not disable O2
for the rest of a session. At enumeration, semantic Range bindings resolve the
current target effects so a new captured write cannot leave an old literal
refinement behind.

Static call obligations survive a successfully compiled submission's runtime
failure, as in the published 0.8.0 implementation. An uninitialized new export
still does not become an accepted binding. These are distinct rules; collecting
runtime ownership roots is not a substitute for the static signature contract.

## Focused source acceptance

The real `CompilerSession` / `ExecutionSession` host passed 33 assertions on the
frozen9 producer, then 15 affected factory/path assertions on frozen11, all at
O0/O2/O3. The latter cover two objects sharing constructor code, a setter
capturing a private cell, replacement of a Scope-valued root, and lexical
shadowing. Runtime-only table mechanics and optimizer opacity have separate
focused tests; they do not stand in for these source journeys.

One distinct latent boundary was checked against the published 0.8.0 executable:
reassigning a closure from the same lexical template with a different captured
runtime kind already mis-specializes there. This work does not claim to fix that
older capture-variant issue or introduce a general runtime shape/JIT subsystem.
The disposable comparison is under `.temp/hir-session-dispatch/capture-variants`.
