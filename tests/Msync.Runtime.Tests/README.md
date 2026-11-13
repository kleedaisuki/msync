# Native runtime acceptance and independent validation

This solution project runs the current typed native IR, never the retired stack
VM or its Value/Cell/Scope carriers. Run from the repository:

```sh
dotnet run --project tests/Msync.Runtime.Tests
```

The existing `--profile`, `--inline-native`, and `--mutable-state` entrypoints are
preserved. `--files` and `--validation` isolate host journeys and generated IR
properties. Default execution runs every group. Disposable host files remain in
`.temp/runtime-tests/`, and each host journey deletes only its own directory.

## Contract-preserving migration

| Retired mechanism | Current acceptance contract |
| --- | --- |
| Operand-stack underflow and stack-height joins | Typed register definite initialization, exact Bool/Number ABI, valid CFG targets, iterator lifetime and exhaustion edges |
| Instruction-level stack verifier oracle | Independent exhaustive `(pc, initialized-register mask, open-iterator mask)` path-state exploration; 6,000 seeded branch/loop/iterator programs, with both positive and negative outcomes |
| Generic Scope constants and Cell normalization | Fresh typed allocation initializers per execution; shared namespace aliases; mutable captured locations; escaping return locations; session-owned lazy producers |
| Borrowed-frame/detach counters | Observable result 127 across captured write/returned closure, alias-write result 84, proved private frame placement and escaping-hint downgrade, actual native placement counters |
| Number boxing and stack-inline/vector owner counters | Typed native builtin boundaries, exact native call counts, Range producer demand, field writes, and original native-failure spans |
| Compact stack instruction positions | Native fixed-PC/source identity, eliminated executable arithmetic, actual CFG edges and selected call targets |
| Old VM-only timeline interval schema | Native instruction/function/call profiles, paired dispatch pause/continuation, scheduler admission/dependency intervals and selected transfer observations |

The profile's hand-authored typed loop has independently derived counts: main
executes 22 instructions, the increment function executes 3 instructions on each
of 3 calls, and conditional edges are taken 3/1 times with 3 backedges. Eight
concurrent executions must report exactly 248 total instruction visits. Ready
input must not pause dispatch; gated input pauses/resumes once. Cancellation and
native input failures preserve their caller-visible outcomes and original source.

Real user journeys exercise source compilation, checked source-free `.mso`
roundtrips, contextual preparation and portable executable roundtrips; Unicode
scalar ordering, exact string bytes, regex partial predicates, generic dispatch,
local and symlink-aware transfer dependencies, frozen versus relocatable cwd,
no-demand lazy ranges, persistent-session lazy callbacks, process output/spool
limits and once-only deferred producers are checked with independent results and
filesystem oracles. Failed transfers must return false, count once before
interpreter disposal, preserve the existing destination, and retain structured
source/native diagnostics without also writing fallback errors. PATH qualification
retains its explicit-extension and captured-working-directory fixture.

128 additional real native-IR region executions compare checked address geometry
against `index * sizeof(double)` and reject indices outside the independently
chosen element count, preserving original source and concrete bounds causes.
The property oracle intentionally does not call production CFG, operand-facts or
flow-state helpers. Static kinds/targets and runtime region bounds remain separate
contracts rather than being collapsed into an old ambiguous Any-stack rule.

## Verified post-retirement receipt

The complete isolated-artifacts command below passes 6,484 assertions, including
all host journeys and all generated properties. The migration exposed and retained
regressions for nontext regex predicates, Unicode-scalar constant folding, failed
transfer accounting before/after disposal, and non-executable PATH qualification;
product owners repaired these contracts before this project was marked passing.

```sh
dotnet run --project tests/Msync.Runtime.Tests \
  --artifacts-path .temp/runtime-tests-artifacts
```
