# Working in msync

- Product: .NET 10 Native AOT in `src/Msync.*`. Python is fully typed development
  infrastructure, never a product dependency.
- Start with the relevant [design](docs/design/native-model-v090.md),
  [language contract](docs/design/language-v080.md), and
  [development contracts](docs/development-contracts.md).
- **Detailed telemetry is required for every new or changed compiler and runtime
  path.** Instrument the actual phases, analyses, passes and decisions, not just
  an outer stopwatch. Preserve useful source/function context, work counts,
  cache/proof/rewrite outcomes, causal ownership and concrete failure causes.
  Extend the existing sink-neutral telemetry; keep recording-disabled paths lean.
  See [observability](docs/design/observability-architecture-v086.md) and
  [compiler telemetry](docs/design/telemetry.md#compiler-telemetry).
- Preserve language behavior, alias identity, resource lifetimes and artifact
  compatibility. Never introduce a second execution pipeline for old artifacts.
- Validate changed behavior through focused real user journeys and properties;
  do not substitute unit-test counts or blanket regressions for delivery.
- Keep work inside the repository: `.temp/` for disposable work, `artifacts/`
  for payloads, `dist/` for archives. Persist reusable findings in English.
