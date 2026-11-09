# Typed HIR integration and release acceptance (0.8.1)

## Objective and baseline

This change introduces an actual semantic program representation between source
syntax and bytecode. It is not a bytecode dump, an AST replay container, or a
second wrapper around the existing emission-oriented frontend. The product must
separate semantic decisions from execution layout, preserve the released 0.8
language and editor experience, support semantic artifact export, and publish
0.8.1 after acceptance.

The starting checkout is `0d7dbdc`; the published 0.8.0 source tag points to
`637bd091e62e013f4003a1fa7e2c8f0b5e917140`. Its public packages are immutable
comparison inputs, not evidence that the new frontend works. Bytecode format 4
and its existing language-semantic contract remain independent of the new HIR
file format and product patch version.

## Completion requirements

| Requirement | Evidence needed |
| --- | --- |
| A real typed semantic model | Structured expressions/control flow, declaration and location identities, types/constraints, call and capture decisions, effects and source ownership are explicit without embedded bytecode or syntax replay. |
| Semantic analysis is independent of code generation | The public analysis path produces HIR without constructing bytecode instructions, stack slots, physical captures or jump patches. Editor requests use this path. |
| A one-way lowering boundary | A lowerer consumes a completed semantic module without AST nodes, mutable inference frames or callbacks into source binding. Lowering order does not change semantic results. |
| Generic analysis and queries do not clone an emitting compiler | Bound generic definitions, local substitution/constraint solving and reserved recursive identities replace deep speculative compiler snapshots and bytecode rollback. |
| Deferred execution has explicit semantics | Capture values versus locations, read time, current refinements and legal future storage domains are represented before lowering; no eager/deferred AST re-emission or mutable-call bytecode correction pass remains. |
| Layer ownership is documented | Every category of semantic information has an owner, a retention boundary and a justified discard point. Session transaction deltas are distinguished from speculative semantic queries. |
| HIR can be exported and consumed | A versioned semantic artifact round-trips, can be inspected, and can lower after original source deletion without rebinding or executing source. Session imports are explicit and never serialize live resources. |
| Existing language behavior is retained | Real source/object/executable journeys cover nullable flow, fixed storage, aliases, higher-order/recursive functions, delayed arguments, native domains, concepts, metadata queries, indexed storage and source diagnostics. |
| REPL and VS Code remain first-class | Persistent exported locations, failed-submission recovery, completion, hover, navigation, symbols, formatting, remote metadata selection and live Process interactions use the new semantic pipeline. |
| Compiler cost is measured | Matched baseline/candidate time and allocation observations include no-stdlib controls, selective modules, generics, structured objects, metadata and editor/session workloads; export and lowering costs are separated. |
| Native delivery is complete | New Linux/Windows Native images and the matching VSIX pass the selected real workflows, installation and actual editor-host acceptance. Public 0.8.1 assets, tag, source manifests and checksums agree. |

These requirements remain pending until their implementation and scoped evidence
are recorded. Existing unit names, green CI status or a plausible HIR class name
are not substitutes for inspecting the actual representation and data flow.

## Integration checkpoint: default frontend cutover

Commit `6403421` changes the public `Compiler.Compile` path to semantic analysis,
pure HIR lowering and the existing optimizer. It removes the old emitting
`Compilation` implementation, standard-library partials and editor snapshot
path. The semantic and backend boundaries are now production entry points,
not only an experimental export command. This checkpoint is not a release
acceptance verdict.

| Area | Established evidence | Still required |
| --- | --- | --- |
| Phase ownership | `6403421`; the focused review in `dce9379` closes formal caller projection, query output ownership and binding-time standard-module demand. | Remaining semantic counterexamples must pass through the default frontend. |
| Semantic files | `d5a8a7c` exposes export/import; `b3fe756` records source-deleted standard-library execution, deterministic repeated lowering and retained runtime source diagnostics. | Run the complete affected artifact journey against the cutover Native images. |
| Editor retention | `7a6230e` replaces whole-module retention with signature/type/source projections; held editor views permit collection of the original module and node arena. | Matched real editor cost and installed-host acceptance. |
| Session and language-server consumers | The semantic session bridge is committed in `fd87785`; consumer-owner reports cover the persistent-session and LSP journeys on frozen pre-cutover producers. | Revalidate affected integration paths using the new default-compiler producer; pre-cutover consumer evidence does not prove Native source compilation. |
| Compiler cost | Separate real Analyze, Lower and HIR serialization APIs have measured allocation and lifetime observations for small controls. | Representative standard-library/generic workloads, unrelated-world scaling and matched user-visible latency. |
| Delivery | Version/project locks and Native/editor release journeys are prepared. | Linux/Windows Native acceptance, matching VSIX, final provenance/checksums and immutable public 0.8.1 publication. |

## Integration checkpoint: composed semantics and bounded-growth diagnosis

The default-pipeline journey now has a concrete O2 result: frozen8 executes
source, deleted-source `.mso` and context-bound `.mexe` with 84 exact file
observations and six rejected invalid programs. The image identity and scope
are recorded in [the validation note](typed-hir-validation-v081.md). This
supersedes the earlier experimental-only source evidence, but does not establish
complete standard-library or Native acceptance.

The BTree input is a remaining release requirement, not an optional benchmark.
Frozen9 reaches semantic analysis but grows without completing even one direct
insert. The causal investigation separates repeated expansion of a small shared
graph in the specialization key from subsequent recursive Scope/resource-view
growth. The first boundary has a graph-definition/backreference correction in
`5a05220`; the second requires finite formal views and caller-specific resource
projection. [The focused review](typed-hir-review-v081.md) and bounded trace
notes record the evidence. Neither a partial key timing nor an unfinished run
is a whole-program speedup observation.

Open-session future callable definitions have their own
[signature-slot contract](typed-hir-session-dispatch-v081.md). Its runtime-only
tables, semantic obligations and ordinary lowering replace instruction repair;
closed file execution must remain unaffected. Final session and Native journeys
must use the completed frontend, not infer acceptance from hand-built modules.

Publication follows [byte-preserving candidate promotion](packaging-v080.md#candidate-validation-and-byte-preserving-promotion-081):
freeze the source, validate Native/editor candidate packages, measure the actual
candidate images, and only then publish those same bytes. No source or versioned
documentation commit may intervene between the accepted candidate and promotion.
Final release receipts can be committed after publication without changing the
tagged source or asset bytes.

## Correct execution is not a matched cost result

Frozen14 completes the original one-insert source and the loop-based 24-insert
source through default compilation and actual execution, producing `1` and
`17`. The earlier semantic-growth observations remain historical failure
evidence, not the current outcome. The expanded 24-call source used for the
published performance baseline is a different input; its byte identity must be
checked independently before comparing function counts or costs.

That exact expanded input revealed approximately 1.48 GB of candidate analysis
allocation, versus approximately 151 MB for the baseline's cold whole compile.
The single GC allocation trace recorded in `2ef67e4` attributes about 58% to
formal-input rewriting and 22% to future-domain enrichment. Code-schema frames
overlap formal-input frames and must not be added to them. This regression
remains an acceptance issue despite correct output and fewer generated bodies.

Local graph rewriting should preserve unchanged values and arrays rather than
rebuild them unconditionally. Roslyn's
[bound-tree list rewriting](https://github.com/dotnet/roslyn/blob/main/src/Compilers/CSharp/Portable/BoundTree/BoundTreeRewriter.cs#L29-L57)
allocates its replacement builder only after a changed item and otherwise
returns the original list. The same no-op discipline fits enrichment here;
completed-subgraph reuse additionally needs a call-local, role/substitution-aware
memo. A recursion stack alone does not reuse a shared DAG, and a global
caller-world cache would violate the semantic ownership boundary.

Formal-input rewriting now keeps completed subgraphs for one rewriting
operation, indexed by source-value reference and structural role. Location-only
callable environments can additionally share a completed rewrite by semantic
callable identity when every captured cell has an actual caller fact. Value
captures and missing-cell fallback snapshots retain reference/role separation.
This memo is neither a persistent caller cache nor a change to MAY/MUST alias
semantics. Native callables without captures and unchanged callable arrays are
reused. Frozen15's focused source-to-HIR-artifact-to-runtime journeys preserve
shared captured-field identity, distinct factory instances, alias-sensitive
flow, returned closure environments and repeated indexed-read aliases (14
assertions, including conversion and fixed nullable-carrier controls).

Invocation projection similarly reuses completed callable rewrites for one
invocation, using reference identity so distinct captured flow snapshots are
not collapsed. Capture and callable arrays are rebuilt only when a projected
item changes; native callables and unchanged scalar leaves remain shared.
Frozen16 repeats the same 14 semantic assertions after combining projection
sharing with role interning and enrichment changes. The combined candidate's
cost observation cannot be attributed to projection alone.

## Work ownership

The compiler team owns semantic contracts, binding, specialization and lowering.
The artifact team owns HIR persistence, source-free consumption and artifact
inspection. The experience team owns editor/REPL semantic consumers. Independent
validation owns representative real programs, architectural boundary evidence
and compiler cost observations. Release preparation owns version coherence and
the final immutable publication. The root integrates those contracts rather
than repeating every owner's accepted test matrix.

## Standalone dependencies versus logical locations

A logical location's binding role is not sufficient to determine whether an
exported module needs a live session. A standard-module reference can originate
as an imported name while its initializer and all of its dependencies are
already contained in the finalized program. Likewise, a closure's formal
capture describes a function boundary, not necessarily an external program
dependency. Standalone admission must use explicit unresolved module/session
dependencies, rather than reject every location with an import-like role.

The artifact journey must therefore include a selectively used standard module:
export its HIR, remove the original source, then lower and execute it without a
session. A genuinely unresolved session location remains an actionable error;
neither its current runtime value nor credentials are a substitute for an
explicit dependency contract.

## Allocation identity across session generations

A Scope construction has a semantic allocation-site identity independent of its
position in the Scope payload arena. Publishing a typed construction assigns
that identity; compaction, serialization and lowering preserve it. Backend
allocation mappings and runtime-feedback projection use the explicit identity,
not the construction's current dense array index. Otherwise pruning an unrelated
payload can reconnect a live REPL object to a different construction.

An allocation site is not an object instance. Repeated execution of an anonymous
construction creates distinct objects at the same site. A named Scope's
declaration-owned singleton identity is another separate contract. Neither
ordinary object identity nor capture-cell identity belongs in a code-sharing key
merely to recover facts that should be projected at each invocation.

Disposable reports and fixtures stay under `.temp/`. Baseline packages and final
publication payloads belong under `artifacts/` and `dist/`. Reusable mechanisms,
measurements and decisions belong in the focused English design notes.
