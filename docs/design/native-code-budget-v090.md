# Native v0.9 deletion budget

This is the user's new **current-worktree** baseline, after the native migration
and the earlier retirement of the object backend. Those earlier deletions do not
count toward this budget. The baseline is a measurement, not a claim that v0.9
is complete or published.

| Measure | Baseline | Required final maximum |
| --- | ---: | ---: |
| Code lines (`cloc`, excluding comments/blanks) | 50,794 | 35,555 |
| Physical source lines | 54,832 | 38,382 |

At least **15,239 code lines** must disappear net. All replacement code and new
product files count. The scope is product C# in `src/` and the source standard
library in `stdlib/`; tests, documentation, third-party code and build output
are not deletion credit. The checked-in generated Unicode table remains fixed:
removing its data is neither simplification nor a valid way to meet the target.
Changes to other product surfaces must also be reported, not used to hide moved
implementation.

[`native-code-budget-v090.json`](native-code-budget-v090.json) records exact paths, source hashes, byte/line totals and the hard
target. Raw `cloc` reports remain under `artifacts/validation/deletion-budget-v090/`;
the versioned JSON also retains the tool version and each file's code/comment/blank counts. This baseline includes uncommitted v0.9 work; its Git
HEAD is provenance, not a substitute for the content hashes.

No minification, compressed statements, generated/string-encoded replacement
logic, extension changes, relocation to excluded directories, or removal of
features, compatibility inputs and tests qualifies. The architectural deletion
must remove work and state: duplicated semantic interpreters, repeated static
proofs, literal relocatable-IR execution, address/value roundtrips and unnecessary
materialization/cache authorities. New helper layers do not count as deletion.

Correctness and reduction are joint acceptance conditions. Preserve source/HIR,
portable/bound artifacts, REPL/editor behavior, builtin representations, alias
identity, source diagnostics, deferred work and real I/O/resource lifetimes.
Validate coherent batches with real journeys and destructive properties; do not
run a new full matrix for every line edit or substitute a test count for behavior.

Measure a checkpoint with `python3 packaging/code_budget.py --report-only
--output .temp/code-budget-CHECKPOINT.json`. Without `--report-only`, the command
fails until both numeric targets are met; it never overwrites a prior receipt.
The measurement uses development tool `cloc`, not a product dependency. A numeric
pass still requires the semantic and no-hiding-code audit above.
