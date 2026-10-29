# v0.5 performance: barrier interpreter and recoverable statements

## Status and workload

This is the performance workstream for the breaking v0.5 model. Correctness
integration is still in progress. **No v0.5 release measurement or production
optimization is accepted here yet.** Do not substitute historical measurements
for a new executable hash.

The main workload is the user's macro invoked twice: local `config.toml` and
`agents/.`, then the same two operations with a WSL source. The maintained
`benches/macro_latency_v4.ps1` runner uses exclusively owned data, not the user's
personal `.codex`. It validates 28 output files, 126,083 bytes in the mixed case,
exact contents and removal of a seeded stale agent file. A two-local-source
control has 126,082 bytes. Neither statement order nor payload size is reduced
to obtain a better number.

The runner's historical filename is retained to reuse the same experiment. It
now additionally supports:

- `-RequirePureStatic`: the candidate's check/compile/plan traces must contain no
  prepare, execution, remote inspection, transport or publication spans. The old
  reference is not required to meet this new contract.
- `-DestinationSiblingFiles N`: place N owned unrelated files directly in each
  output parent, retain them across publication, and validate their bytes and
  mtime. These controls are not counted as copied payload.
- `-DestinationState missing|existing`: separate the first invocation's missing
  output roots from repeated overwrites. The default retains the original seeded
  existing-target fixture. Missing-target mode cannot also seed sibling controls.
- A hard `stat -f` magic `ef53` guard for the WSL source and recorded mount data.
  Windows storage is reported from `DriveInfo`, not assumed from the drive letter.
- Start/end executable hash consistency and explicit transport-process counts.

Parser validation and a no-msync helper self-test passed: a synthetic 28-file
copy preserves sibling controls, and intentional control mutation is rejected.
That is runner validation, not compiler/runtime acceptance or a latency result.

## Reusable baselines

The following immutable files were SHA-256 checked during preparation:

| Image | SHA-256 | Role |
| --- | --- | --- |
| `target/performance-v4/baselines/msync-user-macro-4d54eff-windows.exe` | `190b8c5cba8e563a9cf0e352b77df851795685a96f3a13c9b097c4cd16c4194d` | Accepted v0.4.0 historical user-journey reference |
| `target/performance-v4/baselines/msync-user-macro-95df1e5-windows.exe` | `083af37ee8b87cd29ce19ce1b8f5eb72aaa3ad33472cf125e0c5596bde1dc6d1` | Intermediate v0.4.1 investigation only, not v0.5 acceptance |

[performance-v4.md](performance-v4.md#five-pair-revised-path-comparison) records
five alternating pairs: mixed compile 267.33 to 22.91 ms and mixed run 3339.48 to
2015.72 ms for those historical images. Helper counts fell from 16 to four. A
guest syscall probe placed much of a delayed config import outside the guest
copy itself. The inherited-cwd hypothesis was tested and rejected; do not repeat
that experiment or add `--cd /` on the strength of whole-command noise.

## Ordered experiment, after the correctness gate

1. Freeze the selected **release** image under `target/performance-v5/baselines/`
   with a hash-derived name. Record its version, complete source revision and
   build flags. Do not time `target/debug` or a path another build can overwrite.
2. Run one functional local/mixed control with the exact macro. Confirm the
   native-ext4 source guard, selected file bytes/counts and no stale agent child.
3. Compare five alternating pairs using identical warm WSL state, default
   resource limits and tracing on both arms. Use the same filesystem/volume and
   source lifetime; setup, seeding, validation and cleanup remain outside timing.
4. If a worthwhile optimization is implemented, freeze **both v0.5 images** and
   repeat the same comparison. The v0.4 reference measures the overall user
   journey change; it cannot isolate the benefit of a later v0.5 optimization.

Example commands, where `CANDIDATE` is the frozen selected release:

```powershell
./benches/macro_latency_v4.ps1 -Executable CANDIDATE -Samples 1 -Commands compile,plan,run -RequirePureStatic
./benches/macro_latency_v4.ps1 -Executable target/performance-v4/baselines/msync-user-macro-4d54eff-windows.exe -Candidate CANDIDATE -Samples 5 -Commands compile,plan,run -RequirePureStatic
```

Report complete-command p50 and each paired difference, helper counts and actual
publication phases. Five samples do not establish a tail-latency distribution;
the runner's nearest-rank p95 is effectively its observed maximum. Nested spans
overlap and must not be summed as independent wall-clock costs. New durability
work must be explained separately from eliminated transaction/MVCC work.

## Discriminating probes and adoption criteria

### First probe: did eager block observation reintroduce WSL launches?

The old intermediate ordinary owner used four helper processes for the two WSL
sources. The new interpreter calls `preparation::prepare_block` with live
observation. Source inspection shows two distinct possible additional paths:

- `Observations::complete` calls `runtime::inspect_locations_live` for queued
  remote endpoints, producing an `inspect_remote_batch` span.
- Earlier local writes in this macro can make a later WSL source initially
  alias-unknown. `Observations::refine_wsl` then calls
  `runtime::wsl_native_file` separately per source to establish native-ext4 versus
  native-Windows separation. That helper is also a real transport invocation,
  even when no `inspect_remote_batch` appears.

These are inspected call paths, **not a measured count**. The frozen release may
therefore show more than four processes through either route. The runner records
total transports, WSL transports, explicit metadata batches and guarded private
acquisitions separately. In particular, zero metadata-batch spans does not prove
zero extra probes. Check this before choosing the parent-enumeration experiment
or a more elaborate batched-copy design.

An offline or capture-time-only planning policy could retain Unknown kinds and
conservative dependency edges, then establish the necessary exact source facts
at acquisition. Adopting that policy still requires proving that source ordering,
stream/FIFO classification and alias hazards remain correct; a cheap fast path
must not silently bypass correctness or turn a finite source into an untracked
stream. Benchmark and gate the actual proposed policy before claiming a win.

| Candidate | Inexpensive discriminator | Required proof / reason not to adopt blindly |
| --- | --- | --- |
| Destination-parent enumeration in `Batch::prepare_inner` | Same frozen v0.5 image, local-only run, 0 versus 1,000 destination siblings; first one sample per arm, then repeat only if material | Existing scan finds coordinators even from another configured scratch root. A cached parent identity or metadata observation does not prove no new journal appeared. Keep recovery correctness; do not merely delete the scan. |
| Group ready WSL finite reads | Actual new release helper counts and transport spans for the two WSL source nodes | Both reads must already be dependency-ready in the current block. Batch preflight/name validation, private ownership, source fingerprints, cancellation draining and per-statement publication remain required. Failed group guards must fall back before effects, never replay completed statements. |
| File IO engine replacement | Only if new traces identify payload movement as dominant | The workload is about 126 KB. Previous evidence did not justify a ring engine; do not optimize guest copy loops to explain a host-launch delay. |

The sibling probe differs deliberately from the previous 1,000-file cwd control:
the latter did not put entries in the directories actually enumerated by durable
publication. The new control preserves unrelated file contents and timestamps
instead of silently omitting them from validation.

```powershell
./benches/macro_latency_v4.ps1 -Executable CANDIDATE -Endpoint local -Samples 1 -Commands run
./benches/macro_latency_v4.ps1 -Executable CANDIDATE -Endpoint local -Samples 1 -Commands run -DestinationSiblingFiles 1000
```

## Platform and persistence references

- [Microsoft WSL filesystem guidance](https://learn.microsoft.com/en-us/windows/wsl/interop): native Linux storage and mounted Windows storage are different workloads. Build-source location is not evidence about timed endpoint storage.
- [SQLite atomic commit](https://www.sqlite.org/atomiccommit.html): persistence ordering is part of the recovery protocol. Removing flushes is a contract change, not a free latency improvement.
- [Linux filesystem persistence background](https://www.kernel.org/doc/mirror/ols2009.pdf): an atomic namespace replacement alone does not establish durable namespace state. Preserve the selected storage contract while measuring its cost.

Actual v0.5 measurements, accepted optimization and verification belong below
when the release and comparison gates have executed; they are currently pending.

## Candidate: one namespace operation as the statement decision

The storage team and performance workstream agree on this **unimplemented,
unmeasured candidate**, gated on the cross-platform correctness gate and a frozen
pre-optimization release:

| Publication shape | Candidate path |
| --- | --- |
| One existing regular-file root replaced by one regular-file root | Fully registered, materialized and synced private stage; one native same-filesystem atomic replace is the irreversible decision. |
| One absent finite root, including a promoted missing directory ancestor | Same ownership/staging protocol; one kernel no-replace rename is the decision. Do not replace an unexpected concurrent destination. |
| Existing directory, multiple publication roots or unsupported native primitive | Keep the recoverable Batch protocol. |

This removes the before-image, coordinator intent/decision and parent-wide
coordinator scan only where a **single operation already supplies the whole
statement decision**. Stage recovery deletes only the identity-verified private
workspace, never the logical target. It must work whether the stage was still
private at the crash or had already been renamed into place.

Choose this strategy after semantic assignment validation and complete target
projection. The decision uses the final publication root count and old/new kinds,
not a growing whitelist of `Ready.mode`, selector syntax or container spellings.
Parent promotion becomes an ordinary absent-directory root. A selector with
multiple final roots remains a batch. Existing nonempty-directory exchange is
not required for this first, Windows-dominant optimization round.

Cancellation before rename leaves the old state. After rename succeeds, no
cancel, cleanup error or later sync error may trigger `Publication::rollback`.
A post-decision durability error must explicitly distinguish installed data from
unconfirmed persistence; it cannot claim no execution or successful rollback.
The implementation should express this consumed/irreversible receipt in types,
not an unchecked boolean that allows a generic compensation path to run.

Reuse mature platform primitives rather than adding an unrelated replacement
engine, but qualify the **actual syscall path**, not the name of a wrapper.
`filesystem::move_new` can temporarily chmod a readonly directory around its
rename, and `io::publish_file(no_clobber=true)` can fall back from unsupported
Linux `renameat2` to hard-link creation followed by private-stage unlink. The
unlink can fail after the target was already installed. Neither helper can be
treated as an opaque single-decision operation whose every `Err` means no effects.
Use a direct supported native no-replace/replace operation, or a typed outcome
that retains the exact irreversible decision. An unsupported primitive or
pre-decision access denial can fall back to Batch only when no target effect
has occurred.

All desired metadata must be installed on the private root before the atomic
operation. In particular, never chmod a readonly directory to permit movement,
publish it and then restore its mode publicly without a recovery protocol: a
crash would expose complete new bytes with incorrect metadata. Existing
recoverable Batch remains the fallback for such a platform restriction.

Rust documents the platform mapping of
[`fs::rename`](https://doc.rust-lang.org/std/fs/fn.rename.html). In particular,
do not casually switch to
[`ReplaceFileW`](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew),
whose documented error outcomes include partially moved names. Filesystem and
network error bounds still require the selected supported-platform contract.

Measure cold and existing destinations separately. With missing `local` and
`wsl` directories, the macro's config copy publishes a promoted directory root,
not a regular-file root; the following agents target is also absent. Restricting
the candidate to existing files would therefore miss the cold user journey.

```powershell
./benches/macro_latency_v4.ps1 -Executable BEFORE_V05 -Candidate AFTER_V05 -Samples 5 -Commands run -DestinationState existing
./benches/macro_latency_v4.ps1 -Executable BEFORE_V05 -Candidate AFTER_V05 -Samples 5 -Commands run -DestinationState missing
```

Required tests cover pre/post-rename process termination, old/new target bytes and
portable metadata, no private payload after next startup, missing ancestor
promotion, foreign target arrival/no-clobber, cancellation on both sides of the
decision, and post-decision cleanup/flush error classification. Existing
multi-root and directory crash gates must remain unchanged. Include a real Unix
**non-root** case publishing a readonly directory into a previously absent root
across parents: either the native operation installs the correct final mode in
one decision, or the no-effects fallback uses the recoverable protocol. A test
running only as namespace root must not mask this permission boundary. No measurement or
implementation acceptance is implied by this design note.

## Release-freeze handoff (prepared, not executed)

Run the following only after the coordinator releases the correctness gate and
serial Cargo slot. The managed `target` directory is an artifact location, not a
temporary external workspace. Preserve the complete source state, including new
untracked Rust files; a Git HEAD alone does not identify this integration image.

```powershell
$env:CARGO_BUILD_JOBS = '2'
$env:CARGO_INCREMENTAL = '0'
cargo build --locked --release --bin msync
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
$hash = (Get-FileHash -LiteralPath target/release/msync.exe -Algorithm SHA256).Hash.ToLowerInvariant()
$root = Join-Path (Get-Location) 'target/performance-v5/baselines'
[void](New-Item -ItemType Directory -Force -Path $root)
$frozen = Join-Path $root "msync-v05-before-$hash.exe"
Copy-Item -LiteralPath target/release/msync.exe -Destination $frozen
if ((Get-FileHash -LiteralPath $frozen).Hash.ToLowerInvariant() -ne $hash) { throw 'Freeze hash mismatch' }
$files = @(git ls-files --cached --others --exclude-standard | Sort-Object -Unique)
$manifest = foreach ($file in $files) {
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        [pscustomobject]@{ path=$file; sha256=(Get-FileHash -LiteralPath $file).Hash }
    } else {
        [pscustomobject]@{ path=$file; deleted=$true }
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$frozen.source-files.json" -Encoding utf8
[pscustomobject]@{
    head=(git rev-parse HEAD); status=@(git status --short); image_sha256=$hash
    rustc=(rustc -Vv); flags='cargo build --locked --release --bin msync; CARGO_BUILD_JOBS=2; CARGO_INCREMENTAL=0'
    source_manifest_sha256=(Get-FileHash -LiteralPath "$frozen.source-files.json").Hash
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$frozen.provenance.json" -Encoding utf8
& $frozen --version
./benches/macro_latency_v4.ps1 -Executable $frozen -Samples 1 -Commands compile,plan,run -RequirePureStatic
```

The source tree must remain frozen between the source manifest and build; record
that coordination boundary rather than implying the manifest can repair a
concurrent edit. A passing control authorizes the first five-pair historical
comparison, not a production optimization on its own:

```powershell
./benches/macro_latency_v4.ps1 -Executable target/performance-v4/baselines/msync-user-macro-4d54eff-windows.exe -Candidate $frozen -Samples 5 -Commands compile,plan,run -RequirePureStatic
```

Inspect the mixed-run transport counts and phase traces first. If no additional
WSL probe dominates, use the same frozen image for the local 0/1000-sibling
comparison above. After the chosen change passes its correctness gates, build a
second hash-named image with the same flags/provenance capture and compare the
**two v0.5 images**, five pairs each for existing and missing destinations. Do not
reuse the historical comparison as causal evidence for the later optimization.

### Native-operation review at this handoff

Source review confirms `Storage::publish_checked` currently routes every
nonempty final publication set through `durable::Batch::prepare_registered`.
Thus the proposed one-root strategy can remove real coordinator work rather
than merely shortening a name. Its end-to-end benefit remains unmeasured.

The mature native interfaces support the intended split, but not a universal
fallback wrapper:

- Linux [`renameat2(RENAME_NOREPLACE)`](https://man7.org/linux/man-pages/man2/rename.2.html)
  requires underlying filesystem support. Unsupported-primitive fallback is
  safe only before any destination change. The same manual explicitly requires
  write access to a moved directory when updating its parent link, so retain
  the real non-root readonly-directory gate.
- Rust's [`fs::rename`](https://doc.rust-lang.org/std/fs/fn.rename.html) maps to
  Unix rename and Windows MoveFileExW with a handle-based fallback; this is a
  replace operation, not no-clobber admission.
- Windows [`MoveFileExW`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)
  can simulate cross-volume movement as copy/delete when `MOVEFILE_COPY_ALLOWED`
  is supplied. Do not enable that flag for a one-decision primitive. Its
  `MOVEFILE_WRITE_THROUGH` documentation specifically describes copy/delete
  flushing; it is not evidence for a universal hard-power-loss guarantee for
  ordinary same-volume renames.

Keep desired metadata private until the single decision, consume the prepared
receipt into a committed receipt, and retain identity-verified Stage cleanup.
Never call generic compensation after a committed result or treat a subsequent
cleanup/flush failure as permission to retry the statement.
