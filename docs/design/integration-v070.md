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
