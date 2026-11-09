# Native AOT packaging in v0.8

## Current complete local delivery set

The current mutable-state/effect pipeline source is `372296b`, still version
0.8.0. Default compilation is O2; O1 remains explicit for lower compilation cost,
and O3 is an explicit tier. Current artifacts use format 3 with independent
format-1/2 reading compatibility. No public release, tag or pull request was
created.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `msync-0.8.0-linux-x86_64.tar.gz` | 6,349,717 | `c45310ec364bcff2a51bc2e6b34f44d240e4f429f152ade24c096b0bae53e799` |
| `msync-0.8.0-windows-x86_64-setup.exe` | 6,815,534 | `01f4b5207c4e6594b7fc744dd4107f4563b0c684b89befb2cdf31bd8b631c5df` |
| `msync-0.8.0-windows-x86_64.zip` | 6,831,521 | `46b2857b8f9c289db7e4b43da335f4c4b6c950982aaffa537aac3ca3b504c899` |
| `msync-language-0.8.0.vsix` | 125,182 | `f9f8103a8f1ee7f9259ea8348624d65219069f87c06029594b00fb1f1d73be68` |

`dist/SHA256SUMS` covers exactly these four. Both portable manifests were verified
completely after extraction: 225 entries each, including all 59 tracked files of
the modified MPL-2.0 prompt source. Packaged C# source bytes match the repository.
The accepted Linux executable is SHA-256
`6106c20a0c9e119f917419a07db2e5b7f7e383138e0d4f4ccf7bd65acb0e9225`;
the accepted Windows executable is SHA-256
`8cb08a0d75c361ccbd5c5481641f818f00d7702509f3d90b40367019ed0affa3`.
They were copied unchanged from
[the final native publication](https://github.com/kleedaisuki/msync/actions/runs/42518195849).

Acceptance is incremental by changed contract. The initial
[state run](https://github.com/kleedaisuki/msync/actions/runs/42514423642)
passed mutable source/object/executable byte programs, causal process scheduling,
all selected optimizer equivalence projects, old format-2 captures, presentation
and navigation. It exposed a nullable getter schema error, the stale format-2
writer assertion and Windows directory-link replacement failure. Those were
corrected and the exact failed contracts, O2 defaults/O3 options and old format-1
execution passed on both hosts in
[the closing run](https://github.com/kleedaisuki/msync/actions/runs/42516326981).
The Windows namespace program verified real symlink creation, retransmission,
retargeting, nonempty-directory obstruction preservation, owned-stage cleanup,
retry, tree replacement and future-alias WAR ordering without a blanket skip.

The final Core-only linear normal form then received its own focused
source/`.mso`/`.mexe` bytes, bound-cwd, source-span/error and structural proof.
The direct-atom fixture was corrected to exercise that fast path rather than
falling back through `PathJoin`: all twelve actual Linux cases passed against
the final image, and
[the Windows fixture-only follow-up](https://github.com/kleedaisuki/msync/actions/runs/42519245409)
reused the identical Windows image without a native rebuild or repeated Core
matrix. That report distinguishes native source `372296b` from fixture source
`3b8cb6a`. Matched performance and platform observations are recorded in
[the performance note](performance-v080.md), retaining exact image identities.

The real VS Code 1.144.0 host ran against the final Linux image at
`.temp/vscode-host-A1eJY4`. It passed three editing revisions of a 5,001-declaration
outline, meaningful typed navigation/hover, all eighteen semantic roles,
UTF-16 ranges, nullable diagnostics, formatting and restart without runtime
effects. All eight VSIX payload files were compared with that tested client,
grammar, snippets, documentation and notices. Raw collation metadata is
`.temp/final-mutable-pipeline-delivery-v080.json`.

Earlier native SSH/WSL authentication, installer upgrade, transfer metadata,
foreground ownership and editing records remain in the associated design notes.
Their accepted evidence was retained instead of running an unrelated blanket
matrix for every subsequent optimizer change. Documentation updates do not
trigger native republication.

The product is published from `src/Msync.Cli/Msync.Cli.csproj` using .NET 10
Native AOT. The independent Rust implementation in `legacy/` remains a semantic
baseline, not a dependency of the new executable or release payload.

## Build and acceptance

```text
python packaging/build.py --rid linux-x64 --smoke
python packaging/build.py --rid win-x64 --smoke
python packaging/build.py --version
```

Build on the target operating system. Native AOT does not support ordinary
cross-OS publishing because platform SDKs and native linkers are required; the
[upstream cross-compilation contract](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
is the reason for the native Windows/Linux jobs, rather than assuming a Linux
build establishes Windows compatibility. Linux package builds use Ubuntu 22.04
so the glibc floor remains the established 2.35 baseline. Install `clang` and
`zlib1g-dev` as required by the
[Native AOT deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

`Directory.Build.props` owns the numeric product version. Packaging checks that
it agrees with the editor manifest, runs the published executable's `--version`,
and includes the exact executable rather than a framework-dependent build.
Published binaries and staged payloads live under `artifacts/`; distributable
archives live under `dist/`. Disposable journey workspaces live under `.temp/`.

## Focused presentation and editor acceptance

The manual native workflow accepts `polish_only=true`. It builds the current
Windows and Linux Native AOT packages, exercises their installed-layout smoke,
then invokes `packaging/polish_journeys.py` against those exact executables.
The selected contracts are CLI color/NO_COLOR and source guidance, inferred
Scope/function semantic roles, useful hovers, related generic-call locations,
applicable diagnostic quick fixes, cancellation/latest-snapshot editor races,
source-deleted artifact runtime diagnostics, width-aware literal-preserving formatting,
the progressive built-in `msync skill` reference, and contextual path/host
completion. Linux completion uses a real encrypted loopback SSH endpoint.
Windows completion imports one owned pinned WSL1 distro for directory queries
and a controlled guest SSH server. Native Windows OpenSSH then exercises the
real host-key/private-key prompt lifecycle from a spaced product/key path.
The distro is unregistered afterwards; `--completion-only` excludes all previously
accepted file-transfer and interruption cases.
Both hosts additionally check automatic color and NO_COLOR through actual
terminal descriptors (a POSIX PTY or Windows ConPTY), without repeating the
accepted editing/foreground-input suite. The POSIX fixture distinguishes stdout
and stderr terminal ownership independently. Windows additionally proves a
bright-black binding ghost suffix does not enter submitted source until the
user accepts it with RightArrow, using exact destination bytes as the oracle.
The native askpass helper is also launched from a spaced product path on both
hosts, checking UTF-8 answers, repeated prompt ownership, configured helpers and
cancellation against the same packaged executable.
Each phase retains stdout/stderr and the native image hash under
`.temp/platform-results`; one failed phase does not suppress another's result.

This selection deliberately excludes previously accepted transfer metadata,
WSL copy/interrupt cases, installer upgrades and performance measurements. The existing editor host
journey exercises the changed client with an actual VS Code host; the final VSIX
is packaged from the same tested source. Native package and VSIX version remain
0.8.0, with no public release/tag implied by local candidate assembly.

For local targeted work use `python scripts/check_local.py --journey polish`.
For the actual platform run, dispatch `ci.yml` with `platform=both`,
`polish_only=true`, `benchmark=false`, `wsl=true`, `console=true`. The latter
select the changed endpoint/console journeys, not the earlier transfer/editing
matrices. `polish_phases=editor` selects only presentation, analysis lifecycle,
response/resolve caching and contextual completion (with actual SSH on Linux).
It excludes formatter, skill, authentication, WSL and console suites. A final
source-freeze commit can carry `[skip ci]` to avoid redundant automatic broad
runs while preserving explicit workflow dispatch. For fixture-only diagnostics,
`reuse_run=RUN` with `polish_only=true` reuses that recorded Windows payload
without publishing another native image. The report distinguishes the fixture
checkout revision from the original native source revision and image SHA-256;
the complete downloaded artifact identity is retained alongside it.
`polish_phases` can select only `portable`, `editor`, `completion`, `authentication`,
`presentation_console`, `hints_console`, or `wsl` (comma-separated); an empty
selection runs all groups. One validated group map controls admission so a
fixture-only retry can run only the failed console/endpoint contracts. The
portable runner also accepts repeated `--phase NAME` selections; a `completion`
group plus `authentication` reruns only contextual completion and real encrypted
authentication, not already accepted formatting/guide/color scenarios. The
current [workflow-dispatch contract](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onworkflow_dispatchinputs)
allows 25 inputs; this workflow uses 11.

## Mutable-state and optimizer native acceptance

The `state` selection in `ci.yml` targets the mutable language and effect-aware
execution pipeline without repeating the accepted editing or transport matrix.
It runs actual CLI mutable programs through source, relocatable and bound
artifacts, causal process scheduling, persistent REPL recovery, and focused
presentation/navigation checks. `packaging/state_contracts.py` separately labels
managed equivalence/structural checks, old format-1/2 compatibility and actual
namespace/symlink publication on each target OS; Windows requires the real
symlink operations rather than silently skipping those cases.

Windows also publishes one Native AOT Components observation image and retains
raw O1 optimizer timings/allocations for tiny/32/128 call/Scope workloads and
128/1024 ordinary-transfer compilation. Those are observations, not timing
thresholds. The selected Windows CLI comparison uses the recorded Rust 0.7.3
image from run `42471357057`, the frozen C# editing image from `42506951530`,
and the current native payload on the same runner. Their exact source/hash
identities are recorded by `packaging/windows_threeway.py`; no Cargo rebuild is
needed, and an expired optional historical artifact is reported explicitly.

## Dependency graph ownership

`global.json` pins SDK 10.0.403, including its ILCompiler/ILLink 10.0.14 tool
packages. CI installs that exact SDK rather than selecting a moving `10.0.x`.
SDK upgrades and dependency-lock updates are one deliberate maintenance change.
NuGet's canonical `packages.lock.json` pins the ordinary development graph.
Targeted publication uses `packages.linux-x64.lock.json` or
`packages.win-x64.lock.json`, selected by `RuntimeIdentifier` in the common build
properties. This separates actual platform graphs instead of letting a native
publish overwrite the ordinary development lock. No runtime packs for the other
platform are needed during an ordinary development build.

The CLI project owns `PublishAot=true`. The packaging command does not pass that
property globally to project references, which would incorrectly add executable
AOT compiler packages to every class library. Publication uses
`RestoreLockedMode=true`: dependency changes require a deliberate lock update,
not an incidental version upgrade in a package job.

To update the three graphs, restore the solution normally and with
`-p:RuntimeIdentifier=linux-x64` / `-p:RuntimeIdentifier=win-x64`. `dotnet restore
-r` sets the plural `RuntimeIdentifiers` restore hint and does not select the
RID-specific lock property. Restore on Linux can resolve the Windows graph;
it does not claim that cross-OS Native AOT compilation is supported. The scheme
uses the documented [NuGet lock-file properties](https://learn.microsoft.com/nuget/consume-packages/package-references-in-project-files#locking-dependencies).

`packaging/smoke.py` exercises the executable as a user: compile a typed function,
check/dry-run without writes, run the source, remove the source, then execute the
`.mso` and `.mexe` artifacts and compare exact UTF-8 payload bytes. A telemetry-enabled
artifact execution then verifies the normalized SQLite application/schema identity,
completed session and root/transfer records, exercising the packaged native library
rather than merely assuming file presence establishes loadability. The Windows
installer lifecycle uses this same journey after installation and repair. Its
existing Inno Setup AppId, per-user destination, PATH ownership and uninstall
preservation rules are unchanged.

## Contents and integrity

Only version-controlled documentation and standard-library sources are packaged;
untracked user notes and research artifacts are not selected by walking `docs/`.
The six standard-library modules (`fs`, `path`, `bool`, `string`, `ranges`,
and `process`) are required. The installed SDK's actual
`LICENSE.txt` and `ThirdPartyNotices.txt` are copied alongside product notices;
Rust attribution is not represented as if Rust were linked into the new product.
The bundled .NET notices may cover more SDK components than the native payload.
Published native `.so`, `.dll`, `.dylib` and versioned `.so.*` dependencies are
copied beside the executable with their relative layout; debug symbols are not
part of this payload. This includes SQLitePCLRaw's native SQLite library.
The installer installs that complete staged `bin/` payload, not just `msync.exe`.
Product-specific Tomlyn/Microsoft.Data.Sqlite/SQLitePCLRaw attribution accompanies
the SDK notices under `native-dependencies/`. The vendored PrettyPrompt editor
keeps its MPL-2.0 license and complete modified source in the installed
`native-dependencies/prettyprompt-6.0.5/source/` subtree; its optional ghost hint
is rendered by the existing prompt rather than a second terminal/input owner.
The generated invariant Unicode case tables derive from Unicode 17 data;
`vendor/unicode/LICENSE.txt` is shipped as `native-dependencies/Unicode-LICENSE.txt`
beside those notices. The runtime does not require the raw Unicode source files.

Each portable package includes `manifest.sha256`. Sorted archive entries and
fixed `SOURCE_DATE_EPOCH` timestamps make packaging repeatable for identical
payload bytes and the same compressor. The per-platform checksum sidecar is
supplementary: publication generates the authoritative `SHA256SUMS` over the
Windows installer, Windows ZIP, Linux archive and VSIX together. The existing
publisher verifies a matching source tag and resumable draft asset digests;
published tags and asset bytes are never replaced.

GitHub workflows perform native platform/package/editor-installation acceptance.
They do not run formatting or lint quality gates, and publication does not wait
for an unrelated CI workflow's check status. Package completeness and actual
installation journeys remain prerequisites within the release workflow itself.

## Focused local evidence

Container tests cover editor/product version agreement, deterministic ZIP/tar
bytes and checksum coverage. Existing publisher tests cover complete publication,
immutable tags/assets, draft resumption and failed uploads. Executable acceptance
must use the real Native AOT binary through `packaging/smoke.py`; container tests
are not a substitute for the user journey.

## Verified native package journey (2026-11-08)

`python packaging/build.py --rid linux-x64 --smoke` published the integrated
compiler/runtime/CLI with Native AOT without warnings and passed source, dry-run,
source-free relocatable/contextual bytecode execution and native SQLite telemetry
acceptance. The staged payload includes `libe_sqlite3.so` beside `msync`; the
archive is `dist/msync-0.8.0-linux-x86_64.tar.gz`. Extracting that archive and
verifying every manifest digest, all six standard-library modules and the same
real-process user journey also passed. This establishes Linux native
execution and distribution contents, not Windows installer acceptance; the
Windows native job remains the platform-specific verification path.

### Final frozen delivery image

The final locked Native AOT publication uses SDK 10.0.403 and invariant
globalization with the source-owned Unicode 17 case/order contract. The extracted
installed payload passed its complete manifest, six standard modules, native
SQLite load, Unicode attribution, source/dry-run/source-free artifact journey,
and exact baseline help/version delegation. Actual native stdio LSP navigation
into direct and aliased embedded standard definitions and persistent non-replaying
REPL/formatting/binary-stdout journeys also passed.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Linux Native AOT executable | 7,214,320 | `ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b` |
| Linux archive | 4,819,517 | `d7bd061f4b3440df2bdac5ce9ba7d345857b123008c799138646aa39f1842a0b` |
| VSIX 0.8.0 | 122,831 | `fdf2f987a7582a9c0b2b6f5b4fe8ed6f63fdf0dc43575af57c2c24ad51bfd182` |

The VSIX contains ten entries. Its manifest, bundled client, notices, grammar,
snippets, configuration and README were compared byte-for-byte with the accepted
production editor assets.

Six final actual OpenSSH source/`.mso`/`.mexe` pipeline journeys each observed
one native client, one authentication, one exec session and no scp. Compilation
and binding observed no clients. The changed-contract native language selection
passed all sixteen cases, including callback arity, nullable/structural function
flows, raw regex paths, generic type dispatch, complete Unicode and scalar-order
iteration. These are incremental final-image checks, not a repeated full matrix.
The corresponding maintained procedures are
`tests/Msync.Transfer.Tests/cli_ssh_journeys.py`,
`tests/Acceptance/language_journeys.py`, and `tests/Msync.EndToEnd/*.py`.

## Optional native WSL1 endpoint journey

`packaging/wsl_journey.py --binary NATIVE_EXE --output REPORT.json` owns one
randomly named imported distribution and its install directory below `.temp/`.
It follows Microsoft's [custom tar import interface](https://learn.microsoft.com/windows/wsl/use-custom-distro)
with an explicit `--version 1`; it never changes the host's default WSL version,
optional Windows features, or existing distributions. Cleanup unregisters only
the name assigned by that live fixture, including failed import attempts.

The fixture uses the official Alpine 3.22.2 x86-64 minirootfs with pinned SHA-256
`412454ed98025ad9cd910d13f3d448b184e81502baa83180e89f98d0f13674be`.
The [upstream checksum](https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.2-x86_64.tar.gz.sha256)
identifies the immutable versioned download, rather than an unversioned latest
rootfs. The owned distro installs actual GNU tar, findutils and coreutils; the
transport's `mv -T`, `chmod --reference`, `find -printf`, and related GNU options
are not assumed to work through BusyBox substitutes. Exact installed tool
versions and command diagnostics are retained in the report.

The native executable checks, compiles and binds while the owned distro is
terminated, requiring it to remain absent from the running-distribution list
and leaving the remote payload absent. Real selected executions then upload and
download through that named WSL endpoint using source, `.mso` and `.mexe`.
Windows local home and Linux remote home differ. Captured `~/.codex/config.toml`
and relative paths are qualified before/after joins, compared byte-for-byte,
and checked for preserved sibling data. The `.mso` runs from a second cwd;
the `.mexe` retains the first local cwd/home while the native WSL client is
launched from the second cwd. No fake WSL launcher is involved.

A host unable to import or start WSL1 produces an explicit `unavailable` report
with its native diagnostics, not a passing endpoint result. Setup failures and
product failures are separate outcomes. Linux can validate the script and source
fixture, but actual WSL behavior is established only by the native Windows run.

### First actual WSL1 execution

The [native Windows job in run 42471357057](https://github.com/kleedaisuki/msync/actions/runs/42471357057/job/128702629674)
executed the fixture on source `3a1275d`. Native binary SHA-256 was
`2cb89065e098c87465d6c8fc23086a1610122b2398492249859ba06b2961938a`.
Import and boot succeeded as WSL1 with Linux HOME `/root`. The distro installed
GNU tar 1.35, findutils 4.10.0 and coreutils 9.7. Check, compilation and binding
completed successfully while the owned distro remained stopped; the remote
payload remained absent before selected execution. The fixture's own native
`sh -c` setup and inspection commands succeeded.

The first actual product transfer failed with `sh: set: line 0: illegal option -`
followed by a carriage return. Uploads exited 2 and download readers then reached
EOF. This is a transport shell-code failure, not a missing WSL/GNU prerequisite:
Windows checkout CRLF survives inside C# raw multiline shell literals, so POSIX
`set -eu` received a trailing CR. The product's shell-code construction boundary
must canonicalize its own program text to LF without changing pathname, payload
or process-argument data. WSL transfer success is not claimed for this image.

Owned distribution unregistration succeeded with exit 0. The host default WSL
version remained 2, and no unrelated distro or Windows feature was modified.
The retained report is the job artifact's `.temp/platform-results/wsl.json`;
the full command list and failure chain survive normal temporary workspace
cleanup through this summary and the maintained fixture.

### Controlled stream and completion experiments

Subsequent diagnostic-only Windows runs reused the exact corrected-LF product
image `40d37d193439bde5263bbc6b0e152c0aed89b0efee736e400a41f7fc04bf0e79`
without rebuilding it. Native stat and byte captures proved that uploads had
installed both intended files correctly. Python PIPE captures returned valid
GNU/PAX archives, while the product failed inside the .NET PAX extended-header
and block-padding reads. Default and `--jobs 1` executions failed alike.

The standalone Native AOT process probe then isolated the relay boundary:
ordinary C# bulk capture obtained 10,240 bytes for the small files, but live
512-byte header reads obtained only 512 bytes before EOF despite native exit 0.
The same native executable replaying the archive through a generic Windows pipe
did not lose bytes. A 64 KiB `BufferedStream` repaired the small files but still
truncated a continuous binary source, so buffering alone was rejected as a
complete remedy.

The [acknowledged-completion experiment, run 42477767279](https://github.com/kleedaisuki/msync/actions/runs/42477767279)
kept the guest helper alive on its owned input pipe until the receiver had
consumed logical tar completion. Explicit LF protocol tokens were independent
of Windows text-line endings. Unbuffered and buffered ACK variants both returned
the complete 10,240-byte small archives and complete 1,290,240-byte large archive,
with native exit 0 and successful live/offline parsing. The large member was
exactly 1,280,123 bytes spanning all byte values, matching host and native SHA-256
`7a16c6063e0e23004a13995dd6dc3c462a605f53afd2835e5e9b789549c349d2`.
Generic native-self controls replayed this valid large archive without loss.

This establishes an effective completion/lifetime mechanism, not an upstream
kernel attribution: requiring receiver completion before helper teardown avoids
the observed WSL relay truncation without whole-archive spooling or an assumed
maximum file size. It is a transient flow-control acknowledgement, not recovery,
rollback, or an added authentication. Product source/`.mso`/`.mexe` acceptance
must still run after integrating that mechanism; probe success is not relabeled
as an already passing old product image. Maintained reproduction is
`tests/IO/WslTarProbe` plus the fixture's failure-only native/raw diagnostics.

### Production WSL acceptance after completion ownership

The [actual Windows production job 42480251334](https://github.com/kleedaisuki/msync/actions/runs/42480251334/job/128730444211)
passed with source `2d35e42`, incorporating the production acknowledgement and
declared-member-length guard from `8ca36bc`. The newly published Native AOT
executable SHA-256 is
`0f62a046456f64034619cb1bba9ce813e0b4ff774fd338d0ae558592d27ec135`.
This was a new product build, not the old failing image reused by the causal labs.

| Execution | Process cwd | Captured local cwd/home | Result |
| --- | --- | --- | --- |
| Source | First fixture context | First | Exact round trips |
| Relocatable `.mso` | Second fixture context | Second | Exact round trips |
| Bound `.mexe` | Second fixture context | First | Exact round trips |

Every stage transferred the ordinary home/relative files in both directions,
verified qualification before/after binding and joins, transferred the full
1,280,123-byte all-byte binary oracle and an empty file, and retained unrelated
sibling bytes. Windows local home and the imported Linux HOME `/root` remained
distinct. Static check/compile/bind kept the owned distro stopped and did not
publish the remote payloads.

Two deterministic interruption controls also passed. GNU tar's eighth-record
checkpoint and owned FIFO rendezvous stopped the producer while the Windows
receiver had already written **70,144 staged bytes**. Actual ConPTY Ctrl-C and a
separate termination of only the owned WSL distro both preserved the previous
ordinary target and retired the live file stage. The Ctrl-C case surfaced as a
guest interruption and short-body transfer failure (CLI exit 1), not an asserted
managed-token exit-130 path. Both failures retained their actual declared and
received byte counts instead of publishing an incomplete member.

Owned distribution unregistration returned 0. The job completed successfully in
1 minute 24 seconds. Its retained `.temp/platform-results/wsl.json`, per-stage
traces and `diagnostics/controls` results distinguish this production acceptance
from the earlier failed images and mechanism-only probes. No further full
platform/performance matrix was required for this focused completion change.

## Native Windows execution and fixture lifetime

The first actual Windows run used Windows Server 2025, runner image
`windows-2025-vs2026` version `20261103.298.1`, SDK 10.0.403 and Python 3.14.10.
At source revision `364de75`, [the native job](https://github.com/kleedaisuki/msync/actions/runs/42465854974/job/128685099644)
successfully compiled Native AOT and completed source, dry-run, source-free
`.mso`/`.mexe` byte comparisons, SQLite telemetry and baseline delegation.
The subsequent fixture cleanup failed with Win32 sharing violation 32 on
`telemetry.sqlite`, before installer construction or lifecycle execution.

The owning Python inspection used `with sqlite3.connect(...)`; that context
manages transactions, not the connection lifetime. The standard
[SQLite context-manager contract](https://docs.python.org/3/library/sqlite3.html#how-to-use-the-connection-context-manager)
requires explicit close or `contextlib.closing` for this use. `45e20fe` closes
the inspection before its owned directory is deleted. The fix adds no retries,
does not ignore failed cleanup, and changes no product I/O semantics. The same
revision's Ubuntu 22.04 native package job passed. The corrected Windows job
then reached the portable and installer lifecycle recorded below.

### Corrected native Windows installation and public upgrade

At `51689bc`, [the actual package workflow](https://github.com/kleedaisuki/msync/actions/runs/42467144102)
passed both native platform package jobs and the installed-editor job. The
[Windows job](https://github.com/kleedaisuki/msync/actions/runs/42467144102/job/128689195077)
used `windows-2025-vs2026` image `20261103.298.1` and passed:

- Native AOT publication, portable manifest digests, all six embedded/source
  standard modules, source/dry-run/source-free bytecode and native SQLite.
- Per-user installation, Start Menu target/arguments, and actual command
  resolution after refreshing the terminal PATH from Machine/User settings.
- Repair/upgrade without duplicate PATH entries; missing PATH and existing
  `REG_SZ`/`REG_EXPAND_SZ` ownership; declined PATH task and preexisting user-owned
  equivalent entries.
- Uninstallation restoring the exact unrelated PATH text/type while retaining
  user configuration, data, and a user-added helper in `bin/`.
- A real upgrade from the immutable public 0.4.0 installer to current 0.8.0,
  including installed `fn`/bytecode operation and subsequent uninstall retention.

The public baseline retained SHA-256
`ed22547657e0981cb282180d72342f3e6cf95ea3ceada33993b8dec9e5e03498`.
The earlier direct asset URL returned an HTTP 504 before installation; using
`gh release download` resolved that external fetch while retaining the digest
check. No published asset or tag was modified. The Ubuntu 22.04 package job
passed independently, and the editor job exercised the downloaded installed
Linux archive rather than a separately rebuilt server. Publication was skipped
on the development branch.


## Cross-block memory and compiled-artifact delivery (2026-11-09)

The current unpublished 0.8.0 payload was built from
`ff265450b7180e0414cfb1f9675c7337761daa00` in
[Native run 42582869154](https://github.com/kleedaisuki/msync/actions/runs/42582869154).
The Linux job `129045576609` passed all selected new contracts. Windows job
`129045576853` passed memory/layout, nullable callables, the user's exact Scope
CLI/LSP sources, global imports, disassembly, four managed memory projects and
native component observations. Its protocol fixture completed its assertions
then failed printing Unicode JSON to CP1252. Fixture-only `48f46ad` preserved the
Unicode inputs and UTF-8 report, emitting escaped JSON on stdout instead.
[Windows protocol-only run 42583702434](https://github.com/kleedaisuki/msync/actions/runs/42583702434)
then passed against the exact original Windows native payload; no AOT build or
already accepted contracts were repeated.

| Image | Executable SHA-256 |
| --- | --- |
| Linux | `ca27463e899595a838b27861ef1328ac2f33b413b0d628ae00e58aaf60741d46` |
| Windows | `c10d05c8c5ccf682d1a34002b5758da4c3ea0b6fbc9e24359d9347ea6174ced9` |

Both package manifests verify all 226 entries. All 59 modified PrettyPrompt
source files are present; five Windows text files have the exact Git-checkout
CRLF transformation while package digests cover their actual bytes.

The final real VS Code 1.144 artifact-only host used the exact Linux image above,
not a Debug build or PATH fallback. Initial host `C91uM3` exposed a genuine initial
VS Code watch-registration race. UI `fdb29c3` uses synchronously registered,
shared parent-directory watchers for file URIs and retains provider watching for
other schemes. Independent host `R5GyqX` passed first atomic recompilation,
automatic regular-write corruption/repair, read-only views, symbols/navigation,
restart, inert debug data and byte preservation. Test-only `81ad4ef` requires these
automatic transitions; no manual refresh or longer timeout masks the defect.
Native program bytes were unchanged by this UI repair.

The tested bundle is packaged as `dist/msync-language-0.8.0.vsix`. Its nine
payload files, including both TextMate grammars, manifest, bundled language
client/viewer and notices, match the current sources/build bytes. All four
current delivery assets are covered by `dist/SHA256SUMS`; exact package hashes and
sizes are recorded in `.temp/final-memory-artifact-delivery-v080.json`.
Same-version replacement must update both Native AOT and VSIX, reload the window,
and restart the intended absolute `msync.serverPath`; see
[the deployment instructions](../releases/0.8.0.md#replacing-a-same-version-development-build).
No public release, tag or asset was changed. Performance evidence is retained
separately in `performance-v080.md` rather than inferred from acceptance counts.


### Follow-up evaluated-callee correction

Independent runtime experiments exposed an existing optimized-source alias
proof error in the preceding native candidate: evaluating `action(change())`
correctly snapshots callable A, but the argument changes its shared binding to
B and the following `action(false)` must call B. Source O0 and independent typed
IR/codec controls produced AB; optimized candidate `ff26545` produced AA.
`5e66232` invalidates future local callable facts through captured aliases while
retaining the already evaluated operand-stack callable. Frontend and runtime
ABIs are unchanged.

The owner accepted 36 real source/`.mso`/`.mexe` executions across O0/O1/O2/O3 and
four REPL results in `a18ec6c`. The targeted native entry is
`tests/Msync.EndToEnd/call_order_journey.py --binary PATH`. Only this changed
contract and new payload construction are selected for the corrective native
pair; accepted Scope/global/artifact/UI evidence is retained. The final native
hashes must identify the corrected image rather than relabel the previous one.


### Accepted corrected and scheduler images

[Corrected native run 42592564053](https://github.com/kleedaisuki/msync/actions/runs/42592564053)
passed both operating systems from `ec3754544350fb82f43433dc34f0bfe48fc526a4`.
It selected only the 36 callee-order byte executions, four REPL results and
payload construction. The accepted initial memory/global/editor contracts were
not rerun. Corrected executable hashes were Linux
`93e6b0b405afbef5c21c099483c15049d66cd460b6f7fa45a51abf3add450e9d`
and Windows `1087acc1ccbd518a6ff57c2f74caf57ddf4329a4553e07d2ecfbb0e72f4442aa`.

The final scheduler delta `c0b21ab` was then accepted separately in
[run 42595248446](https://github.com/kleedaisuki/msync/actions/runs/42595248446),
source `a8c71875e5f7bd2b5a628a8bcdece3f8b72ec4d1`. Both native jobs passed the
14 selected source/bound process gates and the real managed retirement,
late-lease, fault/retry and namespace tests. Relative to the corrected image,
production changed only `Interpreter.Scheduling.cs`; no adaptive-runtime research
prototype was adopted. The formal native performance record is separate in
`scheduler-runtime-v080.md`.

| Final executable | SHA-256 |
| --- | --- |
| Linux | `f41a6731d02802065b9f280d73c1941f5b62f4ccdbc0a48ff2a7e06d79ce8b88` |
| Windows | `3fd63f24e705b67ff1a1ae6af4a10cdff4a7f799dff3467a7159bdbe205535dc` |

The final same-image VS Code 1.144 artifact host `cqv5mr` used that exact Linux
path and SHA, with no managed assemblies. Read-only object/executable views,
21 symbols per stage, hover/definition, automatic refresh/corruption recovery,
restart, normal source editing and inert metadata all passed. The tested UI and
VSIX remain `fdb29c3` / `2a71e1e6185489feeed8b109d90d4901d46c742f260a88453a57a10f413c3b70`.

The final package audit verifies 232 entries per platform and all 59 modified
prompt sources. Four current delivery assets and their actual digests are in
`dist/SHA256SUMS`; `.temp/final-memory-artifact-delivery-v080.json` records all
sources, prior/corrected/final image identities and exact paths. This closes the
memory/artifact/global/correctness/scheduler delivery; a subsequently requested
standard-library expansion is a separate coherent source slice.
