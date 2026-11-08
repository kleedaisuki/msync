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

`packaging/smoke.py` exercises the executable as a user: compile a typed function,
check/dry-run without writes, run the source, remove the source, then execute the
`.mso` and `.mexe` artifacts and compare exact UTF-8 payload bytes. The Windows
installer lifecycle uses this same journey after installation and repair. Its
existing Inno Setup AppId, per-user destination, PATH ownership and uninstall
preservation rules are unchanged.

## Contents and integrity

Only version-controlled documentation and standard-library sources are packaged;
untracked user notes and research artifacts are not selected by walking `docs/`.
The five standard-library modules are required. The installed SDK's actual
`LICENSE.txt` and `ThirdPartyNotices.txt` are copied alongside product notices;
Rust attribution is not represented as if Rust were linked into the new product.
The bundled .NET notices may cover more SDK components than the native payload.
Published native `.so`, `.dll`, `.dylib` and versioned `.so.*` dependencies are
copied beside the executable with their relative layout; debug symbols are not
part of this payload. This includes SQLitePCLRaw's native SQLite library.
The installer installs that complete staged `bin/` payload, not just `msync.exe`.
Product-specific Tomlyn/Microsoft.Data.Sqlite/SQLitePCLRaw attribution accompanies
the SDK notices under `native-dependencies/`.

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
