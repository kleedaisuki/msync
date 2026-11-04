# v0.7.0 standard library integration

## Delivery contract

Starting point: `dd2c7a2` (v0.6.6), development branch `codex/v0.7.0`.
User-owned untracked `docs/analysis/` is unrelated and must remain outside commits
and packages. The requested release is v0.7.0, including Linux and Windows
packages, the VSIX, and checksums. Published assets and historical tags are
immutable.

The language gains a typed `$std` namespace, with `$std::ranges` and `$std::fs`.
Namespace aliases are values inferred by the existing type system, not special
spellings. Explicit `scope name:` blocks expose lexical members. Ordered ranges
must carry Scope values, including selected endpoints passed to ordinary macros.
Existing endpoint scopes, range APIs and old source/object contracts continue
working.

Standard-library operations expressible in msync should have ordinary source
bodies, available both to specialization and editor definition navigation.
External operations use explicit `native` declarations and typed calls, including
void results and each supported return domain. Filesystem calls execute against
the actual selected endpoint at runtime, never during checking or preview.

## Ownership and integration boundaries

| Workstream | Owned implementation | Integration responsibility |
| --- | --- | --- |
| Language lead | Parser, typed namespace model, compiler, HIR/object contract, runtime native dispatch, source library | Coordinate frontend/range workers and native signatures |
| Filesystem lead | Native filesystem backend, endpoint execution, filesystem contract and focused journeys | Agree signatures with language lead; avoid alternate type systems |
| Tooling lead | Index, LSP, highlighting, VS Code presentation and real editor journeys | Navigate the actual bundled source on installed systems |
| Root | Public user journeys, packaging, user documentation, version agreement and release | Review and integrate cross-workstream behavior |

All work shares one checkout. Commit only coherent owned files; do not sweep up
another workstream's partial changes. The hierarchy is limited to root, leads,
and bounded workers.

## End-to-end acceptance

1. The supplied nested loop over local/SSH/WSL Scope aliases type-checks without
   authenticating. An equivalent all-local endpoint journey publishes actual
   bytes from source and source-free objects/IR.
2. `scope hello: codex := ...` resolves members without leaking declarations;
   namespace aliases, nested namespaces and normal lexical functions compose.
3. Filesystem predicates distinguish files, directories and links at execution
   time. Creation, enumeration, copy/rename/link/removal and lexical path
   operations obey their documented local and remote endpoint contracts.
4. `check`, `plan`, compilation and dry-run do not perform native effects or
   authenticate unselected endpoints. Selected native effects participate in
   execution ordering, diagnostics and cancellation.
5. Standard source functions specialize normally; native scalar, Range and void
   calls survive source-free object/IR loading. New executable semantics carry
   negotiated object features rather than changing legacy bytecode meaning.
6. An installed language server offers completion, hover and definition jumps
   into the same standard-library source that the compiler uses.
7. Local formatting, strict static analysis and contract-selected process tests
   precede publication. Native Windows/package/editor acceptance supplements
   these checks, without adding CI quality gates or waiting on unrelated suites.

## Design basis

The C++ [filesystem synopsis](https://eel.is/c++draft/filesystem.syn) supplies an
operation inventory, not a requirement to invent numeric/status/clock types that
msync cannot currently express. The library must cover the expressible operations
and explain the remaining type gaps in its filesystem note. Rust's
[intrinsics declarations](https://doc.rust-lang.org/std/intrinsics/) illustrate
the useful boundary between source-visible signatures and runtime/compiler-owned
implementation. msync keeps that boundary explicit instead of making ordinary
user function spellings magical.

The existing [type contract](type-system-v065.md),
[normal-copy model](statement-atomicity.md), and
[local-first release policy](../../AGENTS.md) remain authoritative unless this
release intentionally extends a named contract.

## Native execution and object contract

A native call is explicit typed IR, not an executed source fragment:
`ExprKind::Native` carries scalar results, `RangeSource::Native` supplies demanded
range values, and `ControlInstruction::Native` discards a result (including Void).
All three retain the implementation identity and typed argument expressions.
The object loader resolves the same signature registry used by specialization,
checks arity and argument/result types, and negotiates `native_std_v1` with the
0.7 object language. New lexical namespaces and unbound Path provenance also
require that feature; historical 0.5/0.6 object representations remain accepted.

The runtime evaluates each argument once. Pure lexical/text operations do not
wait for filesystem work. Read/write operations wait only for active flows whose
actual evaluated Path arguments overlap; endpoint metadata alone does not admit
SSH or join unrelated flows. Native directory ranges initialize on first demand,
so `directory_iterator(missing) | $std::ranges::take(0)` performs no enumeration.
Native failures keep the original statement span and backend/path diagnostics.
Ordinary publication remains owned by the existing transfer machinery.

`tests/native_runtime_v070.rs` is the focused executable companion: it covers
source-free native range/mutation execution after cwd relocation, signature and
feature rejection before execution, zero-demand range laziness, raw lexical Path
projection, and explicit Local endpoint provenance through source-free objects.
All six tests passed in the integrated local Cargo window. The same window passed
all six namespace journeys (including a computed Path member name through a
source-free Scope range), both source-standard-library journeys, and the six
existing v0.6.5 type-composition journeys. The command was
`cargo test --locked --test native_runtime_v070 --test stdlib_v070 --test types_v065 --test namespaces_v070`.

Native lexical Path results keep their textual value independently from the
current interpreter process directory. A Local relative result becomes the same
compact raw-plus-local-projection value used by unbound range paths: string/path
inspection sees the raw name and local I/O uses the captured module cwd. A compact
runtime provenance bit retains endpoint binding: pure projections inherit the
first input's binding, while filesystem observations bind their result to the
selected endpoint. Only an unbound projection can acquire a different endpoint
through later qualification. Absolute results remain explicit, and remote results
retain their backend. This applies equally to names derived from an explicitly
Local input and to relative symlink target text; inferring behavior from a
particular native function's spelling is unnecessary.

## Local acceptance record (2026-11-02)

The checks were selected by changed contracts, not a full historical regression
run. Process tests inspect actual files and executable source-free artifacts.

| Journey / selected command | Result |
| --- | --- |
| `cargo test --locked --test acceptance_v070` | 3 passed: full supplied script checking/planning/object/IR/dry-run, nested namespaces and Scope distribution publishing bytes from source/object/IR, runtime observations changing without recompilation and an inert unselected SSH branch |
| `namespaces_v070` | 7 passed: scoped functions/macros, nested/aliased membership, missing-member errors, REPL/formatting, qualified keyword calls, exported standard API aliases, source-free lexical Scope ranges, inferred native syntax |
| `ranges_v070` | 3 passed: supplied endpoint lists, callbacks/consumers from object/IR, source Range returns snapshot a native argument exactly once and remain lazy under `take(0)` |
| `ranges_v065`, `ranges_v6`, `types_v065` | 7 + 6 + 6 passed: compact finite-union representations, typed callbacks/consumers, captured path contexts, operator rejection and computed Scope single synthesis |
| `native_runtime_v070` | 6 passed: source-free native execution with cwd relocation, negotiated features/signatures, zero-demand observations, raw lexical projection and explicit endpoint provenance |
| `stdlib_v070` | 2 passed: all embedded sources parse/format/compile; source compositions, inferred host natives, inferred dependent compiler natives, Unicode text and real mutations |
| `filesystem_v070`, library `native_fs` slice | 2 + 9 passed: native lifecycle, permissions, lexical/components/traversal, links and >64KiB directory framing; an additional 4 focused checks cover C++ ENOTDIR absence and preserved loop diagnostics |
| `config_cli_v6` | 22 passed after deriving the default syntax from the canonical language version |
| Highlight / lexical index / stdio LSP / REPL | 11 highlight, 4 new index, 11 old-v6 index, 5 old-v065 index, 7 provider and 5 editor checks passed; the real stdio LSP journey passed |
| `npm test` in the VS Code extension | 16 passed with the real TextMate/Oniguruma grammar and native source provider |
| Real VS Code 1.144 host | 13 phases passed, including opening actual inferred range Native declarations and navigating inside read-only source; [host evidence](tooling-index-v6.md) records the frozen image |
| Formatting / static analysis | `cargo fmt --all -- --check`, whitespace checks and strict Clippy for library, binary and all changed/new test targets passed; the final inferred-native delta passed a separate narrow strict window |
| Packaging scripts | PowerShell 7.6.9 passed both selector test groups and parsed both Windows packaging scripts; Bash syntax checking passed |

The initial all-target Clippy attempt encountered the unchanged single-element
loop in `tests/telemetry_sqlite.rs:252`. No unrelated telemetry cleanup or lint
suppression was bundled into this feature. Strict changed-contract checks above
are the accepted scope; there is no claim of a complete historical test run.

### Actual native SSH

`python3 tests/stdlib_ssh_v070.py target/acceptance-v070/msync` passed against the
real loopback OpenSSH fixture, without replacing transport with shell mocks.
The final feature image SHA-256 is
`d7d591d5ce0a99f41bf7322d99869a8a9c34384142b27ee44c10fd38eabd554e`;
its source-free `.mobj` and JSON IR journeys cover filesystem predicates,
creation/rename, raw relative links, POSIX permissions, native callback filtering,
directory iteration and download. The remote pathname contains Unicode, quotes,
`$()` text and a newline. Checking/planning/compilation/preview used zero SSH
clients and created no remote directory. The separate ordinary file transfer
used exactly one SSH client, one authentication and one remote exec, and no SCP.
Disposable reports and fixture logs live under `.temp/v070/`; the final record is
`ssh-final-results.json`. The earlier successful pre-refinement image and report
remain separately retained. This is Linux loopback SSH acceptance; native WSL
and a user's WAN timing remain separate workloads.

### Native package acceptance

`packaging/stdlib-smoke.msync` is a shared installed-program journey for Linux
and Windows. It checks native filesystem effects, lexical members, Scope ranges,
inferred standard namespaces and String primitives from a compiled object after
the script is removed. Preview must not create its output tree. Portable package
acceptance also requires all five readable standard source files.

The locally built optimized Linux archive passed its checksum, contained all
five sources byte-for-byte, and excluded user-owned `docs/analysis`. Its installed
binary passed the shared source-free smoke journey and the full actual SSH
object/IR fixture above. Binary SHA-256:
`52ac10ab332947ad641f0e9e81a9dde5678cafff3c366b48b96e14a2b21c18be`.
The local archive SHA-256 is
`084569325086ecccfc79682143963032b6ef0fc8306529a88b5bd835c162edd3`;
the official Ubuntu-baseline archive built by Actions has a separate identity.
Optimized-package SSH results are `.temp/v070/ssh-package-results.json`.

### Historical executable artifacts

The published v0.6.6 Linux archive was verified against its existing SHA256SUMS.
Its executable (`5224927dabebd927fc0dd767a7ab17fa65f434594fb810723c6091df1fbb0d91`)
compiled a 0.6 script containing a lexical macro, file transfer, Bool range and
`$view::take` into both `.mobj` and JSON IR. After deleting the source, the v0.7
executable ran both artifacts from a different working directory and produced
the expected copied bytes and Bool-selected marker. Fixture paths are
`.temp/v070/legacy-producer` and `.temp/v070/legacy-consumer`; the immutable
historical producer is retained under `target/compatibility-v070`.

## Native release packaging integration

The first official [package run](https://github.com/kleedaisuki/msync/actions/runs/41473527239)
passed the Ubuntu-baseline Linux package and packaged-binary VS Code host.
Windows portable acceptance passed, then actual installed acceptance detected
that Inno Setup's explicit file list omitted the newly staged `share/msync/std`
directory. The installer manifest now installs that directory with the same
recursive ownership rules as packaged documentation. No runtime or compiler
change was needed, and the source-presence assertion remains in installed
acceptance. Publication was skipped; no v0.7.0 tag or public asset existed.

## Published release

The corrected [release run](https://github.com/kleedaisuki/msync/actions/runs/41474318621)
passed native Windows portable/install/repair/upgrade/PATH/uninstall acceptance,
the official Ubuntu-baseline Linux package, and the actual VS Code host against
the packaged binary. It published [v0.7.0](https://github.com/kleedaisuki/msync/releases/tag/v0.7.0)
at 2026-11-02 23:47:26 +08:00. The immutable tag and release target both point to
`69b1921b7322a614baac1730fa6ec73b4cd2533b`.

All four public assets were downloaded to `dist/releases/v0.7.0` and matched
the release's SHA256SUMS. The public VSIX manifest is version 0.7.0, and the
Windows portable ZIP contains all five standard source modules.

| Public asset | SHA-256 |
| --- | --- |
| `msync-0.7.0-linux-x86_64.tar.gz` | `9141953d437667cff1a23186c2bc79262ba2a8beabe0787287433f3a7d141ee8` |
| `msync-0.7.0-windows-x86_64-setup.exe` | `e7a47601e4c5efdf5c43531f24d05da23c601a868eda9d894cb4bcee3cd9cc5c` |
| `msync-0.7.0-windows-x86_64.zip` | `54d0758d3dd1efe57fd6f7656e8b2955090c6aa94f1a4711b4ad3fc7a311dc21` |
| `msync-language-0.7.0.vsix` | `d7368b237021a73f3d050f6a309aab64828c517273541ac5881e258597d72961` |

The separate initial Rust workflow passed its library tests, then failed the
unchanged Linux binary assertion
`object_export_never_replaces_its_source_or_a_readonly_output`: replacing the
readonly output succeeded instead of being rejected. Both that assertion and
the artifact writer are unchanged from the starting v0.6.6 source. Its Windows
job was superseded by the packaging-only follow-up. It is not counted as
full-project Rust acceptance. The release uses the focused local checks and
native package/editor journeys recorded above.
Previously published tags and asset bytes were not replaced.

## Published patch v0.7.1

The dynamic qualifier operand fix is commit `1754bd4`; release preparation is
`fbdb30dd1dea10c955621f4239a8fa4be9cb8d93`. The immutable `v0.7.1` tag and release
target both point to the latter. The
[release run](https://github.com/kleedaisuki/msync/actions/runs/41595828293)
published [v0.7.1](https://github.com/kleedaisuki/msync/releases/tag/v0.7.1) at
2026-11-03 15:27:49 +08:00.

Native Windows portable and installed-program acceptance passed, including the
new nested endpoint/path macro loop compiled to an object and executed after
source deletion. Both target files' bytes were checked using the packaged
executable. Install, repair, cross-version upgrade, PATH and uninstall acceptance
also passed. Linux package and actual packaged-binary VS Code host acceptance
passed. All package versions agree at 0.7.1.

All four public assets were downloaded to `dist/releases/v0.7.1` and matched the
published SHA256SUMS. The Windows ZIP's complete internal manifest also matched;
it contains all five standard source modules. Its `bin/msync.exe` SHA-256 is
`6f124c08862ee77d878f6bb8e966bfca321d72735911bc9cd86ad8d28ac1ce93`.
The public VSIX manifest reports 0.7.1. The downloaded Linux executable reports
0.7.1 and passed the shared source-free macro regression smoke locally. Download
and smoke scratch evidence is under `.temp/release-v071/`.

| Public asset | SHA-256 |
| --- | --- |
| `msync-0.7.1-linux-x86_64.tar.gz` | `8f389667bcbbcb8fc2456e133adb72090520cd525de403f3d5373eee41321611` |
| `msync-0.7.1-windows-x86_64-setup.exe` | `5992783b4ad0b4966300df32f99832e732538e2d8a4cbef164118fc976dc2cf0` |
| `msync-0.7.1-windows-x86_64.zip` | `d91959e5a6fbd38aa3917e68f13278cd522cb9dc23f932d150cc9034135991b5` |
| `msync-language-0.7.1.vsix` | `d79901e4ccd31110900602494966ce8a8adf47e889c28babf9735dc6207ed44b` |

The independent [Rust run](https://github.com/kleedaisuki/msync/actions/runs/41595828315)
is not a complete-project pass. Ubuntu repeated the previously recorded
`object_export_never_replaces_its_source_or_a_readonly_output` failure after all
246 active library tests passed. Windows passed 227 library tests and failed two
unchanged fixtures: `local_filesystem_lifecycle_and_endpoint_values` attempts to
create a filename containing a newline, rejected as InvalidFilename;
`a_shared_capture_root_excludes_other_live_workers_without_hiding_user_scratch`
fails its path `strip_prefix` unwrap. The affected test files and `src/main.rs`
are unchanged from v0.7.0, and these direct fixture failures do not execute the
modified compiler selection branch. No unrelated fixes or CI suppressions were
bundled into this patch. Local focused source/object/IR transfer acceptance,
strict changed-target Clippy and formatting passed before push.

## v0.7.2 operator and editor convergence

The frontend now preserves unary reference and binary selection nodes instead
of encoding `$name` and compact `scope::item` as special qualified paths. It
continues to specialize into the established typed IR; this is not a second
runtime type system. Native argument admission shares the registered capability
with object validation and reports the offending argument's inferred type.

The central interoperability contract is `(String | Path) / (String | Path) ->
Path`, not Windows-path recognition. Quoted strings remain data unless consumed
by a path operator. Independent real-process acceptance uses all four operand
pairings, lexical String and Path aliases on either side, native observations,
and changed file bytes through source, source-free objects and JSON IR.
`tests/operator_ux_v072.rs` covers these journeys with strict targeted Clippy.
The older real transport macro regression also passes after compact selection
and parenthesized selection were unified.

Filesystem completion queries the compiler for the parent value's type and
location. Only selected Local locations enter the shared filesystem path binder;
remote endpoints never authenticate during editing. Completion labels retain
normal filenames while inserted basenames are String fragments, so the same
`::` and `/` capabilities produce paths without lexical-name shadowing or a
second filename parser. Query-only declaration sessions are cached independently
of executable sessions. Hover uses the same inferred value types.

Final interoperability inspection exposed a remaining content-inference defect:
`"."/"<literal>"` became Range despite both input types being String. Selector
intent now originates in explicit unquoted source syntax, not produced Path
bytes. Source and destination paths formed from Strings retain their type;
transfers and compile-time script composition no longer rescan them for selectors.
Existing typed selector objects remain executable. Quoted-selector source should
use a quoted base followed by an explicit selector, such as `"directory"/<.*>`.

Intrinsic member completion likewise follows the resolved receiver capability,
not its source spelling: aliases of File/Pwd retain their members, while lexical
values named `file` or `pwd` do not fabricate intrinsic members. Directory
projection and member metadata come from one compiler receiver synthesis.

The platform package smoke now replays all four String/Path pairings through a
source-free object. Windows additionally uses its actual quoted drive path,
as a platform acceptance case of the general type relation. Existing installed
lifecycle checks and immutable release publication remain in place.

### Focused local delivery checks

- Frontend, types, namespaces, native runtime and source provenance: 90 selected
  cases; strict library/changed-target Clippy. The real transport macro regression
  passed without reintroducing a qualifier-specific resolution path.
- The selector/capability correction additionally passed 59 affected compiler,
  property, source-provenance, range and script-expansion cases, with strict
  library/changed-target Clippy. Independent review replayed dynamic endpoint
  selectors and literal angle-named include/execute scripts through source,
  source-free objects and IR.
- Independent source/object/JSON IR user journeys: 8 cases, including all four
  generic String/Path pairings, runtime mutable references and inferred native
  results, plus literal selector-shaped source/destination paths; strict
  changed-target Clippy.
- Final real LSP-to-CLI journeys passed 2/2; selected provider, tooling and
  highlighter checks passed 11, 20 and 23 cases respectively. The final LSP
  capability/selector deltas passed protocol acceptance and strict Clippy.
  Extension Node tests passed 21 cases.
- The actual VS Code 1.144.0 host applied root/hidden-directory/spaced-filename
  completions, checked separate operator tokens, navigated embedded sources and
  reconnected through `msync.restartLanguageServer`. Its nonexecution fixture
  retained the deletion victim and created no DSL outputs. Evidence is under
  `.temp/vscode-host-c4VQqo` (frozen server SHA-256
  `f2b069caf5fd41cd60b1b8553fa710f0020a578b30a71fa28e94239f9d3b19b5`).
- The 0.7.2 VSIX built successfully and installed with the actual Code CLI into
  an isolated extension directory, reporting `msync.msync-language@0.7.2`.
- A locally extracted optimized Linux archive passed check/dry-run/source-free
  native and String/Path package smoke, with all expected output bytes. Local
  build/install/package evidence is under `.temp/release-v072`.
- `cargo fmt --all --check` and `git diff --check` passed. No complete-project
  regression matrix was used as a substitute for these changed workflows.

### Published v0.7.2 and public artifact verification

[PR #6](https://github.com/kleedaisuki/msync/pull/6) merged into main as
`952850d641b3ae57427fea0a03d1448234c2eee9`. The immutable `v0.7.2` tag and release
target both point to that commit. The
[official release run](https://github.com/kleedaisuki/msync/actions/runs/41826442995)
passed Windows portable/install/repair/upgrade/PATH/uninstall, the Ubuntu-baseline
Linux package and the real VS Code host against its packaged binary. It published
[v0.7.2](https://github.com/kleedaisuki/msync/releases/tag/v0.7.2) at
2026-11-04 20:12:06 +08:00. Superseded branch/PR runs were cancelled after merge;
publication did not wait for the separate Rust or editor CI status.

All four public assets were downloaded into `dist/releases/v0.7.2` and matched
the published SHA256SUMS. Every one of the Windows ZIP's 142 internal manifest
entries matched. Its executable is native x86-64 with SHA-256
`1eba793948ae69fec83422e15318fcd35a03cd2b866c68dff0d6964ac61d7a5c`.
All five Windows standard sources match their Git source after native CRLF/LF
normalization; the package and its embedded standard sources come from the same
Windows checkout, not the Linux checkout's byte representation.

The public VSIX reports 0.7.2 and every payload file is byte-identical to the
locally installed/tested VSIX. Its bundled client SHA-256 is
`a782ee9d454d5793129b655b312d75f90a525de0ac21562fca7eddf454752997`.
The public Linux executable is native x86-64, reports 0.7.2, and passed the full
expanded source-free native/operator package smoke after extraction, including
all String/Path pairings and selector-shaped literal Path content. Public audit,
package smoke and native logs are under `.temp/release-v072`.

| Public asset | SHA-256 |
| --- | --- |
| `msync-0.7.2-linux-x86_64.tar.gz` | `d6b9dea6632c243a42291c6ac8903fb2515e8810f7f5db1d270f4b7836d6f52a` |
| `msync-0.7.2-windows-x86_64-setup.exe` | `f8e23b0440a7d4ceec33bf9576167af4fc5071043e27e9ec0bcb7a9f147dde66` |
| `msync-0.7.2-windows-x86_64.zip` | `94e87a758a01831262e21324385b95e1dc6434365f43fcb042c318f2268cc439` |
| `msync-language-0.7.2.vsix` | `f2c681895144b58ff33dae7bd8fd5e2586f3b1a28608181de1352517e998a7be` |

### Supplemental compatibility follow-up on main

The independent [Rust run](https://github.com/kleedaisuki/msync/actions/runs/41826443001)
passed its library suites (Ubuntu 254 active cases, Windows 237), but is not a
complete pass. Both platforms failed the same changed-contract fixtures in
`cli`, `cli_v052` and `repl_terminal`: old unknown-endpoint diagnostic ownership,
quoted script selector inference, and merged `$namespace::` color spans.
The actual typed diagnostic was correct; a strengthened assertion also exposed
lost nearest-binding help that the old source-excerpt assertion did not detect.

Test-only commits `e8472e4` and `06e42d6` migrate those expectations without
weakening the real copy, object/IR, editing/history or source-span journeys.
Two failed PTY journeys and all 27 CLI cases now pass locally, with strict
changed-target Clippy and formatting. Commit `2e3adac` restores nearest semantic
binding help only on error paths, preserving the typed error, operator span and
operand label. Its four-scenario compiler unit and strict library/CLI Clippy pass.
This small diagnostic improvement is explicitly **Unreleased** on main; it is
not represented as part of the already-published 0.7.2 bytes. No tag or asset was
moved or replaced, and no CI assertion was skipped or disabled.

The final [native follow-up Rust run](https://github.com/kleedaisuki/msync/actions/runs/41828249607)
at `69257cb5577cdd4f9bc6ceb0e1a0e259b6dbcde3` passed on Ubuntu and Windows,
including the migrated CLI and actual terminal journeys. The separate
[editor follow-up](https://github.com/kleedaisuki/msync/actions/runs/41828249465)
passed its actual VS Code host as well. This is changed-contract/native evidence,
not a full-project matrix or a reason to replace the already-verified release.
The published tag remains `952850d641b3ae57427fea0a03d1448234c2eee9`.
