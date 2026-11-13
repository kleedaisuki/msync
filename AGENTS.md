# Working in msync

## Product and design

- Product code is .NET 10 Native AOT in `src/Msync.*`; Python is typed development
  infrastructure, never a product dependency. Rust is historical, not a fallback.
- Start with the relevant design note, not historical benchmark tables.
  [Native model](docs/design/native-model-v090.md),
  [language](docs/design/language-v080.md),
  [research status](RESEARCH.md), [benchmarks](BENCHMARK.md).
- Preserve observable language behavior, source diagnostics, alias identity and
  resource lifetimes. Read native IR format 1 and import historical format 6
  into the same native pipeline; never restore a second VM for compatibility.
- Keep compilation, contextual preparation and execution ownership separate.
  Artifacts do not replay source; the VM does not invoke the planner.

## Engineering

- Finish the requested behavior, not an MVP. Report meaningful progress and
  concrete blockers; prefer removing special cases to accumulating guards.
- Validate changed contracts locally through real user journeys and properties.
  Use focused formatting/static analysis; do not replace end-to-end evidence
  with unit-test counts or repeat accepted measurements without a new question.
- Ordinary transfers are copy-and-retry, not recovery transactions: owned
  same-directory temporary file, atomic replacement, at most one file fsync.
  Cohort failures may leave completed members. No persistent rollback registry,
  filesystem-wide sync, or cleanup of another owner's temporary paths.
- One finite remote transfer uses one native SSH client/channel for metadata
  and bytes. Preserve configured aliases, agents and interactive prompts.
- Keep actionable diagnostic context. Do not flatten concrete failures into
  generic errors or weaken observability to obtain benchmark wins.

## Workspace and delivery

- Keep project work inside the repository: disposable work in `.temp/`, payloads
  in `artifacts/`, archives in `dist/`. Preserve reusable findings in relevant
  English documents; do not turn this file into a second design manual.
- Keep dependency lock files versioned. Never include unrelated notes, caches
  or credentials in commits or packages.
- GitHub Actions supplements local validation with platform/install acceptance
  and publication; no separate lint/format publication gate. Check package
  version agreement, required platform/VSIX assets and checksum coverage.
  Never move a published tag or replace published asset bytes.
