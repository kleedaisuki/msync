# Changelog

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
