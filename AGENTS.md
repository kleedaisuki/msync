# Project engineering knowledge

## C# staged language (v0.8.0)

- The current product is the .NET 10 Native AOT implementation in `src/Msync.*`.
  The retired Rust implementation is available in Git history at `v0.7.3`, not
  in this checkout or a product compatibility path. Current readers accept
  artifact format 4 only; formats 1/2/3 and historical Rust artifacts are
  rejected, not reinterpreted or delegated to another executable.
- Braces own blocks, `;` discards values, and a block tail is its result.
  Binding values are mutable but inferred/declared storage types are immutable;
  assignment checks the fixed constraint. Shadowing creates a new lexical identity.
  Closures share captured locations and Scope aliases share instance identity.
  `Null` is a real value type; nullable bindings and Scope fields require explicit
  admission (`field?: null`) and postfix `value?` means
  **is null**, not is-present. Narrow the binding on actual control-flow edges.
- `fn` is generic by default. Specialize by callable identity and argument
  types, reserve recursive identities before lowering, and preserve captured
  binding slots rather than looking their names up again in recursive callers.
  Implicit Path/String conversion must change the runtime value as well as its
  inferred type, including annotated function returns.
  Number is binary64 with IEEE arithmetic/comparisons, right-associative `**`,
  truncating `%`, and finite integral Int64 count/index admission. `as` checks a
  static target and the explicit conversion matrix; Path/String is the only
  implicit conversion. Named structural `concept` constraints compose member,
  associated-type and callable contracts without opening `TypeKind` or proving
  algebraic laws; see [the language contract](docs/design/language-v080.md).
  Named Scope declarations are program/session-owned singletons initialized on
  first execution; anonymous Scopes are fresh. `global` imports definition-time
  lexical ancestor locations, not process-global names.
- `Scope` is lexical; `Connection` is physical. `$wsl`, `$local` and SSH URLs
  produce Connection values in 0.8. `Scope::member` selects lexical members;
  `path @ connection` qualifies Path/String values. Connection `::` is removed.
  Endpoint qualification retains raw path
  provenance; path separators follow the selected endpoint's grammar, so a
  POSIX backslash is filename data even on a Windows interpreter.
- `.mso` and `.mexe` contain typed binary bytecode, never AST/debug-source replay.
  Format version and language-semantic version are independent. Relocatable
  bytecode is machine independent; executable lowering freezes context and
  specializes operations, but never stores live credentials or stale filesystem
  observations as reusable proofs. Binding lowers, optimizes once at the chosen
  level (default O2), then performs final contextual lowering. Executing `.mexe`
  does not rebind or reoptimize. `decltype` infers without executing effects.
- Product code never depends on Python or a Rust toolchain. Typed Python under
  `scripts/`, `packaging/` and `tests/` owns development/installation experiments.
  Keep publication payloads in `artifacts/`, archives in `dist/`, disposable work
  in `.temp/`, and NuGet's transitive resolution in version-controlled lock files.
- See [the architecture contract](docs/design/architecture-v080.md) and focused
  0.8 language/runtime/tooling/validation notes before revisiting these decisions.
  [Mutable state and effects](docs/design/mutable-state-and-effects-v080.md)
  supersedes the earlier immutable binding/capture decision and fixes the
  dataflow, escape and directional barrier invariants. Its older binary
  compatibility discussion is superseded by the current format-4-only contract.

## Normal-user transfer model (2026-11-01)

- The user reconfirmed that the old recovery paragraphs in a later instruction
  snapshot are void. The normal-copy/retry model below is authoritative.
- Robustness must earn its normal-path cost. Do not trade 99% of ordinary work
  for 1% exceptional cases; use real input distributions and property-based,
  end-to-end checks rather than conservative rejection policies.
- The user explicitly superseded the former recovery-before-success contract:
  failed transfers can be retried. Do not charge every successful operation for
  persistent recovery registries, before-images, commit journals, parent-wide
  publication locks, filesystem-wide syncfs, or startup recovery of unrelated work.
- An ordinary file uses an owned same-directory temporary file and an atomic
  replacement, with at most one file fsync. Directory/cohort failures may leave
  completed members in place; report the actual result and permit retransmission.
  Do not introduce transactional rollback under another name.
- One finite remote transfer uses one native SSH client/channel, including its
  metadata and bytes. Preserve configured aliases, agents and native prompts;
  do not split a transfer into separately authenticated helper invocations.
- Keep actual language/platform semantics, useful diagnostics and resource
  lifetimes. Remove arbitrary quotas, duplicate validation and blanket rejection
  of ordinary filesystem features instead of inventing a hostile-user sandbox.
- Temporary cleanup belongs to the live owner. Never scan/delete old similarly
  named paths or change unrelated hard-link metadata. Legacy state is not an
  execution prerequisite and must not block a normal declaration or transfer.

## Local-first validation and release

- Run formatting, strict static analysis and relevant unit/property/real-process
  tests locally before committing or pushing. Select tests by changed contract;
  use a complete matrix only when explicitly requested or justified by concrete
  failures or affected scope. Do not repeat already accepted measurements without
  a changed implementation or discriminating question.
- GitHub Actions is for cross-platform behavior and package/editor installation
  acceptance, plus release publication. Do not add lint/format quality gates or
  make publication wait for a separate GitHub CI/check status. Remote success is
  supplemental platform evidence, not a substitute for local verification.
- Release completeness checks remain necessary: package versions must agree,
  platform packages and the VSIX must be present, and SHA256SUMS must cover them.
  Never move a published tag or replace its asset bytes to hide a defect.
- Keep reusable findings in existing English design notes. Keep disposable
  fixtures/logs under `.temp`; never include unrelated user notes, caches or
  credentials in commits or release packages.

## Typed composition, transport and prompt ownership

- Infer values once, then dispatch on semantic types and operator capabilities.
  Do not probe a computed Scope and synthesize it again on fallback: nested
  lexical calls can multiply that work. Keep ordinary scalar types compact;
  finite Bool/String/Path unions do not need a heap vector in every expression.
- `$std::ranges::` is the only predefined range namespace in current 0.8 source.
  The former `$view`, `$views`, `$ranges` and unbound bare range aliases are not
  source/compiler/editor/runtime entrypoints. User-declared lexical names are
  still ordinary values. Preserve source-owned operand diagnostics. Historical
  capability negotiation in [the old type contract](docs/design/type-system-v065.md)
  does not impose old-format compatibility on current format 4.
- An implicit finite Range transfer is one selected transfer operation, not an
  implicit source-level `for`. Normal collision preflight can precede writes;
  failed multi-file work may be retried without a persistent rollback protocol.
  Explicit loops and For bytecode retain their iteration semantics; never
  guess executable intent from debug source.
- Possible SSH paths are metadata, not executed effects. An unselected branch
  must not authenticate or globally join unrelated jobs. Bind credential and
  foreground ownership to actual selected endpoint/work admission.
- Exclude runtime-owned SSH control directories from snapshots by retained,
  verified filesystem identity, never by their filename. Do not hide user data
  or external user-owned control resources under the same spelling.
- Prompt notifications must not block interpreter progress waiting for a reader
  that can leave its input loop in the same turn. Preserve ordered messages with
  nonblocking/coalesced native wakeups; input priority or sleeps alone do not
  repair a cyclic backpressure dependency.

## Standard library and namespaces (v0.8.0)

- `$std` modules and explicit `scope Name { ... }` declarations use inferred
  named Scope values; named modules have type presentation in the editor.
  Lexical namespaces select declared members with `::`; physical endpoints use
  `@`. Keep their ownership distinctions rather than guessing from names.
- Standard sources in `stdlib/*.msync` are executable compiler input, not
  parallel documentation. Cache parsed documents, never semantic scopes with a
  first caller's slots/cwd. Source compositions specialize normally; explicit
  native declarations own host-only and dependent compiler capabilities.
- Range<Scope> retains endpoint identity. Unbound paths retain raw spelling and
  a captured local projection until qualification; derived relative paths also
  retain explicit endpoint provenance. Lexical inspection uses raw text while
  actual local I/O uses captured context, not the process cwd.
- Runtime executes current typed IR, never debug source. Ordinary Range calls
  evaluate arguments once on demand, including source functions returning
  ranges. Explicit eager snapshots (`materialize`, `environment.variables`)
  propagate their eagerness through source functions instead of replaying
  mutable inputs. Collections remain Range/Scope/Fn compositions, not new types.
  See [the library inventory](docs/design/stdlib-v080.md).
- Installed editor navigation uses versioned `msync-stdlib` virtual source
  documents backed by the same embedded bytes as compilation. Compare URI
  scheme/path semantically: VS Code may normalize triple-slash spellings.
- Public library families use named PascalCase Scopes and snake_case associated
  functions with explicit instance arguments. Instances retain data and selected
  policies, not per-instance getter/method bags. `containers`, `files`, `commands`,
  `data`, `text` and `regex` own the corresponding families; `io` and `process`
  retain host primitives. The unpublished factory interfaces have been removed,
  not preserved through compatibility adapters.
- Inline `typename` binders introduce associated types in concept requirements.
  Qualified standard concepts are compile-time declarations, not runtime Scope
  fields. `$std::meta` dependent queries do not execute their operands and must
  isolate specialization, storage constraints and mutable indexed metadata.
- Indexed storage is an explicitly mutable Range resource. Snapshots freeze
  membership, not referenced objects. First-write carrier inference must update
  the live runtime Range across REPL submissions without changing earlier
  snapshots or hashed identities. See [indexed storage](docs/design/indexed-storage-v080.md)
  and [ordered containers](docs/design/ordered-containers-v080.md).
- Returned values do not alias a callee's private binding cell. Deferred free
  reads and immediate callee-body flow are different phases; retain future
  domains for writable captures without erasing valid local refinements.
  Semantic specialization keys include callable/member shapes and generated
  standard-module reads. See [generic specialization](docs/design/generic-specialization-v080.md).
- Live Processes reachable through returned values, caller-owned locations or
  exported REPL bindings move to the caller/session; non-escaping work still
  joins. Range ownership roots are inspected without enumeration. Session cwd
  changes never alter the host process cwd or reanchor existing/deferred paths.
  Runtime-only session roots are not serialized into `.mso` or `.mexe`.
- Standard export demand projection applies to closed standalone source. Module
  escape retains complete shapes and shared cells; persistent sessions and
  editor contexts retain exports for later requests. Do not cache first-caller
  semantic environments or trade missing members for faster initialization.
- Deferred-read stability is proved for declaration identities and lexical
  lifetimes, not inferred from a variable's spelling or a single non-null use.
  Unwritten private locations and closed-script roots can retain refinements;
  future REPL roots, exported module cells and Scope fields remain mutable.
  The shared AST cache contains syntax facts, never caller semantic state.
- Repository `.msync` files use LF because literal newlines are language values.
  Host-specific delimiters come from `environment::newline`. Windows human
  console output uses UTF-8 at one saved/restored boundary; do not change input
  encoding or framed/binary protocol streams to repair text rendering.
