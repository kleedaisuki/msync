# Reusable staged intermediate representations

## 0.5 structured artifacts

The CLI IR command names and REPL export commands are retained. New exports use
`msync-ir` envelope version **2**, stage `structured`, wrapping the validated
`ObjectModule` used by `compile -o`. Macro expansion, literal data, exact-argv
program calls, directional barriers, source maps and context relocations are
therefore shared with ordinary 0.5 execution, not replayed through the old parser.
The object itself keeps its independent format/language version checks.

In 0.5.2, symbol calls and source directives are already expanded in this object.
Its optional `source_modules_v1` manifest preserves child and captured `$file`
contexts without reopening scripts. Relocatable IR retains those contextual
bindings; contextual export resolves them just like other context anchors.
See [source-module binding](object-context-v5.md#expanded-source-modules-in-052).

`Structured.context == None` is relocatable. `bind-ir` or contextual export
instantiates symbolic roots and freezes local cwd/HOME/ENV/PATH bindings into
physical operands, retaining the original compile context. Unknown filesystem
kinds, runtime optimization premises, authentication sessions and private recovery
state are never serialized. A REPL export of a submission referencing a process
owned by an earlier submission is rejected as non-standalone, without rejecting
the original valid in-session operation.

A dedicated version-1 endpoint wire adapter keeps previously exported flat
artifacts readable and executable under their original semantics. Their
externally tagged backend representation must not change just because the new
structured object uses a `kind` discriminator. The public 0.4.0-generated fixture
in `tests/fixtures/ir-v0.4.0.json` exercises this actual compatibility boundary.
The original finite-copy API remains available; its compatibility projection of
structured IR rejects program/literal/barrier effects instead of discarding them.

This separation follows [MLIR's format-version versus dialect-semantics distinction](https://mlir.llvm.org/docs/BytecodeFormat/):
container compatibility alone does not authorize changing executable meaning.

## Version-1 binding contract

The compiler now exposes a distinct name-resolved relocatable representation.
This is not serialized source, an AST replay, or a serialized optimized Plan.
`Compiler::compile_relocatable(&Script)` resolves endpoint aliases and path-value
aliases, validates the normal frontend rules, removes declaration instructions,
and emits flat transfer instructions with typed endpoint and path roots:

```text
source → parser → Compiler::compile_relocatable → Relocatable
                                                    │ bind(CompileContext)
                                                    ▼
                                        Contextual (Program + context)
                                                    │ prepare live/offline
                                                    ▼
                                             fresh Plan → execute
```

Each portable path has a physical backend (or endpoint-relative default), a root
(`literal`, `pwd`, `file`, `file_parent`) and path text/suffix. Selectors are
separate validated regex operands. `$pwd`/`$file` references in this compilation
remain symbolic; name resolution is finished. Rebinding an artifact does not
reparse source or re-resolve binding names. Path values captured by an *earlier*
REPL submission stay captured, even in relocatable output, preserving established
binding-time semantics. No global process environment or compiler state is mutated.

Frontend validation uses a synthetic absolute context as a type witness, never
reads its metadata and never emits its paths. It validates item names and
file-versus-directory suffix rules while deferring actual `$file` availability
until binding. A portable script using `$file::this` can therefore be compiled
without a named source; binding it without an explicit source-file context fails.
This avoids substituting cwd or accidentally treating the JSON artifact filename
as the original script filename.

Binding produces contextual IR: local relative paths become absolute against the
supplied cwd, local HOME/ENV/PATH lookups are resolved, and the supplied logical
source-file identity is recorded. Physical remote relative and home paths retain
their existing endpoint semantics; this is not a remote filesystem snapshot.
Contextual IR does not relocate when loaded under a different cwd. It is an
execution description, not a REPL history/bindings backup, file-content archive,
SSH authentication cache or proof that endpoint state has not changed.

Neither representation serializes file-kind observations, file identity hints,
optimization proofs, resolved dependency edges, tasks, handles, cancellation
state, passwords or connected SSH sessions. Every run prepares again. In
particular, a contextual artifact whose source was deleted must fail on reuse,
not run on an imported stale `Regular` classification. Optimized-away operations
are not discarded from the saved pre-planning contextual program.

`scheduler::prepare_snapshot_spanned` returns a `Prepared` pair: the normal plan
and the resolved, **unoptimized** input program produced by that same preparation.
Existing preparation entry points still return `Plan`. This lets the interpreter
retain evaluated lookup values without a second metadata/PATH pass, and without
accidentally exporting only the optimized surviving nodes. It does not extend
the lifetime of observation caches or make plan proofs reusable.

## API and format

`msync::ir::{Relocatable, Contextual, Artifact}` expose the stages. Compilation
uses `Compiler::compile_relocatable`, relocation uses `Relocatable::bind`, and
`Contextual::capture` supports existing `Program` callers. `Contextual::program`
feeds the normal scheduler. `Artifact::to_json/from_json` use a versioned
`msync-ir` JSON envelope and explicit stage tag. Version 1 rejects unknown formats,
versions, invalid enum variants, invalid paths, malformed selectors and read-only
sinks. Contextual local paths must already be absolute and Env/Path lookups must
already be resolved. Artifact input is validated as data, not trusted executable
state. Both import and binding check the relevant invariants.

Source operation spans survive as byte ranges; original source text and detailed
operand escape-offset maps are not embedded. Artifact execution errors therefore
name the artifact/phase and retain runtime causes, rather than inventing exact
source snippets. No implicit file-type detection changes existing script inputs.

[Serde derive](https://serde.rs/derive.html) supplies structural encoding; semantic
checks reuse the existing compiler rather than a parallel URL/selector validator.
The stage/version separation follows the useful general idea in
[LLVM's bitcode format](https://llvm.org/docs/BitCodeFormat.html): a persisted IR
is an explicit format contract, not a dump of process-owned memory. This project
uses readable JSON and its own transfer semantics, not LLVM bitcode.

## CLI and REPL

```sh
msync emit-ir copy.msync -o portable.json
msync emit-ir copy.msync --ir-stage contextual -o bound.json
msync bind-ir portable.json --source-file /path/to/copy.msync -o bound.json
msync plan-ir bound.json
msync run-ir bound.json --dry-run
msync run-ir bound.json
```

Exports default to plain JSON on stdout, unaffected by color policy; named output
uses the shared staged-publication primitive. `emit-ir` never authenticates SSH,
probes remote metadata or transfers files. `bind-ir` resolves local lookup values
but does not copy file data. A relocatable artifact uses the current invocation's
cwd; `$file` requires `--source-file`. A contextual artifact retains its stored
context and paths. Live `plan-ir` may inspect remote metadata; dry-run stays offline.
All execution shares ordinary resource limits, authentication cancellation and
per-file publication semantics. Export plus `--dry-run` is rejected rather than
silently writing an artifact under a no-write expectation.

The REPL provides `/export-ir FILE`, `/export-relocatable FILE`, and `/load-ir FILE`.
Export uses the last successfully prepared transfer submission (including a
preview), not a later declaration-only command. It retains that submission's
compiler snapshot and logical context; `/cd` does not retroactively change them.
The interpreter retains the resolved pre-optimization program from submission
time; exporting does not re-evaluate a PATH entry that disappeared later. `/reset` clears
this retained submission. `/load-ir` executes in the foreground, so a pending
stream may be interrupted with Ctrl-C; active background work must be stopped or
joined before loading or writing an artifact, avoiding out-of-graph filesystem
mutations racing a live flow. It does not import aliases into the
interactive compiler environment. These new commands do not change `/load`
script behavior or the established `compile` alias for `plan`.

## Local validation

`tests/ir.rs` covers independent relocation contexts, prior captured bindings,
transactional compile state, JSON round trips and version rejection, contextual
path freezing, explicit file context, fresh real-file execution and reinspection
after a source disappears. `tests/ir_cli.rs` executes real subprocess workflows:
export, delete original source script, relocate, bind, preview without writes,
execute under a different cwd, named-file context, forced-color machine output,
invalid-option nonmutation, and REPL exports/imports across `/cd`.

Cross-platform CI remains a diagnostic test matrix (Linux/Windows, independent
jobs and manual dispatch); this change does not configure branch protections,
required status checks or merge gates. Local execution is the primary development
feedback loop. Platform-specific native SSH authentication evidence is recorded
separately in the runtime note.
