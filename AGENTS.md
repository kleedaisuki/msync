# Project engineering knowledge

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
- `$std::ranges::` is the canonical typed range API; `$view::` remains its
  compatible primitive scope. Preserve working aliases and source-owned operand
  diagnostics. New typed object operations must
  negotiate `typed_values_v2`, including exported values and intermediate types.
  See [the type contract](docs/design/type-system-v065.md).
- An implicit finite Range transfer is one selected transfer operation, not an
  implicit source-level `for`. Normal collision preflight can precede writes;
  failed multi-file work may be retried without a persistent rollback protocol.
  Explicit loops and old For bytecode retain their iteration semantics; never
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

## Standard library and namespaces (v0.7.0)

- `$std` and explicit `scope name:` declarations use inferred Scope values.
  Lexical namespaces select declared members; physical endpoint scopes qualify
  paths. Keep their ownership distinctions rather than guessing from names.
- Standard sources in `stdlib/*.msync` are executable compiler input, not
  parallel documentation. Cache parsed documents, never semantic scopes with a
  first caller's slots/cwd. Source compositions specialize normally; explicit
  native declarations own host-only and dependent compiler capabilities.
- Range<Scope> retains endpoint identity. Unbound paths retain raw spelling and
  a captured local projection until qualification; derived relative paths also
  retain explicit endpoint provenance. Lexical inspection uses raw text while
  actual local I/O uses captured context, not the process cwd.
- Native operations, lexical namespace values and deferred path representation
  negotiate `native_std_v1`. Old objects keep their original semantics; runtime
  executes typed IR, never debug source. Lazy range calls evaluate arguments once
  on demand, including source functions returning ranges.
- Installed editor navigation uses versioned `msync-stdlib` virtual source
  documents backed by the same embedded bytes as compilation. Compare URI
  scheme/path semantically: VS Code may normalize triple-slash spellings.
