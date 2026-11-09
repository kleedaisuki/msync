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
fixture migrations do not imply uncommitted product bytes. At that handoff,
native platform acceptance, the final VS Code host and the distribution audit
were pending. The subsequent closure below records their actual results.

## Final native and editor closure

The upgrade is delivered locally as version **0.8.0**, default **O2**, current-only
IR **4**. Exact native production source is
`a952448083a6f0bea9ee2def0e775dcd804500f7`. Test-only follow-ups do not change those
product bytes. No public release/tag was created or moved.

| Boundary | Actual accepted result |
| --- | --- |
| Linux Native AOT | [Job 129166315467](https://github.com/kleedaisuki/msync/actions/runs/42619095812/job/129166315467) passed package smoke, all eight focused phases including real PTY and selected SSH, and matched native performance |
| Windows Native language | [Job 129166315112](https://github.com/kleedaisuki/msync/actions/runs/42619095812/job/129166315112) passed package smoke, nine language stages/four diagnostics/four format contracts, matched performance and the captured-context filesystem journey |
| Windows immutable-image follow-up | [Job 129169946668](https://github.com/kleedaisuki/msync/actions/runs/42620165945/job/129169946668) passed tooling/ConPTY, library, empty factories, skill, disassembly and Path display using the exact first image |
| Actual VS Code 1.144 | `.temp/vscode-host-oa5zbm` passed the language-upgrade suite with absolute final Linux executable, no managed assemblies, two themes, concept navigation/completion and idempotent maximum-61-column formatting |
| Distribution | Four final assets and complete checksums audited; 253 manifest entries on each OS, 16 corresponding standard sources, 59 modified MPL sources and nine matching VSIX product payloads |

Accepted executable SHA-256 identities are Linux
`c828e4135c837da84b77f39c5117f9167742d27ef77830c170de59242772d69e`
and Windows
`a76b773498662ef3034221d339bbce5f18ee020c6aca50f7ab804d430f6d3d81`.
The final actual-host record contains the absolute Linux path and this exact
hash; it did not accidentally launch a Debug/PATH server.

The first Windows job's aggregate status is failure, not silently reclassified
as success: its Python runner tried to print the completed tooling report through
CP1252. Fixture `7aae458` explicitly configures UTF-8 output and child Python;
only the affected and not-yet-executed phases were selected in the immutable
follow-up. The accepted language/performance/context journeys were not repeated.
The host suite also required test-only decimal-token-offset and empty-format-edit
repairs in `8b584a9`; neither changed the Native or VSIX product bytes.

Final raw collation is `.temp/final-language-upgrade-delivery-v080.json`.
The refreshed `.temp/language-upgrade-delivery/native-wave.json` and
`windows-reuse-wave.json` contain completed job states, not dispatch-time queued
snapshots. Original native source and fixture revisions remain distinct there.
Phase logs/results live under
`artifacts/ci/42619095812/{linux,windows}/download/.temp/platform-results/language-upgrade/`
and the downloaded Windows reuse artifact under `artifacts/ci/42620165945/windows/`.
The [current package inventory](packaging-v080.md#current-complete-local-delivery-set)
and [matched native performance record](performance-v080.md#full-numberconcept-upgrade-matched-native-cli-observations)
provide the asset hashes and complete measurement interpretation.

Install both the Native package and VSIX from `dist/`, reload the editor window,
and restart the language server. Recompile old `.mso`/`.mexe` files. Existing
same-version installations must not retain a stale `msync.serverPath` binary.

## Standard data composition and allocation follow-up

This incremental delivery supplies reusable source-backed concepts for type-system
composition, including product/sum protocols and Option/Result construction.
Public constraint names use `$std::concepts::Name`. Concepts remain compile-time
constraints, not runtime values or new carrier kinds. Mathematical operation
dictionaries are a separate concern, not a substitute for these data protocols.
The library architecture uses named Scope namespaces, language-native Fn/Scope
specialization and explicit policies. Unpublished factory-method bags and
compatibility adapters are removed rather than retained as alternate APIs.
Canonical class namespaces use PascalCase and functions use snake_case.

Changed collections and services require an integrated policy-driven journey in
addition to the core data constructors. Ordered structures and closed-type
metaprogramming traits receive their own scoped integration journeys. Typed
terminal tools and persistent direct REPL commands form another real-IO and
prompt journey on both native platforms. Object/closure allocation and Number
execution are a separate measured workstream; IEEE binary64 behavior, mutable
shared locations and Scope identity remain fixed.

The baseline is the preceding accepted Native source
`a952448083a6f0bea9ee2def0e775dcd804500f7` from run 42619095812. The existing
packages and evidence above are that baseline, not acceptance for this follow-up.
Incremental Native acceptance selects `option`, `data`, `concepts-tooling`,
`number-allocation`, `static-optimization`, `library-policy`, `command-policies`,
`trees`, `type-traits`,
`terminal-library`, `shell-repl` and `stdlib-cost` under `polish_phases=upgrade`.
An explicit phase selection is not broadened to unrelated remote-library work
by the Linux SSH-fixture option. Already accepted frozen-context tests run only
when the selected language or path contract actually requires them.

Final Native acceptance records cold CLI controls, selected List construction,
meta and shell checks, actual first prompt and the first List namespace
completion in `stdlib-cost`. Its JSON and terminal transcripts are retained
under the platform-results directory. These absolute Native observations are
distinct from the performance owner's matched managed before/after allocation
measurements.

The selected static-optimization journey also inspects final bytecode for
public Buffer read/count reuse, while preserving distinct buffer identities and
callee-before-argument evaluation across source and both binary stages. It does
not repeat the optimizer owner's internal graph/effect matrices.

### First Native wave and focused repairs

Run [42646810447](https://github.com/kleedaisuki/msync/actions/runs/42646810447)
built source `7b891eacedb0593bfb6a2f927616d6b0ab536f53`. The immutable images were
Linux `0b45d226284aa6a8f927bddc9653542925aaf3673c992e82da424c11fd7b7cc2`
and Windows `3d35993771224559e08405c9454f01a51fd7912f660b7932504e784d5dd43415`.
Both passed data/Option, concept tooling, runtime Number/object, static Buffer
optimization, selected command policies and all four ordered-structure journeys.
Linux additionally passed terminal-library and actual PTY REPL work. Both
platforms produced the focused absolute Native component and cold-cost reports.
This wave is not an aggregate success: it exposed two nullable-flow regressions
in lazy Range arguments and two Windows-specific product issues.

The repairs preserve the language contract instead of rewriting library callers:

- `6e50f37` tracks stable local locations by declaration identity. Narrowed,
  unwritten locals remain usable as lazy Range arguments, while actual future
  writes and persistent REPL locations retain their sharing semantics. The owner
  accepted the prior 192 and new 101 focused contracts and the actual failing
  class-library/type-trait programs, without adding guards or casts to stdlib.
- `ebfcbcf` makes tracked `.msync` source use LF on every checkout. Literal LF in
  executable standard sources is data, not the host's preferred line ending.
  In particular `wc` must count LF bytes even in a Windows-built product.
- `887da51` configures UTF-8 without BOM at the Windows human-console boundary
  and restores the prior output encoding afterwards. It leaves input encoding,
  paths and fully redirected/language-server transport unchanged.

The immutable Windows A/B run
[42649863293](https://github.com/kleedaisuki/msync/actions/runs/42649863293)
proved the console diagnosis: both default and CP65001 input contained six UTF-8
bytes for the two CJK characters and both changed into the real Unicode folder;
only default-code-page painting lost the characters. The formal Unicode paint
assertion remains strict. Raw evidence is retained under
`artifacts/ci/42649863293/windows/`.

The second Native wave selects `library-policy`, `type-traits`,
`terminal-library`, `shell-repl`, `concepts-tooling`, `number-allocation` and
`stdlib-cost`. It rebuilds both current packages and records final-image costs,
while reusing the first wave's unaffected ordered/data/static-optimization
platform evidence rather than repeating the tree/property matrices.
