# v0.5 performance: barrier interpreter and recoverable statements

## Status and workload

This is the performance workstream for the breaking v0.5 model. The final section records an accepted, measured local-only block-observation optimization, including its small measured local-command trade-off. Earlier sections remain chronological evidence, not final measurements. Final release CI/installation acceptance is separate; do not substitute historical measurements for the selected executable hash.

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

The first actual v0.5 baseline and its comparison are recorded below. An accepted
production optimization and its causal before/after comparison remain pending.

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

## Release-freeze procedure (first baseline executed below)

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

## Frozen pre-optimization v0.5 measurement

The coordinator authorized baseline measurement after the local correctness gate;
full-source remote CI was still running, so no production optimization was
implemented during these measurements. Frozen image:
`target/performance-v5/baselines/msync-v05-before-23f73b1a4a537c3ee012a646581d50edde2ea5c0e0406e40507224aaf346b9bd.exe`.
SHA-256 is the complete filename suffix. Adjacent provenance records source HEAD
`73402e4` and a complete source-file manifest; both image hashes remained
unchanged across the runner. Release build took 72 seconds with the flags above.

The functional control passed for both local and mixed endpoints. The timed
Windows fixture was **NTFS on D:**; the WSL source was independently copied to
an owned `/tmp/msync-macro-latency.*` directory and required `stat -f` magic
`ef53` before any measured invocation. This is not a benchmark of Linux reads
from `/mnt/d`. Build source and the initial untimed fixture seed may use DrvFS.
Every mixed run verified exactly **28 files, 126,083 bytes**, including removal
of the old agents child; local runs verified 126,082 bytes. Static commands had
zero runtime/preparation/transport/publication spans on the new image.

Five alternating pairs against historical v0.4.0 SHA `190b8c5c...4194d`:

| Case | Historical p50 ms | v0.5 baseline p50 ms | Interpretation |
| --- | ---: | ---: | --- |
| Local compile | 30.252 | 21.777 | Complete command, not compiler-only time |
| Local plan | 27.603 | 26.228 | Small difference at this sample size |
| Local run | 374.046 | 280.658 | All five paired deltas favor v0.5 |
| Mixed compile | 267.856 | 23.380 | Remote probes removed from static phase |
| Mixed plan | 310.448 | 27.126 | Remote probes removed from static phase |
| Mixed run | 3187.960 | 3327.802 | **No speedup:** p50 is 4.39% slower |

Mixed-run paired deltas (new minus old, ms) were `+494.00, -173.68, +23.38,
-17.90, +139.84`. This is noisy and not evidence that a precise 4.39% regression
will persist, but it decisively prevents claiming a measured end-to-end run win.
Five samples are not a tail-latency characterization.

The new image used exactly **six WSL helpers** in every mixed run; historical
v0.4 used sixteen. Mixed static commands used zero rather than two. Of the six
new runtime helpers, two are parented directly under `execute`, before private
acquisition, and four under the two `ordinary.wsl.acquire` spans. The two extra
probes together cost 210.7–315.2 ms per run. Source inspection identifies these
as `Observations::refine_wsl` calls; `inspect_remote_batch` count is zero and
therefore would miss them. Mean nested phase totals were acquisition 2446.6 ms,
staging 197.3 ms and publication 58.6 ms; these are not additive independent
wall times. The per-source config capture still varies around 1.6–2.2 seconds,
so removing two probes cannot explain or eliminate the whole mixed-run delay.

Artifacts, retained under managed `target/performance-v5`:

- `control-before.log`, `control-raw/`
- `historical-pairs.log`, `historical-raw/`
- `historical-paired-deltas.json`
- `baseline-helper-breakdown.json`

### First measured optimization hypothesis

Prefer removing redundant remote lookahead before the native single-root
publication optimization: two measured probes cost substantially more than the
entire current publication phase, while acquisition already performs the exact
finite/type/alias guards before effects. Keep local observation and actual
native FIFO generation proofs. The existing preparation `live=false` path still
calls local metadata inspection and establishes local existing-parent frontiers;
it only suppresses proactive WSL refinement/remote metadata helpers and keeps
those endpoints `Unknown`. This is a concrete bounded policy, not disabling all
runtime observation or caching previous-block filesystem truth.

Before adoption, tests must show remote/local aliases retain conservative source
ordering, remote special files fail before payload open, file creation across
blocks remains fresh, and local FIFO/socket co-admission is unchanged. Then
compare a newly frozen v0.5 image against this v0.5 baseline on the exact macro;
the historical comparison above cannot attribute a later optimization's gain.

## Directional-barrier pre-observation baseline control

After the directional-barrier semantic changes, the coordinator froze a new
pre-optimization image rather than attributing changed semantics to the earlier
performance hypothesis:
`target/performance-v5/baselines/msync-v05-before-observation-ba42e88c35a6c9dafdd985879be94d5894167f924fe896250b2b3cf74497d907.exe`.
Its adjacent provenance identifies the exact clean source and build flags. A
single local/mixed functional control with `compile,plan,run` and
`-RequirePureStatic` passed; the native-ext4 guard, NTFS destination, exact copied
bytes/counts, stale-child removal and unchanged executable hashes all passed.

The control observed local run 356.2 ms, mixed run 3944.9 ms and six mixed-run
WSL helpers. The coordinator was concurrently performing correctness validation;
these single values establish successful baseline execution, **not a performance
comparison or a regression estimate**. Raw evidence is retained in
`target/performance-v5/control-before-observation.log` and
`target/performance-v5/control-before-observation-raw/`. No production optimization
was implemented while full-source CI remained pending.

The proposed bounded policy keeps the public `prepare(..., live)` contract
unchanged. An explicit internal `LocalOnly` versus `LocalAndRemote` observation
policy selects whether remote helper probes are admitted. Runtime block
preparation selects local-only observation; native metadata, parent frontiers,
named-pipe lexical classification and retained FIFO generation proofs remain.
Remote endpoints stay `Unknown` until acquisition, which must retain conservative
alias edges and prove finite/type/selected-source conditions before effects.
Validation must cover an unreachable SSH endpoint without starting a helper,
local/WSL conservative alias dependencies, unchanged public live inspection,
native FIFO/named-pipe journeys, WSL native-ext4 imports and mounted-Windows alias
rejection. Directional barrier motion must not turn unknown aliases into an
independence proof or reuse stale observations across an external effect.

## Accepted optimization: acquisition-owned remote facts

### Controlled source and artifacts

Acquired remote source-root receipts, metadata and recovery cleanup were repaired
**before both causal arms**. They are not part of the performance treatment.
Both releases use source HEAD `8cef15ca9d329ebdbcce6ad53fdf6c940438bff6`, Rust
1.99.0, `cargo build --locked --release --bin msync`, two jobs and no incremental
compilation:

| Arm | Frozen image SHA-256 |
| --- | --- |
| Remote lookahead before | `755a5d6ffe7080e694f91643aeb39ecfcf6149e35b8d6e16462e36c517350e1a` |
| Local-only block observation after | `677de91009c7322a753ba02df5a131c7c0703c8bcca8a5f518f79606690967db` |

The images and adjacent source/provenance manifests are managed artifacts under
`target/performance-v5/baselines/`. Comparing every manifest entry found exactly
two differing files: `src/preparation.rs` and its tests in
`tests/block_specialization_v5.rs`. Production treatment is the 29-line explicit
observation-policy change; public `prepare(..., true)` behavior is unchanged.
Later test-only SSH executable selection and all-local proof-equality regressions
are not hidden input differences between these measured images.

Every local Cargo, WSL/SSH adapter and validation process was terminal before
measurement; no source, document, Git or compiler work ran concurrently. Existing
destinations used five alternating pairs of `compile,plan,run`; missing ones used
five pairs of `run`. Both arms used identical owned macro data, resource defaults
and tracing. The runner enforced native WSL **ext4 magic `ef53`**, **D: NTFS**,
stale-child removal and exact bytes. All **40 run invocations** passed: 28
files/126,082 bytes local and 28 files/126,083 bytes mixed. Static commands stayed
pure with zero transport/runtime/publication spans. Start/end image hashes matched.

### End-to-end results and mechanism

| Destination / workload | Before mean ms | After mean ms | Before p50 ms | After p50 ms |
| --- | ---: | ---: | ---: | ---: |
| Existing / mixed | 3347.54 | 2877.74 | 3447.85 | 2824.12 |
| Missing / mixed | 3451.10 | 2964.96 | 3508.31 | 3061.80 |
| Existing / local initial five pairs | 357.02 | 380.79 | 358.50 | 365.39 |
| Missing / local | 444.01 | 442.94 | 440.10 | 437.15 |

Mixed mean command time fell **14.0% existing** and **14.1% missing**. Mixed p50
fell 18.1% and 12.7%. These are observed results, not guaranteed percentages.
Existing mixed paired deltas (after minus before, ms) were
`-476.61, +129.80, -2077.77, +414.92, -339.34`; missing deltas were
`-124.83, -747.37, -837.91, -765.93, +45.35`. Private config capture still varies
substantially. Five pairs do not characterize tail latency.

Every mixed before run launched **six WSL helpers**, every after run **four**;
both retained exactly two guarded private acquisitions. Mean nested transport
totals fell 398.4 ms existing and 418.1 ms missing. Nested spans overlap and
cannot be added as independent command-wall costs. Static compile/plan medians
stayed roughly 25–32 ms, zero helpers in both arms. No IO-engine replacement,
ring mechanism or weakened durability was introduced.

### Investigated local control and accepted trade-off

Initial existing-local mean increased 6.66%, so it was **not dismissed as noise**.
A quiet eleven-pair local-existing extension used the same images and validation:

- Mean 343.866 to 346.742 ms: **+0.84%**.
- p50 338.712 to 344.194 ms: **+1.62%**.
- Paired deltas: `+4.83, +37.99, +56.29, -135.35, -24.50, +38.90,
  +5.48, +14.33, +19.87, +8.13, +5.66` ms; median **+8.13 ms**,
  **nine of eleven positive**.
- Mean internal `execute` span was 312.33 to 312.10 ms; classification 0.85 to
  0.82 ms and acquisition 97.03 to 95.19 ms. These do not demonstrate an IO
  execution slowdown, but do not erase the whole-command observation.

The initial 6.66% mean increase was not reproduced. **No zero-regression claim is
made.** The coordinator accepted the small measured local-command trade-off for
the substantially larger observed benefit on the user's dominant mixed macro and
the fixed two-helper reduction. Deferring remote proofs can retain alias hazard
edges that a fresh filesystem-separation proof could remove, potentially losing
parallelism in other graphs; deferral is not universally faster.

Native local metadata, existing-parent frontiers, named-pipe lexical handling and
retained FIFO generation proofs remain. Remote endpoints stay `Unknown`;
acquisition establishes finite/type/root/alias facts before effects. Candidate
gates included Windows/Linux suites plus real SSH streams, WSL-native imports
and mounted-Windows alias rejection. An additional all-local regression compares
facts, complete planning context, complete graph and serialized original/optimized
programs between block-local and public live preparation. The coordinator owns
its final compilation/execution gate; adding its source is not a passing result.

### Reproduction and retained evidence

```powershell
./benches/macro_latency_v4.ps1 -Executable BEFORE_755A5D -Candidate AFTER_677DE9 -Samples 5 -Commands compile,plan,run -DestinationState existing -RequirePureStatic
./benches/macro_latency_v4.ps1 -Executable BEFORE_755A5D -Candidate AFTER_677DE9 -Samples 5 -Commands run -DestinationState missing -RequirePureStatic
./benches/macro_latency_v4.ps1 -Executable BEFORE_755A5D -Candidate AFTER_677DE9 -Samples 11 -Endpoint local -Commands run -DestinationState existing -RequirePureStatic
```

Under `target/performance-v5/`: `causal-source-delta.json`,
`causal-{existing,missing}-pairs.log`, their `-raw/` directories,
`causal-{existing,missing}-deltas.json`, `causal-all-deltas.json`,
`causal-comparison-summary.json`, `causal-local-existing-extension.log`, its raw
trace directory and paired-delta/phase JSON. Raw directories retain generated
scripts and every invocation's trace JSONL. Earlier `23f73b1a` and `ba42e88c`
images/results are historical only, not this optimization's causal control arm.
