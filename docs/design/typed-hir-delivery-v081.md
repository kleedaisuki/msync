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

## First Native candidate: rejection and shared semantic corrections

The validation-only package run `42730780886`, from frozen source `d7c1de5`,
built Linux and Windows Native executables successfully. Both platforms passed
six selected boundaries, including the actual terminal journeys, but rejected
the same custom-Map library-policy input. No version tag or public asset was
created, and the installed-editor job did not run after its prerequisite failed.

The failure also reproduced in the managed compiler. The dependent result of
Range enumeration had been degraded from a non-null record with a possibly-null
payload to a generic nullable item. Further acceptance of that same library
phase exposed two other semantic boundaries: discarding a value must preserve
`Never`, and an unannotated storage declaration must infer its complete analyzed
initializer type rather than freeze a provisional compound binder hint.

`c44b487` centralizes the dependent native result rules and bottom-type law and
preserves the distinction between inferred storage and explicit constraints.
The unchanged collections and services inputs then passed their three O2
source/object/executable routes each: 75 exact file observations and six negative
cases in total. Immutable build-input manifests and matching PDB checksums
identify these corrected producers. These managed results authorize a new
Native candidate; they do not replace its platform and installed-editor tests.

Raw package retention on failed Native jobs is now explicit. Failure remains
failure for promotion: exact source, both Native jobs, editor acceptance and
payload receipts are still required before any public release.

## Second Native candidate: editor metadata and short-program cost

Validation-only run `42736530733`, from source
`09ca9d780cc51d46dc66206f12de9393d1068262`, passed all seven selected Native
phases on both Linux and Windows, including real terminal and Windows installer
upgrade journeys. The subsequent actual VS Code source suite failed its
`with_equality` hover assertion: the signature displayed `Scope` instead of the
declared `Equality` concept constraint. Earlier managed protocol tests did not
cover this exact packaged-editor observation. The candidate is rejected; there
is no matching accepted VSIX or public release.

The retained Linux executable also exposes a user-visible short-program cost
regression. For the unchanged List source with SHA256
`8a6688070adf66d76a1b4248e67f1ddee360a9e086b296d4cc07d22c1e6ff3ab`,
15 alternating fresh-process pairs compile at O2 to the same already-existing
output path. Published 0.8.0 has median 46.91 ms and p95 63.76 ms; this candidate
has median 76.48 ms and p95 86.80 ms. Every candidate pair is slower; the median
paired difference is +25.45 ms. The primary operation is `compile`, not `check`:
the latter no longer has the same phase scope in the two implementations.
The exact images, commands, samples and payload provenance are retained in
`artifacts/candidates/v0.8.1/09ca9d7/native-list-paired.json`.

A seven-pair `--version` control has a +2.95 ms median paired difference. It
indicates that startup contributes, but does not explain the complete compile
regression or identify a particular semantic function. Large-tree improvements
likewise do not substitute for repairing this short-program result. The bounded
follow-up removes false recursive-signature prequeries and avoidable schema
materialization while preserving real recursive/native-callback paths and
canonical specialization-key equality. Its effect must be measured on a new
coherent producer and subsequently on the actual Native candidate.

The editor correction belongs in the compact semantic projection: retain the
concept constraint and useful declaration metadata without retaining HIR bodies
or mutable inference contexts. Client text patches and weakened assertions are
not fixes. Neither follow-up changes the release requirement for a matching
VSIX, real isolated installation/activation, and byte-preserving promotion.

## Third Native candidate: measured trade-off and remaining editor failure

Run `42740109409` validates source
`9f4856adeb8f70b38a1446b458be619893096fe8`. Both Native jobs again passed
all seven selected phases. The original Equality hover assertion now passes,
but the same actual VS Code suite reaches a further failure: the initialized
`tree` binding is displayed as `unknown` rather than `Scope`. Replaying the
complete fixture through the actual Native LSP exposes an earlier E0214 at
`Set::new`: the `StrictOrder` contract requires `sample: Number?`, but its
inferred actual field has degraded to `Any?`. Analysis stops there, so the
unknown hover is a bound fallback, not a presentation defect. The compiler must
retain the policy field's type; forcing the hover to say Scope would hide the
underlying error. The candidate remains rejected; the subsequent editor suites
and VSIX packaging have not run. The next local check must replay every assertion
and require zero diagnostics and successful check/run for this exact small
fixture before another Native freeze.

The Linux payload's SHA256 is
`83f9c206904dfdd4ee1a1eae7d066d0fe59faa85caf210aa7b46139f8ddc7494`.
Repeating the same 15-pair actual-compile procedure gives baseline median/p95
42.34/48.50 ms and candidate 51.64/56.61 ms. The median paired difference is
+8.14 ms; 13 of 15 candidate samples are slower. The retained report is
`artifacts/candidates/v0.8.1/9f4856a/native-list-paired.json`. The earlier
+25.45 ms candidate result was not rerun, so the two historical paired deltas
are not a controlled attribution to a single code change.

The intervening immutable managed comparison does isolate the combined fixed-
work correction on the same semantic baseline: warm allocation decreases from
6,642,096 to 5,804,832 bytes, with identical 37-function, 330-instruction,
11,860-byte output. The bounded recursion and key changes therefore remove
measured work without changing the resulting program. They do not establish
zero overhead for a structured semantic phase.

The remaining approximately 8 ms Native short-program cost is an explicit
architectural trade-off, not performance parity. It is materially smaller than
the earlier regression and is acceptable alongside the independent large-tree
and incremental-editor results; it must remain visible in the final performance
account rather than be hidden by those improvements. The unresolved editor
semantic defect, matching VSIX installation and immutable publication still
prevent completion independently of that cost decision.

### Complete-fixture corrections before the fourth freeze

The unchanged 1,015-byte editor program (SHA256
`ffac7323348ab1f3875b02c24ee044652cbeffd10e0b0ebc0528244afe091a3d`)
exposed three successive compiler defects, rather than three presentation
problems:

* A genuinely pending nullable field can learn its concrete concept storage
  constraint. Already-fixed broad or incompatible storage cannot be retyped;
  query checks own their substitutions and sealing effects.
* Future callable environments retain the actual logical capture location of a
  provably single program/module-owned declaration. Synthesizing another cell
  for that same declaration lost method identities. This normalization belongs
  at future-environment construction, not at member lookup. Private factory
  captures remain distinct, and a shared root capture reads current storage
  after reassignment rather than freezing the initial singleton value.
* An executable function body is not its joined return-value summary. Explicit
  returns are converted at their own control-flow edges; the fallthrough body
  is checked and converted independently. Joining return facts must never replace
  the body with an inner return operand. The defect had reduced an annotated
  loop function to a read of an uninitialized local. Cached return summaries now
  retain no executable node.

Immutable producer 27 (Compiler SHA256
`50621becc03bd1f8e31d0f42a74381423d650780a45267a991cad8895647ba57`)
passes the entire original fixture: completion applies the exact edit, the new
document generation has zero diagnostics, all six hovers and three definitions
are correct, the 17 semantic roles and qualified concept completion agree, and
the original check/run commands produce `Klee`, `3,1` and `1,3,4`. The same
program also exports to `.mhir`, survives deletion of its original source, lowers
to `.mso` without executing effects, and produces the same three outputs.

These observations close the local full-fixture failure, including actual
execution, rather than merely repairing each displayed hover in isolation.
They authorize a new immutable Native/editor candidate; they do not establish
that its platform packages or installed extension have already passed.

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

## Completed publication and requirement audit

[0.8.1 is published](https://github.com/kleedaisuki/msync/releases/tag/v0.8.1)
from immutable source `454b56cfbb6f0c641f0991b8014492ec9cd39eea`.
Main was fast-forwarded to that exact accepted commit before publication.
[Candidate run 42748756872](https://github.com/kleedaisuki/msync/actions/runs/42748756872)
passed both OS Native jobs and the three actual VS Code suites.
[Promotion run 42749898704](https://github.com/kleedaisuki/msync/actions/runs/42749898704)
published the same bytes without rebuilding.

| Original requirement | Completion evidence |
| --- | --- |
| Actual structured semantic model and phase ownership | Core-only `Msync.Hir`, typed control/call/location/effect tables, pure lowerer, and the implemented ownership/discard contract in `typed-hir-v081.md`. |
| Analysis independent of bytecode | Default cutover `6403421` removes the old emission frontend; `Compiler.Analyze` and editor consumers analyze bound semantic bodies, without instruction/slot generation. |
| Queries and generics without speculative emitting snapshots | Explicit output capabilities, local fact/substitution workspaces, reserved recursive identities and formal invocation projections; query ownership and source-free lowerer journeys exercise the boundary. |
| Deferred execution without later bytecode repair | Explicit evaluation policies and captured locations, canonical program capture normalization, and the separate persistent-session signature-slot/runtime dispatch contract. Private environments remain distinct. |
| Export, import and source-free HIR use | Independently versioned `.mhir` format 1; both-platform `hir-artifact` acceptance, the complete source-deleted Set program, and actual HIR editor-host acceptance. Live imports/resources are not silently serialized. |
| Existing semantics and REPL | Both platforms pass `hir-semantics`, `hir-artifact`, `concepts-tooling`, `library-policy`, `type-traits`, `shell-repl` and `artifact`, including PTY/ConPTY. Focused pending/fixed storage, query isolation, recursion, aliases, captures and annotated-return controls close the observed regressions. |
| VS Code experience | Original source/concept, bytecode and HIR suites run against the exact Native payload. Separately, a real CLI-installed VSIX activates from its installed directory, uses that Native LSP, provides source hover and opens source-deleted HIR read-only. The development extension in that last test is only the probe, not msync. |
| Compiler time and memory | Matched phase/allocation observations, source-identified larger-workload results, and the final same-payload Native compile/LSP observations in `typed-hir-validation-v081.md`; the approximately +8 ms short-program cost and cold-hover cost remain explicit. |
| Complete immutable delivery | Four published payloads match the accepted candidate byte-for-byte after actual public redownload. The published tag points to the frozen source; SHA256SUMS covers exactly the four payloads. Existing 0.8.0 tag and asset digests/sizes remain unchanged. |

The Linux package SHA256 is
`97476aaa6cae58cfb4ee48d13928b194c15d8a75fc650ee36776a9fd1b2fbbbc`;
the matching VSIX SHA256 is
`2752542aeb299404aab0ba18efd37ad4d9989ec0537f9f7104effe08d61d6fb6`.
The complete publication receipt, all four package identities, actual public
downloads, install/activation evidence and timing receipts are retained at
`artifacts/candidates/v0.8.1/454b56c/`. The release uses product 0.8.1, language
0.8.0, existing bytecode format 4 and new HIR format 1.

VS Code adds an installer-owned `__metadata` property to the installed
`package.json`. Installed verification compares the complete authored manifest
after removing only that property, records the metadata and both raw hashes,
and byte-compares all eight other product members. Rejecting that documented
installation transformation would test archive spelling, not product integrity.
The successful real installation receipt records this distinction explicitly.

This post-publication record does not move the tag, modify asset bytes or change
the accepted source. It closes the requirements above, rather than treating the
earlier partial fixtures, rejected candidates or managed-only checks as release
acceptance.
