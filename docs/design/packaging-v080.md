# Native AOT packaging in v0.8

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
the SDK notices under `native-dependencies/`.
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
