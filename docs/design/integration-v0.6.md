# Version 0.6 integration contract

## Delivery boundary

Version 0.6 adds scalar booleans, boolean functions, deletion, existence queries,
string ordering, lazy path/string ranges, structured control flow, nested lexical
functions, and binding removal. Existing 0.5 source and object contracts remain
supported. The package version is independent of the persisted object format:
new semantics require explicit feature negotiation rather than silently changing
old instruction meaning.

The ordinary statically expanded transfer path remains the dominant-workload
fast path. Dynamic constructs lower to typed semantic expressions and nested
regions. Neither source text nor frontend AST is an executable object payload.

## Invariants that connect the components

- Parsing preserves owned spans and distinguishes literal bytes from paths.
- Static checking determines every scalar, range item, condition and loop result
  type before effects begin. `if` never coerces strings or paths to boolean.
- Pure computation may fold or inline; filesystem state never becomes a compiler
  constant. An existence binding captures its value when its declaration executes.
- `=` updates an existing same-typed bool/string local. Dynamic branches and
  loops carry those cells across iterations; fresh inner declarations stay local.
  Function parameters and explicitly imported globals are invocation-local copies.
- Boolean operators short-circuit at runtime. Static checking still checks both
  operand types. Eliminating unreachable execution does not legalize bad types.
- A range binding records a plan. Enumeration begins at consumption, and predicates
  execute only for demanded elements. A zero-sized take must not pull its input.
- Directory ordering can buffer names to preserve established deterministic order;
  this does not justify eager file observation or predicate evaluation.
- A control operation is an interpretation boundary: preceding finite managed
  effects settle before an existence check or deletion observes that path. An
  unrelated released stream is not implicitly acquired by entering control flow.
- `delete` unlinks a file; it is not recursive directory removal. `drop` only
  removes lexical names, never file resources or retained process ownership.
- Nested definitions capture definition-time lexical ancestry. Explicit `global`
  imports an earlier binding, never an unrelated caller-local binding.
- Objects retain all executable semantics after source deletion. Context binding
  visits operands recursively in nested regions, ranges and exported expressions.
- REPL and object loading preserve identity freshness across submissions; local
  iterator or loop state cannot collide with an earlier persistent runtime slot.

## Prior art and engineering choices

The [MLIR structured control flow dialect](https://mlir.llvm.org/docs/Dialects/SCFDialect/)
models conditional and iterative execution using regions and typed yields. We
adopt the representation principle without introducing the LLVM dependency tree.
[Rust's loop contract](https://doc.rust-lang.org/reference/expressions/loop-expr.html)
provides the model for nearest-loop breaks and compatible result types.

The [C++ adaptor interface](https://learn.microsoft.com/en-us/cpp/standard-library/range-adaptors?view=msvc-170)
separates view construction from per-element work. Existing Rust standard-library
iterators and the project's `regex`/`regex-syntax` libraries supply the underlying
mechanisms; a separate query engine or scripting VM would add a second semantic
owner without solving a missing requirement.

[Bila et al., Compositional Reasoning for Side-effectful Iterators and Iterator
Adapters](https://arxiv.org/abs/2210.09857) identify accumulated closure effects and
iterator-chain reasoning as distinct obligations. The practical implication here
is that `exists` in a filter is time-sensitive: moving it from consumption to
range construction or evaluating past a short-circuit changes observable behavior.
This reference motivates discriminating tests, not a claim of formal verification.

## Incremental acceptance

New real CLI journeys are owned by `tests/cli_v060.rs`; compiler/frontend/runtime
owners add focused tests beside their affected APIs. Integration records actual
commands and results after the implementation converges. Historical full-suite
results are not evidence for newly added dynamic semantics. Windows or remote
behavior is not reported as executed unless that platform or fixture was used.

### Accepted native Linux increments (2026-10-31)

| Surface | Distinct tests with passing evidence | Evidence / scope |
| --- | ---: | --- |
| Real CLI/REPL and static acceptance | 27 | 22 CLI journeys and 5 static/property tests; [detailed incremental record](validation-v6.md) |
| Compiler specialization, mutation, fragments, ranges | 23 | `compiler_functions_v060`, `compiler_mutation_v060`, `compiler_fragments_v060`, `ranges_v6` |
| New parser and presentation | 10 | `language_v6`, `language_presentation_v6` |
| Control planning and independent review | 13 | `planner_control_v6` (5), `review_v060` (8) |
| Runtime and filesystem helpers | 8 | `control_runtime_v6` (2), `execution::control_fs::tests` (6) |
| Typed objects and process/port graph rules | 13 | `object_v6` (8), `hir::graph::control_spawn_tests` (5) |
| Selected established source/runtime contracts | 75 | `barrier_graph_v5`, `cli_v052`, `language_v052`, `language_v4`, `scripts_env_v052`, `scripts_v052`, `symbol_v052`, `version_v4` |
| Established object codec/linker | 15 | `object_codec_v4` (6), `object_linker_v4` (9) |

These are **184 distinct tests across incremental windows**, not a claim of a
final full-suite rerun. Generated property cases are not counted as additional
test functions. The earlier failure of the dynamic stdout destination journey
was fixed without weakening the reproduction. Oversized forged pipeline tests
were rerun after their depth guard landed; empty filtered invocations are not
counted as acceptance evidence.

Final `cargo clippy --locked --all-targets -- -D warnings` passed on the integrated
code. `cargo fmt --all -- --check` and `git diff --check` passed. Local logs live
under `.temp/v060/`; test sources and these notes preserve the useful knowledge
when temporary logs are cleaned. The binary reports `msync 0.6.0`.

No complete test matrix, native Windows run, SSH/WSL transport session, installer
or release publication was performed in this task. The remote shell body was
exercised locally, which is not remote transport acceptance.

### Explicit lexical imports in the supplied drop example

The existing macro rule still requires an explicit import of an outer value:

```text
Codex := ~/.codex
macro codex():
    global Codex
    acquire Codex/config.toml -> s.txt
codex()
drop Codex, codex
```

`drop` removes both names; it does not delete the source configuration or `s.txt`.
Keeping the explicit import avoids silently changing established macros that
intentionally use an ordinary path whose spelling matches an outer binding.

## Main-branch integration gate (2026-10-31)

The delivery topic is `codex/v0.6.0`. Before integration, local and fetched
`origin/main` both pointed to `f3c4f63`; main is an ancestor of the topic, so a
fast-forward preserves the existing 0.5.2 history without replaying or squashing
its compatibility fixes. Engine, native editor, performance helpers and delivery
documentation have separate commits. User-owned untracked `docs/analysis/` and
ignored build/editor caches are excluded.

The targeted pre-integration gate passed:

- `cargo fmt --all -- --check`;
- `cargo test --locked --test cli_v060 --test telemetry_sqlite_cli --test performance_lsp_journeys`
  (22 real CLI/REPL journeys, 6 SQLite CLI journeys and 2 stdio LSP journeys);
- `npm test --prefix editors/vscode` (11 grammar/client tests);
- Bash syntax checks for the scheduler/allocation runners, Python AST syntax and
  function-annotation checks for the three latency/IO probes, and staged
  `git diff --check`.

These are integration smoke checks, not additional distinct cases in the earlier
acceptance totals or a new full-suite/performance claim. The requested integration
is local; remote push, tagging and release publication are separate actions.

## Empty-unit recovery regression repair

The published v0.6.0 tag and assets remain immutable. A local reproduction of
`barrier_crash_v5::empty_and_declarations_only_submissions_recover_before_returning`
found that the new empty-HIR shortcut could return successfully without recovering
an interrupted publication. Recovery is automatic startup work, not something a
user must remember to trigger with a later effect.

Execution/filesystem owners now provide a conservative read-only admission probe
for local and remote recovery registries in both the primary and configured
fallback roots. Absent/not-a-directory roots remain cold; present registries and
probe errors enter the existing ownership-validation/recovery/diagnostic path.
Empty source, object and IR units skip runtime activation only when that state is
absent. Each recovery root is admitted separately, so fallback cleanup does not
require allocating or modifying an unused ordinary-file primary path. Actual
storage operations retain their existing primary/fallback allocation policy.

A retained RuntimeSession checks its own initial scratch/fallback roots, not an
arbitrary later REPL cwd. Repeated admission uses the same recovery owner and
record locks; it neither switches roots with live work nor treats initialization
as proof that no later orphan can exist.

The unchanged crash test passed (including empty/declaration source and object
restarts), and both latency `pure_` tests passed without changing ordinary-file
scratch/fallback sentinels. Three focused tests cover source/object/IR fallback
cleanup, invalid local/remote registries in either root, and repeated admission
recovering an orphan while retaining a live owner's locked stage. The established
read-only-workspace fallback test also passed, including its occupied-primary
error assertion. All seven test functions passed in focused local windows; this
is not a full-suite or new publication claim.
Selected strict Clippy (`--lib --bin msync --test empty_recovery_v6`), full
format checking and `git diff --check` also passed on the repaired source.
