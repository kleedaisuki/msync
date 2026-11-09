# Language upgrade integration and delivery (v0.8.0)

## Scope and evidence ownership

This record separates the new language contract from already accepted product
behavior. The upgrade retains product version 0.8.0 and default O2. Previous
binary formats are not a compatibility requirement for this upgrade; the
current writer, reader, disassembler and editor must agree on one format.

The accepted starting source is `e7149ddc38fe7b77e875bf2dd17ac9ada00d7cfa`.
The previously accepted Native AOT source is
`e85e6170598dfd38adefbcb778e43647686c3096`, built by native run 42603680953.
Its executable identities are:

- Linux: `2b2f40a00e3767fad638b30c096017de671ee1434a81984b9a8218e4587b2a4f`.
- Windows: `09d41e8fdcc383fda5b1aebee7343daf2ad3346d0fe611daedb28d0acd05d597`.

These images are comparison baselines, not evidence for the new contract.
Unchanged installer, SSH authentication, prompt ownership and artifact-watcher
mechanisms retain their existing acceptance. New endpoint syntax and any changed
actual I/O admission still require a real selected-endpoint journey.

Implementation owners provide their focused local evidence. Integration runs
exercise cross-module behavior and immutable Native binaries rather than repeat
those owners' unit matrices. Native publication starts only after a coherent
source freeze; test/editor-only repairs reuse the same accepted image.

## Acceptance matrix

| Changed contract | Integration observation | Owner evidence reused |
| --- | --- | --- |
| Number | Source, object and executable agree for literal forms, adjacent operators, precedence and finite/fractional count/index policies; path spellings remain paths | Numeric semantic matrix, IEEE comparisons and compile-time/runtime equivalence |
| Structural concepts | A composed concept constrains a real source-library value through a parameter and return; missing/wrong members produce useful source diagnostics | Nested member and function-signature constraint tests, recursive/composed concept cases |
| Explicit `as` | Real text/path/number/endpoint conversion workflows and forbidden conversion diagnostics; nullable flow remains explicit | Complete legal conversion matrix and Path/String-only implicit conversion tests |
| Physical `@` | Path/String qualification followed by transfer preserves grouping and selected endpoint provenance; old physical `::` explains migration | Parser precedence and type-checker cases |
| Scope lifetimes | Named declaration state is shared within its program/session; anonymous factory instances remain distinct, including closures | Fixed storage constraints, captures, loop identities and optimizer equivalence |
| Source class library | Useful multi-module file/link/Process and Range pipelines use concepts, factory state and `as`, not host-only mock APIs | API inventory, source composition and native primitive tests |
| Current staged IR | Source to `.mso` to `.mexe` to source-free execution; bound context behavior, view and current format agree, old version rejected clearly | Current schema/loader/optimizer invariants and admission tests |
| Formatting | Long Boolean chains and nested code are readable; formatted program parses and preserves effects and literals | Idempotence and syntax-preservation properties |
| Editor presentation | Actual VS Code host shows concept/type, named standard namespace, keyword, `as` and `@` roles; new artifacts open with useful IR highlighting | Protocol semantic-token, completion, hover, grammar and formatter tests |
| Interactive product | Actual CLI, REPL and language server use the same new syntax and standard source bytes | Terminal control and protocol fixture reports |
| Native platforms | Linux and Windows run changed source/object/executable and product journeys using recorded exact images | Previously accepted unchanged OS contracts |
| Distribution | Four assets, checksums, matching product/editor versions and complete source/license manifests; final host uses absolute Native binary path | Existing packaging and installer acceptance |

## Measurement and freeze protocol

Record exact source, SDK, binary hashes and representative probe inputs before
comparing. Measure compile/bind/run separately, allocations where the existing
component probe supports them, and externally visible milliseconds for real
small commands. Include a no-standard-library negative control, Boolean module
loading, Range pipelines and representative 128/1024 workloads. Do not use only
dead pure expressions, mix managed and Native timings, or turn noisy timing
observations into CI thresholds. CPU-heavy builds and timing windows are shared
resources and are coordinated with implementation owners.

The baseline identity record is
`.temp/language-upgrade-delivery/baseline.json`. New acceptance remains pending
until the changed-contract reports and final artifact identities are recorded.

Because the public product version remains 0.8.0, installation instructions must
replace both the Native package and VSIX, reload the window, and restart the
language server. The final actual-host record must include its absolute
`msync.serverPath` and executable SHA-256; a same-version stale PATH binary is not
a valid upgraded editor observation.

## Focused native entrypoints

`packaging/language_upgrade.py --binary /absolute/native/msync` selects only this
upgrade's integrated CLI, tooling, class-library and progressive-reference
journeys. The native workflow exposes the same selection as
`polish_only=true, polish_phases=upgrade`; it does not run historical object-format
fixtures or unrelated installer tests. Each phase retains stdout/stderr, elapsed
time and the exact executable SHA-256. This is an execution interface, not an
acceptance result.

- `tests/Msync.EndToEnd/language_upgrade_journey.py`: composed concept parameter
  and return constraints, compact arithmetic/bases, `as`, actual `@` file bytes,
  singleton versus fresh factory identity, source-free stages at O0/O2/O3, and
  useful negative diagnostics.
- `tests/Msync.EndToEnd/language_upgrade_tooling_journey.py`: direct language-server
  and redirected REPL contracts owned by the language-experience lane.
- `tests/Msync.EndToEnd/stdlib_upgrade_journey.py`: useful source-composed services
  and collections; optional matched baseline/candidate loading observations.
- `tests/Msync.EndToEnd/skill_journey.py`: progressive offline reference and actual
  compilation of its current-language examples.

The production-bundle actual VS Code host supports the focused selection
`MSYNC_TEST_LANGUAGE_UPGRADE_ONLY=1`, with the final absolute native path in
`MSYNC_TEST_SERVER_PATH`. The host suite is an additional editor boundary, not a
substitute for native CLI execution on both operating systems.

## Managed integration observations

The first producer-owned immutable CLI snapshot is
`.temp/language-upgrade-managed/snapshot-1/msync`, executable SHA-256
`10e48f20bebc980b777a13c12fb5f107c54b91d9e0cb47b5354ecc3b8cf2b282`.
Its provenance file records the source diff as well as the HEAD because parallel
implementation was still in progress. This is managed integration evidence,
not the final native publication freeze.

- The integrated CLI journey passed nine actual source/object/executable programs
  at O0/O2/O3, four source-diagnostic rejections and four current/obsolete format
  contracts. Its report is
  `.temp/language-upgrade-delivery/managed-integrated.json`.
- The semantic owner reported `tests/LanguageUpgrade` (fixture `7ceff29`): 232
  assertions over 24 programs, three stages and O0/O2/O3 plus sixteen static
  rejections. Coverage includes the cast matrix, lazy Range conversion, permitted
  implicit conversions, `@` evaluation order, fixed storage/captures/nullable
  flow, composed concept returns and singleton closures. This focused owner
  evidence is reused rather than rerunning that matrix in delivery.

Later fixes to source-library snapshots are not covered by this first snapshot;
the final native cross-module journeys must consume the coherent publication
image rather than infer acceptance from these early results.

The source-migration owner additionally reported focused existing-workflow
acceptance on snapshot 1: eight standard-library programs across 24 staged runs,
nullable Scope members across 36 staged runs and three LSP revisions, six mutable
state stages with the fixed-type diagnostic, twelve linear-effect cases, current
format-4 views/schema-1 plus singleton/remainder instruction hovers, real SSH
`path @ remote` authentication/cancellation with one authenticated session, and
packaging smoke plus endpoint projection. That snapshot's `msync.dll` is
`42d2052d4645ab33c24ddb7da6791c1666724b412f07440da4b2c52bd26fff24`.
The repository DSL example also checks under snapshot 2, whose `msync.dll` is
`1a98184e3e576898e38f64159b01fcb1db0552578b683cfd8d4982cc4ca61f8a`,
including the corrected trailing-directory `@` syntax. These focused migration
results are reused; no unrelated old-format compatibility matrix is retained.

The native selection additionally runs current-format disassembly and, on Linux
with explicit `--ssh-fixture`, a small real remote class-library journey. The
library owner accepted its three O2 source/object/executable routes on snapshot 2
(fixture `ca99597`): fifteen exact-byte observations, no authentication during
check/compile/bind, five native SSH clients per selected execution and no `scp`.
Windows supplements the native CLI bytes with the focused `tests/ContextBinding`
filesystem contract; Linux already has the owner's local result.

When `benchmark=true` and `baseline_run=42603680953` are selected, the same native
publication wave records matched old/new no-stdlib and Boolean module loading
and 128/1024-statement transfer compilation, generic value checking and
object/executable execution. Each image creates and consumes its own binary
format. The comparison does not require cross-version IR compatibility, build
Rust again, or impose timing quality gates.

Two late user-visible integration selections are explicit rather than hidden in
older fixtures: `path-display` exercises remote Path display independently from
its unchanged native String projection; `empty-collections` exercises all eight
prototype-based empty collection factories, 23 observations through three O2
stages and three fixed-schema/comparer rejections. The ordinary combined library
programs already cover nonempty List/Map/Set, so delivery does not repeat the
collection owner's full 35-program local matrix on every native platform.

## Coherent managed freeze

Producer snapshot 4 includes the final production code, including eager collection
snapshots, Range<Scope> loop-shape convergence, persistent REPL concept metadata,
named Scope path facts, typed empty factories, Regex capture accessors and remote
Path presentation. The implementation owners accepted their changed contracts
against that immutable snapshot:

- Language: Number 601 assertions, named Scopes 93, concepts 48 and integrated
  semantics 232, plus focused grammar/editor identity checks. These counts are
  distinct owner reports, not a summed end-to-end coverage metric.
- Tooling: final LSP/redirected REPL/actual Linux PTY journey and persistent concept
  Tab completion at `.temp/tooling-upgrade-journey-final.json`; selected physical
  completion evidence is `.temp/tooling-final-provenance/report.json`.
- Path presentation: `8795a7b` and
  `.temp/path-display-journey/snapshot-4.json`, preserving native String projection
  while displaying physical qualification with `@`, including source-free IR and
  diagnostic provenance.
- Class library: the authoritative
  [composed-library validation record](stdlib-upgrade-validation-v080.md) records
  45 local staged routes with 252 exact outputs and three actual SSH staged
  routes. Delivery reuses that local evidence and runs the new platform boundary.

The product tree at `c2caa704e311e3cf6622e748adc1f36a73d5b268` is committed; remaining
fixture migrations do not imply uncommitted product bytes. Native platform
acceptance, final VS Code host observation and final distribution audit remain
pending until their immutable image reports are recorded below.
