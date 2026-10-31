# v0.6.5 independent acceptance

## Contract and ownership

This note maps the complete requested release to observable user journeys. The
language owner records the inferred type/operator model in
[type-system-v065.md](type-system-v065.md). Independent executable acceptance is
owned by `tests/acceptance_v065.rs`, not by that implementation's internal shape.
Temporary fixtures and command logs belong under `.temp/v065`; frozen executable
artifacts belong under `target/acceptance-v065`.

Source syntax does not introduce explicit type declarations. `Path`, `String`,
`Scope`, `Bool`, `Range<T>` and finite permitted scalar unions must be inferred.
The existing `Space` wire spelling is compatibility data, not a different user
type. `$` denotes built-in scopes/APIs, not a general string interpolation step.

| Requested surface | Discriminating observation | Required scope |
| --- | --- | --- |
| Operator capabilities | All four `String`/`Path` combinations of `/` produce usable paths; invalid Bool operands and non-Scope qualification fail before effects | Real CLI type errors and actual input bytes; not merely an accepted parse |
| Nested scope qualification | Scope-returning and Path-returning lexical functions work under ordinary nested `::`; function names are arbitrary | Local file journey plus supplied WSL example's pure compile/context binding |
| Deferred remote path binding | `$wsl::codex(config.toml)` retains WSL endpoint and remote-home semantics rather than capturing the local HOME | Typed contextual IR inspection without launching WSL; real SSH endpoint binding is tested by the SSH owner |
| Canonical built-in API namespace | `$view::` constructors, adaptors and consumers compose; ordinary user functions named `single` remain ordinary lexical functions | Source, object and structured-IR execution; editor spelling/navigation belongs to toolchain acceptance |
| Ordered user-created ranges | String, Bool and default/scoped Path lists preserve source order; nested symbol/boolean results are ordinary elements | Real loop state transitions and first/last file bytes across source-free object/IR reuse |
| Mixed permitted elements | A list can contain Path/String/Bool, total callbacks can consume it, and unsupported operations on its union item are statically rejected | No inspection of an unused missing path; strict Bool condition and trait diagnostics before effects |
| Invalid range domains | Nested ranges, Scope and port values are not scalar list members; Bool or mixed non-Path source ranges cannot silently become file transfers | Actual `run` fails before an earlier sentinel transfer can publish |
| Conditional scheduling | `if release`, `if acquire`, `if acqrel` retain Bool-only conditions and existing directional scheduling, not a new global join | A held unrelated released TCP flow remains active while the selected branch and following REPL command finish |
| Compiler performance | Type inference/lexical specialization is compared on representative repeated functions and small controls, separately from IO | Identified source/binary, matched workloads and allocation/time evidence from language/performance owners |
| SSH non-network performance | Identify and reduce orchestration/helper/metadata work while preserving remote bytes, metadata, cancellation and recovery | Real local OpenSSH or Docker endpoint, process/helper counts and paired small-operation latency; a local shell mock is insufficient |
| Historical recovery | Empty source/object/IR and retained sessions settle abandoned owned state while retaining live owners and the no-state cold path | Reuse `barrier_crash_v5`, `empty_recovery_v6` and affected pure-latency evidence; rerun only when changed code reaches that contract |
| Historical public adapter and range atomicity | Public compilation contextualizes control expressions; a multi-item implicit range transfer is one statement, unlike an explicit `for` | Compatibility-owner adapter tests plus collision/no-match public CLI cases below |

The table is a completion requirement, not a claim that all rows currently pass.
No narrow parser, provider or CI success establishes full release acceptance.
Actions remains cross-platform/package acceptance and publishing; formatting,
strict lint and focused correctness work are local-first.

## Frozen starting-point observations

At initial HEAD `d501fd26614055af7a0f77b58a7b20bbb6cadd99`, the existing debug
executable was copied without a Cargo rebuild to
`target/acceptance-v065/msync-v060-debug-baseline`. Its SHA-256 is
`fbdd2245dc3b1e9eb943dbc486536403b2e4adee72305f75a226d4e829aa1cb0`.
The executable reports 0.6.0. Four actual `--no-config check` probes failed:

| Probe | Observed baseline failure |
| --- | --- |
| Supplied `symbol codex` followed by `$wsl::codex(config.toml)` | Qualified executable calls were not accepted as compile-time symbols |
| `[true, false]` consumed by a `for` | The binding was not recognized as a range |
| `$view::single("payload")` | Qualified executable calls were not accepted as compile-time symbols |
| `if release true:` | The whole condition was treated as a non-Bool expression |

All returned exit status 1 and created no declared output. Reproductions,
stdout/stderr and the small status ledger are under
`.temp/v065/acceptance-baseline`. These are negative feature baselines, not SSH
performance measurements or proof of the exact executable's unrelated recovery
behavior.

A second probe on that same frozen executable checked the established tilde
rule before choosing new assertions. A bare `"~/.codex/config.toml"` transfer
produces those literal string bytes; an explicitly joined
`"~"/".codex"/"config.toml"` or Scope-qualified quoted path is already a HOME
Path and reads HOME bytes, just like the unquoted path term. The fixture also
contained a competing cwd directory literally named `~`, so the observation
distinguishes those behaviors. v0.6.5 must preserve that String-versus-explicit-
Path distinction, not invent a quoted-tilde directory rule. The reproduction is
under `.temp/v065/quoted-tilde-baseline`.

## Statement atomicity discriminator

Given two selected Path elements `a` and `b`, with an absent `output/a` and an
existing colliding `output/b`, one implicit `range -> output/` statement must
reject without publishing `output/a` or replacing `output/b`. This applies both
to a regex selector and a user-created Path list. No-match and empty lists must
not create the destination directory. An explicit `for` remains a sequence of
statements; this test must not impose whole-loop rollback on it.

The old collision assertion expresses the established statement contract and
must not be weakened as a supposed legacy expectation. Likewise, a Bool or
mixed non-Path range cannot be made transferable by silently formatting values
as bytes or dropping unsupported elements.

## Artifact and platform boundaries

Every new local journey uses isolated HOME/config roots and the shared
deadline-bounded subprocess helper with concurrent diagnostic drains. Object and
IR cases remove the source before execution; compiler success alone cannot stand
in for those cases. File paths containing spaces/Unicode exercise real namespace
handling rather than shell interpolation.

Linux-local evidence is not native Windows, WSL or remote SSH execution. The
supplied WSL expression has a meaningful pure compiler test even when WSL is not
installed. SSH performance completion requires the independently identified
actual endpoint and measured production path, not a skipped fixture.

## Accepted independent journeys

Eleven distinct functions in `acceptance_v065` have passing evidence across two
focused local windows. The first built ten cases (seven passed); its two harness
mistakes and the compact qualified-Bool type defect were corrected. The second
ran the three corrected cases, the changed quoted-String/Scope-return case and a
new union-callback case (five passed, six unaffected cases skipped). No unresolved
behavioral failure remains in this target's accepted windows.

| Journey | Passing evidence |
| --- | --- |
| Supplied and renamed/nested WSL symbol returns, pure IR binding | Second window |
| All four join signatures, nested/returned Scope, literal String versus explicit HOME Path, source-free object/IR | Second window, including changed assertions |
| Bool lists, nested boolean callbacks, runtime values and order | First window |
| String and scoped/default Path list insertion order | First window |
| Mixed permitted scalar lists and total callbacks, without opening an unused path | First window |
| `Path | String` callback `/`, both iterations and actual final file bytes, source/object/IR | Second window |
| Invalid operator/list/union/transfer domains reject before an earlier sentinel effect | Second window |
| Whole-If flags and compiled explicit directional-fence reference | Second window |
| All three conditional modifiers, both selected branches, complete while an unrelated TCP source remains held | First window |
| Implicit regex and user-created Path range collision publishes no earlier element | First window |
| Regex no-match and empty list create no destination | First window |

Scheduling is compared against explicit `Before`/`After` HIR fences, not the
unsupported old source spelling `release if ...`; source locations and unrelated
instruction identities are excluded from that comparison. The held-flow journey
waits for actual branch publication with a deadline, not an arbitrary sleep or
instantaneous completion at `/help` acknowledgement. Emitted IR is decoded with
the public `Artifact::from_json` API before contextual endpoint inspection.

Commands and logs:

```sh
cargo test --locked --test acceptance_v065 -- --nocapture
# After the affected fixes/new case, preserve incremental scope:
cargo test --locked --test acceptance_v065 -- --nocapture \
  --skip boolean_literal_ranges --skip string_and_scoped_path_lists \
  --skip mixed_scalar_lists --skip conditional_scheduling_does_not \
  --skip implicit_regex_and_literal --skip no_match_and_empty
cargo clippy --locked --test acceptance_v065 -- -D warnings
```

The accepted logs are `.temp/v065/acceptance-first.log`,
`.temp/v065/acceptance-corrected.log`, and
`.temp/v065/acceptance-clippy-final.log`. Strict target Clippy, rustfmt and diff
checks passed. These are incremental-source acceptance windows, not one final
whole-tree rerun, native WSL execution, or proof that the separately owned SSH,
compiler-performance and editor-host requirements are complete.

## Historical assertion classification after the local compatibility pass

The root-owned no-fail-fast compatibility pass exposed both real production
regressions and stale structural assertions. Only the following test contracts
were updated here; production diagnostics, parser/provenance and lifecycle
findings remain separately owned fixes, not weakened expectations.

| Existing case | Classification and retained contract |
| --- | --- |
| `lowering_v4` manually built nested macro | Rejecting every nested macro contradicts the explicitly authorized v0.6 lexical feature. The replacement still constructs the AST manually, accepts an unused definition, invokes it with explicit parent `global` capture, checks the expanded path and both expansion frames, and proves that a same-named global macro and invocation parameters do not leak/change scope. |
| `lowering_v4::read_only_transports_and_selector_policy_reuse_existing_compiler_rules` | The selector is now one typed `RangeTransfer`, not a legacy `Transfer` carrying `pattern`. HTTP destination read-only rejection and invalid-regex rejection remain unchanged. The oracle retains the exact regex, Path element/root, Local endpoint, contextual relocation and Container destination mode. |
| `preparation_v4::metadata_type_validation_distinguishes_directory_modes_and_selectors` | A synthetic `For` is no longer the correct shape for one implicit selector statement. The test now expects `RangeTransfer`; all directory-mode rejections, empty preparation facts, missing-root no-effects, native file-as-directory error and unchanged target/source bytes remain. |
| `relocation_lowering_v4::selectors_are_not_part_of_a_relocated_directory_path` | After the language owner repaired spaced-selector parsing, relocation resides on the typed regex root rather than a legacy `Slot::Source` table entry. The exact `WorkingDirectory` / `source/` assertion remains, with separate regex and `target/` Container relocation checks. |
| `join_v4::composed_executable_calls_and_selector_slashes_keep_their_grammar` | Only the selector's legacy `Transfer` layout was stale. Its oracle now checks one Path `RangeTransfer`, the full `a/b` regex, the separate `directory/` root and the Container target. The original executable fence/await/argv, HTTP namespace and IPv6 TCP assertions remain unchanged. |

The first four changed cases passed in focused local invocations. The selector filter
ran only one case in each of the three affected targets; the manually built
macro case ran separately after its test-data construction was flattened for
readability. Logs: `.temp/v065/stale-selector-oracles.log` and
`.temp/v065/stale-nested-macro-final.log`. No full compatibility rerun was used to
validate these mechanical oracle changes.

`cargo clippy --locked --test lowering_v4 --test preparation_v4 --test relocation_lowering_v4 -- -D warnings`
also passed, as did focused rustfmt and diff checks. Its log is
`.temp/v065/stale-oracles-clippy.log`. The original directory-mode, read-only,
regex-error, native-cause and no-effects assertions were retained; no production
code was modified for these oracle repairs.

The additional `join_v4` case passed by its exact function filter (one test,
eleven skipped), followed by strict target Clippy, rustfmt and diff checks. Logs:
`.temp/v065/stale-join-selector.log` and `.temp/v065/stale-join-clippy.log`.
This brings the classified stale-oracle closure to five functions; it does not
reclassify the independently owned operand-diagnostic or grammar defects as
stale contracts.

## Final-image independent performance audit

The final executable is SHA-256
`e18d9f36b905630b9915cc5634e4ff4833a66ee34924650fc555913482433f7f`,
compared with the preserved `d501fd2` baseline
`dca16a83beb10131ecde492ba6ff587dd90478bfe0dc4450d41088b779fff8e8`.
The final-image public acceptance log contains all eleven passing functions
(`.temp/v065/final-image-acceptance.log`); the subprocess helper explicitly selects
`MSYNC_TEST_EXE` before clearing inherited settings from children. The independent
audit below read existing data and probe code; it did not launch another timing
cohort or change production source.

**SSH:** the [accepted report and owner analysis](ssh-performance-v065.md) match
the maintained probe and preserved binary identities. Its full JSON report hash
is `177611e7218580b46510816b0c2d430ca4660b81dd3c9e137ff83e1efb1372f6`.
The audit independently verified sixty unique case/arm/pair cells, five complete
AB/BA pairs per six cases, recomputed medians and counts, and exact correspondence
with the durable observations CSV. Fixture setup/verification are outside the
timed operation; both arms copy the same payloads and check exact bytes, mode and
whole-second mtime. The error case checks nonzero status, the named missing source
and unchanged prior target bytes/metadata; that performance probe alone does not
prove caret/line-column diagnostic precision.

Authentication counts fall from 8/3/16/40/2 to one on the multi-session cases,
while SCP and remote-session counts stay unchanged. This supports connection-
orchestration gains, not a claim of less payload or omitted durability work.
Reuse adds five local SSH client invocations; local CPU increases 20.6% for the
download and 70.8% for the missing-source case remain visible. CPU here covers the
joined local client tree, not the still-running sshd/server tree. Root accepted
that bounded response/CPU trade-off; bulk improves both. The single `exists`
case wins only three of five pairs. Separate positive/pre-epoch upload/download
reports verify four fractional-metadata cases on this same final image; they are
functional checks, not extra paired timing samples.

**Compiler:** the [compiler acceptance record](compiler-performance-v065.md)
uses identical maintained source for both linked probes, warmups, seven complete
AB/BA pairs, and separate untimed allocation passes. Probe/library hash records
verify unchanged artifacts. Recomputed raw rows agree with the owner tables:
all six old-workload case/size combinations have exactly equal allocation traffic
and the measured control/HIR layouts are unchanged. Timing remains mixed, not a
universal speedup. The 1,000-entry typed case is candidate-only absolute cost,
never a ratio against a parser rejecting the feature.

The original small-symbol observation was retained rather than discarded. The
additional discriminator differs only by batching fifty identical compilations
per timed sample before reporting marginal cost. Its seven paired rows recompute
to 0.802682→0.784103ms marginal medians, +0.024574ms paired-median delta,
+0.022880ms mean delta and three after-wins. The original roughly 0.1ms penalty
did not reproduce consistently under batching; this does not prove zero overhead.
The separate 100-statement CLI control is also mixed (only three after-wins and
a +21.379670ms paired mean), despite its lower per-arm median. The baseline-only
1,000-transfer warmup timeout produced no accepted row and remains explicitly
rejected; it was not silently scored or compared with the typed case.

**Verdict:** the evidence supports the stated loopback SSH response improvements
and the compact inferred-type implementation's bounded, mixed compiler cost.
It does not support universal CPU/latency/throughput improvements, a strict
no-regression bound, or Linux-to-Windows/WAN extrapolation. No methodology blocker
remains for these bounded conclusions. Native-platform functional acceptance and
release packaging remain separate evidence, not consequences of a timing ratio.
