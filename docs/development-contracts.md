# Development contracts

## Product and design

- Product code is .NET 10 Native AOT in `src/Msync.*`; Python is typed development
  infrastructure, never a product dependency. Rust is historical, not a fallback.
- Start with the relevant design note, not historical benchmark tables.
  [Native model](design/native-model-v090.md),
  [language](design/language-v080.md),
  [research status](../RESEARCH.md), [benchmarks](../BENCHMARK.md).
- Preserve observable language behavior, source diagnostics, alias identity and
  resource lifetimes. The v0.9 release reads native IR format 2 and HIR format 4 only; old artifacts
  must be rebuilt from source. Native-2 uses compact revision 4; obsolete prototype
  revisions require recompilation, not loader-side analysis or migration.
  Compiled storage/call plans are authoritative;
  do not reconstruct them in the loader or runtime.
- Keep compilation, contextual preparation and execution ownership separate.
  Artifacts do not replay source; the VM does not invoke the planner.

## Engineering

- Finish the requested behavior, not an MVP. Report meaningful progress and
  concrete blockers; prefer removing special cases to accumulating guards.
- Use the [concurrent journey runner](../tests/TestExecution/README.md) for shared
  incremental builds, supervised Roslyn reuse, bounded task scheduling and SQLite
  failure receipts; existing script entry points remain supported.
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
  English documents; keep entry-point instructions short and link detailed contracts here.
- Keep dependency lock files versioned. Never include unrelated notes, caches
  or credentials in commits or packages.
- GitHub Actions supplements local validation with platform/install acceptance
  and publication; no separate lint/format publication gate. Check package
  version agreement, required platform/VSIX assets and checksum coverage.
  Never move a published tag or replace published asset bytes.
