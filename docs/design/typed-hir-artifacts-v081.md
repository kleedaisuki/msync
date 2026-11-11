# Typed HIR artifacts and inspection

## Boundary

`.mhir` persists the compiler's semantic program, not a disassembled instruction
stream or an AST accompanied by source to evaluate again. A loaded artifact is
an input to the same pure HIR-to-bytecode lowerer used after source analysis.
Deleting the original source must not prevent lowering, optimization, binding
or execution of a self-contained exported program.

The language keeps three distinct persisted stages:

| File | Meaning | Context |
| --- | --- | --- |
| `.mhir` | Typed semantic definitions, expressions and capture/location identities | Independent of bytecode slots and physical execution context |
| `.mso` | Relocatable typed bytecode | Execution context remains symbolic |
| `.mexe` | Context-bound executable bytecode | Retains the selected binding context, not live external observations |

The HIR container starts at format 1. Existing bytecode remains format 4.
Language semantics stay `0.8.0`; the product release is `0.8.1`. These versions
are separate contracts. Introducing HIR does not invalidate published bytecode
by changing its language version string merely to match the product version.

**0.8.6 compatibility note:** the current bytecode contract is format 5. The
connection-first source operator adds the `QualifyOn` node to the format-1 HIR
kind table, retaining the old `Qualify` node for earlier HIR. Lowering evaluates
children in the node's recorded order. An older reader rejects an unknown node
tag; a new HIR file that uses `QualifyOn` is therefore not forward-readable by
0.8.5. Both forms lower to the same format-5 bytecode operation, so existing
executable artifacts retain their recorded operand order.

## User workflow

```text
msync compile sync.msync --emit hir -o sync.mhir
msync disassemble sync.mhir
msync disassemble sync.mhir --json
msync compile sync.mhir -o sync.mso
msync bind-ir sync.mhir -o sync.mexe
msync run sync.mhir
```

An output ending in `.mhir` selects HIR emission when `--emit` is not supplied.
Explicit `--emit bytecode` retains normal bytecode emission. Without `-o`, the
compile command prints the selected representation; `-o -` writes binary bytes
to stdout and keeps diagnostics on stderr. Reading an artifact for inspection
does not initialize runtime execution, bind paths, authenticate or perform
filesystem observations beyond reading that artifact.

### Artifact inspector in 0.8.6

The custom editor is a stage-aware, read-only inspector for all three binary
formats. The stage, format and language are explicit in the header; the
outline groups declarations by their actual semantic roles. Select a callable,
block, plan, or transfer to jump to its line. Select a line number or use the
listing's arrow keys to inspect its annotations and references. Executable
plans show owner, region, batches and eliminated-transfer count; individual
operations show mode, predecessors and the declared read/write resources.
Predecessors link to the corresponding transfer, not to guessed source syntax.

Use **Search entire artifact** for literal, case-insensitive search across
pages, **F3** / **Shift+F3** to move between matching lines, and **Go** to jump
to a line. A result or definition can target any line, not just a mounted page.
**Open IR as Text** retains the native read-only editor for whole-document
selection, native search options, hover, and definitions. Outline, listing and
details scroll independently; both side panes can be hidden when the workbench
has little space. Narrow editors move details below the listing.

The renderer receives one decoded schema-1 model, with no additional binding
or execution request. It mounts up to 160 listing lines, 64 outline entries,
and 24 entries in each details list. Those are presentation pages, not limits
on the decoded artifact or search results. The initial view still reads and
indexes the model in full; it does not claim streaming native decoding. Search
scans the plain lines in frame-sized slices and superseded queries stop
publishing results. A refresh retains the query, panels and selected location,
anchoring to a matching declaration where possible. Reference actions carry the
rendered generation, so an old panel cannot reinterpret its reference index
against a newer model.

Presentation follows the official [VS Code webview guidelines](https://code.visualstudio.com/api/ux-guidelines/webviews)
and [theme and security guidance](https://code.visualstudio.com/api/extension-guides/webview).
Theme tokens, keyboard focus and textual labels work in light, dark and
high-contrast themes. Artifact data remains inert JSON; code spans are escaped,
and details construct text nodes instead of accepting arbitrary Markdown HTML.
Only explicit source-reference actions reach the host's existing URI allowlist.
The nonce CSP and empty local-resource roots are unchanged.

The focused **artifact-ui** extension-host journey exercises the actual bundled
webview, using the native reader rather than a mock. With a frozen executable
and a pinned VS Code installation, run from `editors/vscode`:

```text
npm run build
MSYNC_TEST_ARTIFACT_UI_ONLY=1 MSYNC_TEST_SERVER_PATH=/absolute/path/to/msync \
  MSYNC_TEST_VSCODE_EXECUTABLE=/absolute/path/to/code xvfb-run -a npm run test:integration
```

It captures dark HIR, light object search, and high-contrast executable-plan
details; checks cross-page keyboard search, refresh state, source jumps and
plan predecessor jumps; and verifies exact input hashes, read-only text, and
absence of the planned output. The loopback browser debugger is enabled only
by this isolated test runner, never by the shipped extension.

The inspector journey passed in VS Code 1.144.0 with the published 0.8.5 Native
reader. Its real relocatable object has 1,887 lines; search crosses the first
160-line page and returns to the same selected result after refresh. The
existing source-free HIR and artifact host suites also passed automatic
publication refresh, invalid-file recovery, and inert source metadata checks.
Captures and exact source/reader receipts are retained under
`artifacts/candidates/ergonomics-086/artifact-inspector/`.

## What survives serialization

Semantic arenas use stable module-local IDs rather than object addresses.
Sources, types, definitions, logical storage locations, function identities,
capture modes, structured nodes and control-flow/effect information retain
their explicit relationships. Field selection and calls must carry the
semantic information required by lowering, without looking names up again in
a mutable caller environment.

Finalized executable specializations are self-contained. Generic definitions
retained in the model must have semantic bodies and constraints, not hidden
syntax pointers; they are not an excuse to invoke the old binder after loading.
An export cannot silently omit a required function, shape, template or capture.

Source text, when retained for diagnostics, is data only. Locations refer to
source identities and spans; they are not raw offsets paired with whichever
source document happens to be current. Absence of the original file must not
cause source replay or erase semantic definitions.

Allocation sites also have semantic identities independent of a compacted
payload table's dense index. A typed construction publishes one site, retained
by sealing and serialization; backend receipts map that site to a physical
runtime allocation marker. Generic specializations with different field
contracts have different typed sites. Repeated execution still creates fresh
anonymous objects: sharing a site does not merge their instance identity.
Named singleton declaration identity is a separate relationship.

Live cells, Process controls, pending tasks, credentials and authenticated
connections do not belong in an HIR artifact. A session export with unresolved
locations must declare its imports; it is not a serialized REPL heap. A
standalone lowering or execution request reports those required imports rather
than inventing values or replaying submission history.
`HirModule.UnresolvedImports` names these external locations explicitly. A
location classified as an import is not by itself unresolved: standard-module
cells initialized by the program's semantic prelude are ordinary resolved
members of that program and must not be rejected as live session dependencies.

An open-session callable also carries an explicit semantic dependency. Its call
or callback-adaptation descriptor refers to a stable session dispatch slot; the
module records that slot's parameter/result types and semantic callable targets.
This is inspectable metadata, not serialized live dispatch or closure state.
Slots retain their complete session-table positions rather than being
renumbered during sealing. Such a module remains read-only outside its owning
session: standalone CLI lowering reports the live callable dependency instead
of freezing a future callee or inventing one.

## Representation and inspection

The binary codec uses explicit semantic tags and ID tables. It does not use a
general reflection serializer, encode CLR object identity, or embed an `.mso`
payload as the supposed high-level representation. Floating-point literal bits,
including signed zero and NaN payloads, remain exact. Structural validation
checks the file's actual references and invariants without arbitrary source or
node quotas.

The native inspection API keeps the existing artifact-view schema and adds the
optional `artifactKind: "hir"` discriminator. Existing bytecode models remain
compatible. HIR output describes semantic definitions, types, expressions,
locations and captures rather than pretending that bytecode slot numbers or an
optimization level are HIR properties. VS Code uses the same read-only native
view, source links and file-change notification ownership as existing IR files.

## Artifact acceptance

The CLI journey on the managed frozen4 producer covers standard-module
initialization, generic control flow, independent constructors with shared
captured locations, and concept/metadata composition at O0/O2/O3. Each source
is exported and deleted before semantic-artifact inspection and lowering.
Loaded HIR produces byte-identical `.mso` output to direct source compilation;
`.mso`, context-bound `.mexe` and raw HIR execution produce the same expected
bytes: 12 routes and 54 exact byte observations. Export and inspection are inert.
A selected failing transfer after source deletion retains its original source
line and caret in the runtime diagnostic.

`tests/Msync.EndToEnd/hir_artifact_journey.py` owns this repeatable user journey.
The frozen producer's six-assembly receipt and O2/complementary O0/O3 reports
are retained under `.temp/hir-artifacts` for this investigation; these are
disposable evidence, not product inputs. The focused codec suite currently
passes 340 contracts, including exact literal bits, canonical read/write bytes,
stable allocation-site identity, malformed references, and evaluated dynamic
Type-valued `is` targets. Both static one-child type tests and evaluated
two-child type tests are semantic operations, not a reason to replay source.

NativeAOT packaging and the real VS Code read-only HIR viewer use their
registered release journeys. The managed producer results above do not claim
that those platform/package acceptance runs have completed.

## Engineering basis

[MLIR bytecode](https://mlir.llvm.org/docs/BytecodeFormat/) separates container
versioning from the operations and types represented in it. That distinction is
useful here without introducing an MLIR runtime dependency.
[rustc serialization](https://rustc-dev-guide.rust-lang.org/serialization.html)
shows why arena references require an explicit reconstruction context rather
than serializing process addresses. msync uses its own module-local ID tables.

Native AOT favors an explicit codec and structured text writer. If JSON source
generation is used, its closed type set must be supplied explicitly; the
[System.Text.Json source-generation contract](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
does not make arbitrary object-valued graphs statically serializable.
