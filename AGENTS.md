# Project engineering knowledge

## Recovery is an execution invariant

- Every executable submission must settle existing interrupted statement state
  before it reports success, including empty source, declarations, fully folded
  branches, objects and IR. Never infer that startup recovery is unnecessary from
  an empty instruction list. Users must not remember a recovery command or run a
  dummy transfer.
- Preserve the no-state fast path with read-only discovery at the owning local
  and remote registries, including the configured fallback root. An inaccessible
  or invalid existing registry is not absence: preserve normal owner validation
  and actionable errors. Never scan or delete arbitrary similarly named paths.
- A retained runtime owns its initial recovery roots across REPL cwd changes.
  Probe those roots, not a guessed current directory. Initialization is not a
  permanent proof of absence: later admission must notice newly abandoned state
  while preserving live owners' locks and payloads.
- Reuse statement-local publication and recovery ownership. Do not introduce a
  database, global transaction manager, daemon or retained undo history to repair
  this lifecycle error. SQLite telemetry is observation, not recovery authority.
- Any optimization that skips work must preserve the failure/restart lifecycle,
  not only successful output bytes. Pair cold-state latency checks with real
  crash/restart journeys. See [the publication contract](docs/design/statement-atomicity.md)
  and [the integration record](docs/design/integration-v0.6.md).

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
