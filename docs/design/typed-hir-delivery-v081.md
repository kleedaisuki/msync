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

## Work ownership

The compiler team owns semantic contracts, binding, specialization and lowering.
The artifact team owns HIR persistence, source-free consumption and artifact
inspection. The experience team owns editor/REPL semantic consumers. Independent
validation owns representative real programs, architectural boundary evidence
and compiler cost observations. Release preparation owns version coherence and
the final immutable publication. The root integrates those contracts rather
than repeating every owner's accepted test matrix.

Disposable reports and fixtures stay under `.temp/`. Baseline packages and final
publication payloads belong under `artifacts/` and `dist/`. Reusable mechanisms,
measurements and decisions belong in the focused English design notes.
