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
continues with the actual portable and installer lifecycle rather than treating
the initial product-only success as completed installation acceptance.
