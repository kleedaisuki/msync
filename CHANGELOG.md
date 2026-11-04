# Changelog

## 0.7.3 — 2026-11-04

- Remove backslash escape decoding from String source syntax. Windows path text
  such as `"D:\Code\agents\target"` remains literal; doubled quote delimiters
  represent an embedded quote, and actual multiline content is preserved.
- Insert bare filesystem Path completions by default, quoting only when needed
  for whitespace, grammar or a lexical-name collision.
- Distinguish URI protocol names, the `://` operator and endpoint addresses in
  editor and terminal highlighting without changing endpoint values.
- Restore nearest visible binding suggestions on typed `::` and `$` errors,
  keeping operator-owned diagnostics and inferred operand types.

See the [release notes](docs/releases/0.7.3.md). Published 0.7.2 assets are unchanged.

## 0.7.2 — 2026-11-04

- Represent `$` references and `::` selection explicitly in the frontend and
  resolve operators through inferred values, preserving the existing typed IR
  and native host interface.
- Align VS Code and semantic highlighting for unary `$`, binary `::` and binary
  `/`, including all four String/Path operand pairings in compact joins.
- Keep String-generated paths as Path even when their text resembles a selector;
  only explicit source selector syntax creates a Range.
- Add compiler-backed local filesystem completion, including `$local::`, hidden
  directories and correctly quoted names; editing does not authenticate endpoints.
- Show inferred binding types on hover, reconnect when the server path changes,
  and provide an explicit language-server restart command.
- Exercise operator workflows through real editor interactions and source-free
  object/IR execution, plus installed native package acceptance.

See the [release notes](docs/releases/0.7.2.md).

## 0.7.1 — 2026-11-03

- Fix compact `scope::file` selection when the scope comes from a runtime loop:
  resolve the bound path or macro argument instead of using the literal name
  `file`. Preserve endpoint-specific home resolution and quoted filenames.
- Exercise nested endpoint/path macro loops in the installed Windows executable,
  Windows portable package and Linux package acceptance journeys.

See the [release notes](docs/releases/0.7.1.md).

## 0.7.0 — 2026-11-02

- Introduce the typed `$std` standard library with `fs`, `ranges`, `path`,
  `bool` and `string` namespaces. Infer namespace aliases such as
  `fs => $std::fs`; retain existing range API compatibility spellings.
- Add explicit lexical `scope name:` blocks with nested members, functions,
  macros and aliases. Support ordered `Range<Scope>` values through callbacks,
  consumers and endpoint-distribution loops.
- Preserve unbound path/home provenance through runtime range bindings, so
  `$local::file` and a selected remote `scope::file` use the correct contexts.
- Add C++-inspired filesystem predicates, status, directory iteration,
  creation/removal, copy/rename/link, symbolic permissions and lexical path
  operations on local, SSH and WSL endpoints.
- Compile source-defined standard functions through ordinary specialization.
  Expose host facilities with explicit typed `native` declarations, including
  scalar, Range and Void results, and negotiate `native_std_v1` in new objects.
- Add standard-library completion, hover, signatures and definition navigation
  into the binary's actual bundled source using read-only VS Code documents.
  Ship readable standard sources in portable packages as well.
- Select syntax 0.7 by default while retaining 0.5/0.6 source aliases and older
  object loading. Keep normal-copy/retry semantics and native SSH ownership.

See the [release notes](docs/releases/0.7.0.md) and
[integration contract](docs/design/integration-v070.md).

## 0.6.6 — 2026-11-01

- Replace recoverable multi-file transactions with ordinary staging, atomic
  file replacement and retry. Remove persistent registries, before-images,
  commit journals, parent-wide publication locks, global recovery admission and
  filesystem-wide syncfs. This explicitly changes the former crash-rollback
  contract; completed members of failed multi-file work remain completed.
- Carry finite transfer metadata and bytes through one native SSH exec per
  endpoint, using Rust tar coding on the client. Remove SCP/helper reconnects,
  implicit client preauthentication and duplicate source/payload copies.
- Merge directory containers and replace same-name files so rerunning a transfer
  updates normally without cloning unrelated destination data.
- Support native linked workspaces, recursive link entries and case-sensitive
  paths instead of blanket refusal. Retain native file descriptors for released
  file inputs and support FIFO participant groups and queued independent flows.
- Remove arbitrary input/import/object/query/document width and size policy
  caps, path whitespace rejection, inactive config gates and repeated internal
  object validation. Keep the actual language and platform representation rules.
- Keep optional observer failures separate from transfer outcomes; SQLite
  ingestion no longer blocks interpreter producers. Accept normal editor input
  and format selected symlink targets through normal replacement.
- Use compact execution dependency graphs instead of storing a proof for every
  independent instruction pair. Preserve the public detailed-explanation API.

## 0.6.5

The integrated source has passed local functional, compatibility and editor
acceptance, with bounded performance costs recorded explicitly. Native package,
Windows installer and editor workflows passed on the development branch with
publication skipped; the final [native Rust run](https://github.com/kleedaisuki/msync/actions/runs/41240455544)
at `01aef79` passed on Ubuntu and Windows, including terminal wake and credential
checks. The
[integration workboard](docs/design/integration-v065.md) records their scope.

- Make inferred semantic `Path`, `String`, `Scope`, `Bool` and `Range<T>` types
  explicit internally. Centralize operator operand/result capabilities without
  adding source type annotations or user-defined trait declarations.
- Apply scope qualification to ordinary lexical function results, including
  `$wsl::codex(config.toml)`, and retain deferred home/relative-path binding until
  the scope is known. Support nested Scope composition through `::`.
- Canonicalize built-in range APIs as `$view::NAME`; retain established
  `views::` and `ranges::` spellings as compatibility aliases. The built-in API
  Scope is an ordinary typed value and may be aliased, as in `api => $view`.
- Add `if release`, `if acquire` and `if acqrel` using existing directional
  scheduling and strict Bool conditions, not a new global wait.
- Add ordered `[ ... ]` ranges of String, Bool and scoped/default-local Path
  values. Expand ordinary symbol/boolean calls and infer finite unions for mixed
  allowed elements; reject operations unsupported by a possible member type.
- Negotiate new typed-value operations through `typed_values_v2`, including
  exported values, while preserving old scalar encodings and the established
  `typed_control_v1` / `source_modules_v1` feature contracts.
- Repair executable pipeline contextualization for nested typed control values.
  Restore one-statement publication for implicit finite range transfers rather
  than committing each selected member separately; explicit `for` iterations
  remain separate statements. Retain automatic empty-unit/repeated-session
  recovery with live-owner protection and the no-state cold path.
- Reuse invocation-owned authenticated SSH connections on actual selected work
  admission, and use responsive native child completion with a bounded fallback.
  Real loopback upload/download/selection controls improve response time without
  reducing remote sessions or omitting durability work. Local CPU costs remain
  explicit on the refreshed delivery image: download +32.6%, missing-source error
  +67.6% and single `exists` +6.6%, versus five-upload CPU −40.4%; single `exists`
  response timing is mixed. Restore exact fractional
  upload/download timestamps through the existing private-staging/receipt paths.
- Preserve compact inferred types: scalar/Expr/instruction layouts and measured
  old-workload allocation traffic are unchanged, and computed values are
  synthesized once. The actual delivery's batched 1,000-entry symbol/condition
  controls retain +0.303ms/+0.559ms paired-median costs (each slower in five of
  seven pairs); these bounded feature/correctness costs are accepted explicitly,
  not advertised as a compiler speedup. Earlier faster rows retain their own
  historical image identity.
- Canonicalize known Windows drive/UNC resource-comparison roots without
  rewriting IO operands. A 64-case alias/sibling property and 512-case index
  oracle preserve alias completeness and independent-resource separation.
  Distinguish Windows `SystemTime`'s 100ns fixture representation from actual
  transfer-metadata precision; portable test-data corrections do not weaken
  production timestamp behavior.
- Repair source-owned operand diagnostics and the REPL completion-notice
  backpressure deadlock. A maintained, provenance-recorded Rustyline 19 patch
  preserves complete ordered native messages, implicit per-message raw-mode
  newlines and nonblocking/coalesced wakeups instead of adding sleeps, a
  notification thread or a replacement editor.
- Keep quality checks local-first and Actions limited to cross-platform behavior,
  native package/editor acceptance and publication. Published tags and asset bytes
  are immutable; version agreement, required VSIX/platform assets and checksums
  remain release invariants.

Local strict all-target Clippy and formatting pass after the compatibility
repairs. All eleven refreshed delivery-image public acceptance functions and all
nine real VS Code host phases pass; the verified VSIX is unchanged. Native
package/installer acceptance and the successful native Rust matrix retain their
separate revisions and scopes. No WSL/WAN-performance or publication claim is
inferred from Linux results.

See the [release notes](docs/releases/0.6.5.md),
[type/operator contract](docs/design/type-system-v065.md), and
[independent acceptance](docs/design/validation-v065.md). Detailed measured
results remain in the [compiler](docs/design/compiler-performance-v065.md) and
[SSH](docs/design/ssh-performance-v065.md) records; historical repairs and editor
dependency provenance are linked from the release notes.

## 0.6.0 — 2026-10-31

- Add `bool` values (`true`/`false`), `=` boolean/string bindings, short-circuit
  `and`/`or`, `not`, boolean equality, and lexicographic string comparisons.
  Same-typed `=` reassignment carries existing state across conditions and loops;
  fresh branch/loop-local names and function invocation state remain isolated.
- Add pure `boolean` functions with mandatory boolean returns, nested boolean
  declarations, and explicit definition-time `global` imports. Fold and inline
  pure computation while retaining time-sensitive filesystem queries at runtime.
- Add `exists` for path existence and `delete` for file removal. Both accept
  paths or filename strings; deletion is not recursive directory removal.
- Add statically typed lazy path/string ranges and C++-style `|` view pipelines,
  boolean consumers, and indentation-sensitive `for`, `if`/`else if`/`else`, and
  Rust-style `loop`/`break`. Loop expressions can yield strings, paths, booleans
  or spaces; conditions require actual booleans.
- Allow local macros, symbols and booleans inside macros, local symbols inside
  symbols, and definition-time ancestor lookup through `global`. Add `drop` for
  lexical binding removal without deleting resources.
- Preserve static transfer execution and 0.5 source aliases. Persist new dynamic
  semantics as feature-tagged typed control IR, not executable source text.
  Existing objects retain their interpretation contract.
- Add TOML application settings with platform user-directory discovery,
  `~/.msync/config.toml` fallback, explicit selection/bypass, source provenance
  inspection and pre-effect diagnostics. Help/version bypass configuration.
- Add opt-in normalized SQLite telemetry and read-only session/span/event query
  and JSONL export commands alongside the existing JSONL exporter.
- Add the `--language-server` stdio endpoint and a packaged native VS Code
  extension with TextMate coloring, snippets and LSP client integration.
- Reduce repeated compiler/snapshot/graph/preparation/object-analysis work and
  use event-driven REPL background-completion notification. Workload-specific
  measurement and cross-domain acceptance are tracked separately from the
  language gate; no universal speedup or GUI-session acceptance is implied.

See [0.6 release notes](docs/releases/0.6.0.md) and the
[integration contract](docs/design/integration-v0.6.md) for semantic boundaries
and the original acceptance scope. The published tag and assets remain immutable;
historical defects and later source repairs are tracked separately for 0.6.5.

## 0.5.2 — implementation candidate

- Add pure, lexically isolated generic `symbol` functions with explicit global
  capture, nested static calls, mandatory return and instantiation diagnostics.
- Canonicalize scheduling source to `release`, `acquire` and `acqrel`; keep old
  source aliases and serialized/runtime scheduling contracts compatible.
- Statically inline `execute` (isolated lexical environment per file) and
  `include` (shared lexical environment), sorting source-selector matches without
  synthetic scheduling boundaries. Compiler-local source intake is bounded.
- Carry explicit source-module bindings in tagged object metadata, preserving
  `$file` and captured definition contexts without reopening source scripts or
  guessing linker paths from debug filenames. Caller context overrides win.
- Bound compile-time function work/intermediate values independently of ordinary
  transfer payload sizes. CI now selects the changed frontend/object journeys;
  a Git-proven package-version-only bump no longer forces unrelated full tests.

## 0.5.0 — 2026-10-30

### Integrated public delivery

- Merge the complete structured-language branch alongside main's native SSH
  authentication/cancellation, exact Host aliases and per-user Windows packaging.
- Keep CLI/REPL reusable IR commands for expanded 0.5 objects while preserving
  the actual public 0.4 artifact wire/execution contract.
- Verify portable/installed macro execution, source-free object/IR replay,
  multiline REPL exports and public 0.4-to-0.5 installer upgrades.

### Breaking execution model

- Remove transaction declarations, isolation modes, checkpoints, named rollback,
  MVCC history, and native transactional filesystem views. Local external programs
  no longer require Sandboxie or bubblewrap.
- Compile expanded, typed operations into ordered basic blocks. Only the current
  block is contextualized against the filesystem; independent operations within
  it can run concurrently under explicit resource limits.
- A bare program invocation starts and awaits that program. `async` starts it
  without the implicit join; `await Program` joins that invocation and related
  flows. Bare `await` joins outstanding work. Macros join their own unawaited
  asynchronous invocations before returning.
- Normal cancellation stops new work but drains started external programs to
  their own exit. `/kill` is an explicit destructive alternative. External side
  effects and streaming transfers are not rolled back.

### File publication and recovery

- Finite filesystem statements retain only their own temporary publication and
  recovery state. Failure does not undo earlier completed statements.
- Interrupted multi-path or directory publication is recovered to a complete
  old or new state before new execution, using the same configured scratch root.
  This is recovery atomicity, not simultaneous visibility of every directory
  entry before recovery or a universal power-loss guarantee.
- Active recovery records do not globally exclude other sessions. Input capture
  excludes verified runtime-owned backing, not arbitrary user `.temp` data.
- Exact destinations reject existing directories; a trailing `/` remains an
  explicit container, and `/.` requests tree replacement.

### Language and interface

- CLI and source headers accept language `0.5` only. Quoted values are UTF-8 data;
  write `$pwd / "filename"` for an explicitly relative file path. Old `0.3` and
  transactional `0.4` headers are rejected rather than silently reinterpreted.
- Export typed object modules with `compile -o module.mobj`. Object format 2
  preserves symbolic context and diagnostics, not cached filesystem facts or
  live process handles. Debug source is never reparsed to execute an object.
- Separate compiler, evaluation, execution, and cleanup diagnostics. Retain
  token locations and native causes without presenting environmental failures
  as syntax errors.
- Preserve colored help, in-place script formatting, persistent REPL bindings,
  and `/help` / `/?`. Add explicit process/flow ownership and `/kill`.
- `--spool-limit` bounds each program's combined stdout/stderr spool. Exhausting
  it reports an output failure while continuing to drain the child naturally.

### Measured runtime optimization

- Defer remote lookahead to actual acquisition while retaining local metadata,
  native channel proofs and conservative unknown-alias dependencies. The supplied
  mixed macro uses four WSL helpers instead of six; five alternating pairs for
  both existing and missing targets improved mean runtime by approximately 14%.
  A separate local control retained a small positive complete-command cost
  (+0.84% mean, +1.62% p50); no zero-regression or tail-latency guarantee is claimed.

Implementation contracts and acceptance evidence are maintained in
[barrier execution](docs/design/barrier-execution.md),
[statement atomicity](docs/design/statement-atomicity.md), and
[validation](docs/design/barrier-validation.md). Complete Windows/Linux CI,
the paired release measurements, and the installed executable journey passed.
