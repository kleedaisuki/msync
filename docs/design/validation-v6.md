# v0.6 independent acceptance journeys

## Contract and discriminating checks

The public acceptance target is `tests/cli_v060.rs`. Each journey creates files
under the repository's `.temp` directory and invokes the actual `msync` binary.
Child invocations have a 20-second deadline so an incorrect loop implementation
cannot leave an unbounded test process. Tests assert file bytes, existence,
and exit status rather than merely checking compilation succeeds.

| Contract | Distinguishing observation |
| --- | --- |
| Static compilation has no endpoint effects | `check` and `run --dry-run` leave transfer destinations absent |
| `exists` observes runtime order | A newly written file is seen before `delete`, absent afterward |
| Assigned booleans are values, not thunks | `present = exists victim` remains true after deletion |
| Boolean short-circuiting is operational | Runtime `or` and `and` skip a symlink loop that would fail filesystem lookup |
| Selectors are lazy | An unused range and `take(0)` do not enumerate a missing directory |
| Filesystem selectors observe current execution | A file created after range construction is visible on consumption |
| Filter/transform use typed functions | A path predicate filters; a symbol projection produces literal string bytes |
| Objects retain semantic control IR | An object and emitted IR run after deleting their source, with fresh branch selection |
| Session runtime values have submission lifetime | A captured boolean survives later submissions and deletion of the observed file |
| Object linkage owns fresh runtime identities | Loading one object twice does not overwrite a prior REPL boolean slot |
| Nested control operands relocate | A moved object selects a file present only under its new cwd and writes there |
| Loop results have static scalar types | Nested loop breaks return string, path, bool and space values |
| Lexical parent capture is transitive | An inner macro imports values from an outer macro through an intermediate scope |
| `drop` removes names, not files | Existing output remains available after dropping the producing bindings |
| `delete` is not recursive | A directory deletion fails without touching its child |
| `delete` unlinks symlinks | The symlink disappears while its target bytes remain |

`tests/integration_v060.rs` supplements these with strict operand/condition
rejections, dead-branch type checking, incompatible loop-result rejection,
non-escaping nested/dropped template names, and 48 generated string comparison
cases over ASCII plus Unicode text. The comparison property derives expected
results from Rust string ordering, independently of the DSL evaluator, and
requires constant conditions to lower to the selected literal transfer.

## Mutable scalar bindings

The v0.6 integration contract additionally treats `=` on an existing bool or
string binding as same-type assignment, rather than a new lexical shadow. This
is necessary for a loop whose body changes `done = false` to `done = true` to
terminate. Assignments to existing outer bindings propagate out of conditional
and loop blocks; names introduced for the first time inside a branch remain
local. Changing a bool binding into a string, or vice versa, is a static error.

Three initial additional tests use the `mutation_` name prefix:

- A finite real CLI loop updates both bool and string bindings, exits, and
  exposes the updated values after the loop. Its process has the same 20-second
  deadline, so incorrect constant folding produces a bounded failure.
- A filesystem-selected branch updates outer scalar bindings. A real REPL
  `/bindings` query verifies a fresh branch-local name does not escape.
- Static compilation rejects scalar-type changes and a branch-local `drop`
  outside the branch.

The three `mutation_` tests passed the root-owned Set/promotion integration
gate on 2026-10-31; the log is `.temp/v060/mutation.log`. This adds two actual
CLI/REPL passes and one static pass to the earlier gate below.

Two further isolated journeys passed the root-owned
`.temp/v060/final-journeys.log` integration window:

- `mutation_object_and_ir_retain_loop_state_after_source_removal` compiles a
  state-mutating loop to both object and structured IR, removes its source, and
  requires both formats to retain the updated bool/string values.
- `repl_cd_filename_roles_use_the_selected_cwd_without_touching_the_original`
  gives both the original cwd and a REPL-selected cwd their own `marker` file.
  After `/cd`, string-valued `exists` and `delete` must observe/remove only the
  selected cwd's marker; the original marker and its bytes must remain intact.

The latter complements the review target's public embedding-context check;
neither test changes the process-global cwd to compensate for a missing logical
context in the interpreter.

## Dynamic external programs

Two `dynamic_spawn_` CLI journeys exercise the dynamic Spawn gate:

- A path-valued `loop` returns the current msync executable. Iteration over
  `ranges::of("--version", "--help")` passes each runtime string to that program.
  Separate stdout files prove both calls occurred and that each process's
  stream refers to the correct iteration, rather than merely checking the final
  overwritten result.
- A filesystem selector yields paths to two real child scripts. The current
  msync executable receives each path as its `run` argument; distinct output
  bytes prove both child scripts were actually invoked with the right path.

Both journeys compile an object and structured IR, remove the driver source,
and execute both artifacts. Child scripts remain present because they are
deliberate runtime program inputs, not source files secretly reopened by the
driver's compiler. Executable paths use JSON string quoting for Windows and
Unix portability.

The root-owned `.temp/v060/dynamic-spawn.log` gate passed the path-argument
journey and exposed a real compiler defect in the string-argument journey:
`P::stdout` with an iterator-derived destination was incorrectly sent through
scalar-only lowering. After the production owners added typed process-port
support, the focused `.temp/v060/dynamic-spawn-string.log` rerun passed the
previously failing journey (one pass, 20 unrelated tests filtered out). The
reproduction remained unchanged; it was not weakened to discard stdout or use
a static destination.

A follow-up Unix-only journey,
`dynamic_spawn_bidirectional_ports_transfer_loop_values_in_objects_and_ir`,
uses a real released `cat` process. A string-valued loop result feeds its stdin;
its stdout feeds a path-valued loop result; explicit acquire then joins it.
Object and structured IR execution after driver removal must reproduce the
exact payload. This targets the newly shared transfer input/output domain in
both directions. The root-owned `.temp/v060/final-additions.log` gate passed
this journey (one pass, 21 unrelated CLI tests filtered out, 0.10 seconds).

## Execution record

On 2026-10-31, the focused native Linux command passed:

```sh
cargo test --locked --test cli_v060 --test integration_v060
```

- `cli_v060`: **15 passed**, no failures or ignored tests, 0.38 seconds.
- `integration_v060`: **4 passed**, including 48 generated UTF-8 comparison
  cases, no failures or ignored tests, 0.03 seconds.
- The incremental build took 43.78 seconds. The local captured log is
  `.temp/v060/journeys.log`; the durable test sources retain the reproductions.

Subsequent root-owned incremental gates completed all authored additions:

| Gate log under `.temp/v060/` | Newly accepted CLI journeys | Newly accepted static tests |
| --- | ---: | ---: |
| `journeys.log` | 15 | 4 |
| `mutation.log` | 2 | 1 |
| `final-journeys.log` | 2 | 0 |
| `dynamic-spawn.log` and focused `dynamic-spawn-string.log` repair gate | 2 | 0 |
| `final-additions.log` | 1 | 0 |
| **Distinct accepted tests across incremental gates** | **22** | **5** |

This is a record of focused passes as each implementation increment landed,
**not** a claim that the complete 22/5 targets were rerun together against the
final source snapshot. The stdout regression above has both a recorded failure
and an unchanged passing reproduction after repair. No authored journey in
these two targets remains unexecuted.

The same final-additions log records three complementary compiler-fragment
checks (relative explicit endpoint object round-trip, conflicting endpoints,
and nonrecycled session slots) and three explicit-global template import
checks. Those belong to separate test targets and are not included in the
22/5 totals.

These checks establish the listed local native Linux user journeys, not a
Windows execution result, a full legacy regression matrix, or completion of
the separately owned final Clippy gate. Production changes and other
verification remain with their integration owners.
