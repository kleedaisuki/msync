# Historical compatibility repairs for v0.6.5

## Baseline and evidence boundary

The starting source is `d501fd26614055af7a0f77b58a7b20bbb6cadd99`.
[Rust run 41190821020](https://github.com/kleedaisuki/msync/actions/runs/41190821020)
failed the same two `execution_v4` functions on Linux and Windows. The separate
package/editor runs passed; their success does not erase a library or statement
semantics regression. This inventory does not launch a blanket test matrix.

The existing [0.6 integration review](review-v6.md) and
[performance review](performance-review-v6.md) record resolved earlier findings.
They are not reopened merely because their initial investigation headings remain
in those documents. The empty-executable recovery defect is already repaired on
this baseline; its seven focused acceptance functions and ownership boundaries
are recorded in [the integration contract](integration-v0.6.md#empty-unit-recovery-regression-repair).
Keep that recovery coverage in the 0.6.5 acceptance set.

## H065-01: executable pipeline adapter drops typed contextual binding (P1)

**Owner:** compatibility adapter (`src/pipeline.rs`), coordinated with the
language/type integration lead. **Status:** confirmed; adapter repair is under
focused verification.

At the baseline, `pipeline::compile_named` returns
`compile_relocatable_named(...).map(|compiled| compiled.compilation)`
(`src/pipeline.rs:163-164`). Ordinary instruction operands happen to contain
physical-looking compatibility locations, but a lazy selector now lowers to
typed control expressions whose `ExprKind::Path.relocation` remains populated.
The adapter discards the relocation container without recursively binding those
expressions. Runtime correctly refuses them in `execution/control.rs::eval`
with `control path was not contextualized`.

This is a public API compatibility obligation, not an outdated test assumption:
`execution_v4::run` passes the result of `pipeline::compile` directly to
`execution::execute`. `pipeline_v4` separately requires returned ordinary paths
to equal the supplied logical cwd while compilation remains filesystem-free.
The failing no-match selector journey expects successful no-op execution and no
created destination; instead, it fails before querying the selector.

Repair the executable compatibility adapter through the existing pure
`context::contextualize_source` mechanism, including recursive control operands
and graph rebuilding. Keep `compile_relocatable_named` portable, do not fabricate
runtime context in the interpreter, and preserve atomic compiler-session commit
if binding fails. Focused acceptance should include unchanged `pipeline_v4`,
`submission_v4`, and the affected `execution_v4` selector cases.

The adapter owner reports the first local repaired `execution_v4` run at 11/12:
the no-match selector now passes, and the collision now reaches the expected
`already exists` error before failing the separate no-partial-publication
assertion at line 80. This distinguishes the two defects rather than reporting
the entire target as passing. The same adapter snapshot built fresh selected
test binaries; direct execution then passed `pipeline_context_v065` (2),
`pipeline_v4` (3), and `submission_v4` (5). The new adapter cases cover nested
typed path binding and rollback of compiler state after contextualization
failure. An unresolved environment anchor remains legal in the portable API,
while the executable API reports its contextualization error. Concurrent new
0.6.5 AST changes subsequently made an in-flight integrated build incomplete;
these accepted adapter binaries are not a claim that that newer whole tree
compiles or passes.

## H065-02: implicit range transfer loses statement atomicity (P1)

**Owner:** typed range lowering/runtime publication, coordinated by the language
integration lead. **Status:** confirmed in the public CLI and released 0.6.0
Linux binary; not fixed by H065-01 alone.

At the baseline, `compiler/structured/v6.rs:1075-1102` translates one
`range -> container/` statement into a synthetic `For` with a per-element
`Transfer`. `execution/control.rs:101-110` executes and publishes that body for
each item independently. If a later selected destination collides, an earlier
item is already visible and retained after failure.

The unchanged
`execution_v4::a_selector_collision_does_not_publish_an_earlier_selected_file`
asserts that neither selected file is published on a later collision. H065-01
currently masks this failure by rejecting the unbound control operand first.
The public CLI performs contextual binding and exposes the separate publication
defect directly.

### Real released-image reproduction

The already downloaded official archive is
`dist/releases/v0.6.0/msync-0.6.0-linux-x86_64.tar.gz`, whose SHA256SUMS was
verified during publication. Its extracted `bin/msync` has SHA-256
`4dca6bb8476a111258d65f34abffdd651f258c92477bb5d4611d4dd61a364454`.
The same outcome was independently observed with the pre-0.6.5 debug executable
SHA-256 `fbdd2245dc3b1e9eb943dbc486536403b2e4adee72305f75a226d4e829aa1cb0`.

From the repository root, using either identified executable:

```sh
MSYNC="$PWD/dist/releases/v0.6.0/msync-0.6.0-linux-x86_64/bin/msync"
mkdir -p .temp/v065
fixture=$(mktemp -d "$PWD/.temp/v065/selector-history-XXXXXX")
mkdir -p "$fixture/source" "$fixture/dest"
printf a > "$fixture/source/a"
printf b > "$fixture/source/b"
printf keep > "$fixture/dest/b"
printf 'source/<.*> -> dest/\n' > "$fixture/collision.msync"
(cd "$fixture" && "$MSYNC" --no-config run collision.msync)
# The command exits 1 with a destination-entry collision.
test ! -e "$fixture/dest/a"
```

The final assertion fails: `dest/a` contains `a`, while `dest/b` still contains
`keep`. The fixture's successful earlier element is an unintended partial
publication, not a corruption of the pre-existing colliding file. Disposable
captured stderr and fixture paths are under `.temp/v065/history`; the program,
bytes, identities and outcome above are the durable reproduction.

### Correction boundary

Preserve a single finite range-transfer statement boundary through the existing
preflight/capture/publication owner. A general typed batch-transfer primitive is
preferable to recognizing a particular regex spelling or adding ad hoc rollback
after each loop item. Construction remains lazy; this transfer statement consumes
its range at execution and publishes its selected finite cohort atomically under
the established [statement contract](statement-atomicity.md). An explicit user
`for` loop remains multiple ordinary body statements with its own per-iteration
semantics. Do not weaken the unchanged collision assertion.

## Remaining acceptance scope

The failed Rust jobs stop at the first failing integration target, so their logs
do not prove that every later target passes. After these repairs, select tests
from the changed binding/publication contracts rather than treating a historical
green package job or the two repaired functions as full 0.6.5 acceptance. No
published tag or release-asset byte was changed by this inventory.
