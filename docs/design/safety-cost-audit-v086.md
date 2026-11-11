# Defensive mechanisms in the C# implementation

## Scope and result (2026-11-11)

Implementation status after the follow-up: C01 is corrected for selected local
lazy/owned cohorts and existing-directory merges; remote/unknown-destination
fallbacks remain. C03 now uses tag-based classification (Windows execution is
still pending). Commits through `e775b2d` had already corrected C04's private
intraprocedural passes, C05 and C06. C02's execution-time identity owner remains
open. The native-effect, local tail-call and recursive-capture work below adds
new findings rather than re-counting those earlier corrections.

The user requested a whole-system review of defensive mechanisms that damage
performance and architecture. This is the current C# audit, not a replay of the
retired Rust inventory. Reviewed paths span source/session analysis, HIR and
bytecode boundaries, portable/contextual optimization, VM dispatch and ownership,
resource scheduling, local/remote transfer, platform classification, configuration,
telemetry, editor analysis and package publication. This is a path/ownership
review, not a claim that every source line or platform was executed.

The dominant remaining problem is **the scope of a fallback exceeding the scope
of the uncertainty**. A cohort with an unavailable footprint drains unrelated
processes; a session-local stability flag disables private function transforms;
one overlapping source causes an entire cohort to be copied into staging.
Repeated resource discovery adds a second, independent cost.

An isolated managed Release build succeeded with zero warnings/errors using
.NET SDK 10.0.403. The initial inspection encountered concurrent product edits,
which it did not modify; subsequent implementation is recorded below.
Binary hashes, focused probe sources and observations are
in [the audit artifacts](../../artifacts/benchmarks/safety-cost-v086/README.md).
Measurements here are behavioral and syscall-count observations, not Native AOT
speedup estimates. No full regression matrix was run.

## Findings and correction order

| ID | Priority | Mechanism | Evidence | Decision |
| --- | --- | --- | --- | --- |
| C01 | P1 | Cohort fallback drains all process/transfer work | Actual gated CLI cases below | Replace with selected dependency admission |
| C02 | P1 | Native dependency queries rebuild physical path facts per invocation | Actual syscall counts below | Give execution-time namespace facts a defined owner and invalidation boundary |
| C03 | P1 | Every Windows reparse point is classified as a symbolic link | Code path and Windows tag contract; not Windows-host reproduction | Classify once by tag/capability and reuse the result |
| C04 | P2 | Session-wide `PreserveLocals` disables private Scope optimization | Isolated pass probe and production call chain | Express stable entry slots separately from private function storage |
| C05 | P2 | Unused configuration rejects ordinary copying | Actual CLI failure | Keep compatible setting parsing; remove authority of unused settings over execution |
| C06 | P2 | One related replacement source snapshots all cohort members | Direct selected-path argument | Retain bytes only for sources affected by publication |

### C01: a fallback becomes a global synchronization boundary

`Runtime/Scheduling/Interpreter.Cohorts.cs:ScheduleCohortAsync` has a useful
precise path for owned snapshots, but calls `DrainAsync()` for everything else.
`DrainAsync -> JoinAllAsync` waits for **sessionProcesses as well as transfers**.
`LocalCohortFootprintAsync` rejects an existing member directory, and
`TransferEngine.LocalCohortTarget` rejects replacement destinations before that
precise path is available. A lazy transform also fails the snapshot gate.

The probe launches a real shell waiting for an external `gate` file, schedules
its stdout to `held.txt`, and then copies unrelated input. The host waits for
`child-ready`, observes the destination for one second, releases the child,
and verifies process status and final bytes. This follows the existing
`tests/Msync.EndToEnd/cohort_dependencies_journey.py` method, extending its
fresh materialized-file case rather than repeating its complete matrix.

| Independent copy | Output before releasing unrelated child | Final exit / bytes |
| --- | --- | --- |
| Materialized one-file Range into fresh directory | Yes | 0 / correct |
| Identity `ranges::transform` of that Range | No | 0 / correct |
| Materialized directory into an existing member directory | No | 0 / correct |
| Materialized one-file Range into `output/.` | No | 0 / correct |

If the child waits for the destination that msync is about to publish, the same
dependency cycle has no external release. The probe uses an external release
to finish cleanly; it does not leave hung children behind.

Correct the model, not each spelling: prepare the selected finite cohort's
actual read/write footprint, then admit it against relevant outstanding work.
For existing-directory merges, descendant symlinks require actual namespace
dependencies; simply deleting `DrainAsync` would lose those hazards. Lazy
membership must still be evaluated once, at its semantic observation point,
and callback effects must retain their ordering. Endpoint selection must not
authenticate untaken branches. This belongs to VM admission/transfer ownership,
not to calling the planner from the VM.

### C02: temporary proof owners repeat path walks

`Interpreter.Scheduling.cs:AwaitNativeDependenciesAsync` creates a new
`PlanFileSystem` whenever outstanding registered work exists. Its `footprints`,
`namespaces`, `kinds` and `localParents` caches therefore live for one native
invocation. `ObservationResources` constructs ancestor paths and resolves
physical identities again. An unrelated outstanding transfer is enough to
activate this path for repeated `fs.exists` on the same unchanged file.

The syscall probe holds one unrelated process output pending, executes 1 or 16
`$std::fs::exists(./input.txt)` calls, publishes a ready marker, then releases
the process. `strace -f -qq -e trace=%file,fsync` observes:

| Native exists calls | `readlink` on input leaf | `readlink` on fixture parent |
| ---: | ---: | ---: |
| 1 | 1 | 6 |
| 16 | 16 | 36 |

Both programs completed correctly. Fifteen additional queries caused fifteen
additional leaf and thirty additional identical parent readlink probes, before
counting higher ancestors. These are counts, not latency measurements. A fresh
filesystem observation remains necessary for `exists`; repeated scheduler
identity establishment is a distinct responsibility.

Do not keep a forever-valid canonical-path cache. First separate live
observation from dependency-footprint discovery; reuse identities within a
defined admission/namespace lifetime and invalidate affected paths after actual
namespace publication. Operations that may merge through descendants need
their own footprint. An alternative is to carry suitable handle-relative
identity through admission. Preserve the existing contract that prepared
artifacts are not filesystem freshness proofs.

### C03: Windows platform classification is too coarse

`Platform/NativeFileMetadata.cs:FileMetadata.IsLink`,
`Native/Files/NativeFileSystem.cs:Status`, and
`Transfers/TransferFiles.cs:CopyNode` treat `FileAttributes.ReparsePoint` as a
symbolic link. `CopyNode` then calls `CopyLink`, whose `LinkTarget ?? throw`
rejects non-link reparse data. Recursive traversal also stops at all reparse
points. This conflates cloud/data-filter files with name redirection.

Windows explicitly distinguishes name-surrogate tags from other reparse tags,
including cloud tags. Use the tag to establish a platform node kind once;
ordinary data-filter files should retain native file-copy/read behavior. Keep
explicit symlink/junction handling rather than treating all surrogates as the
same kind. A Windows acceptance case needs a real cloud/WOF or equivalent
provider-managed file, plus ordinary symlink/junction controls; this Linux audit
did not execute that platform journey.

Implemented correction: `FileMetadata` now owns an explicit link capability,
independent of its retained raw attributes. `NativeFileMetadata.IsLink` returns
immediately for ordinary entries; only Windows reparse entries query
`FindFirstFileW`'s `dwReserved0` name-surrogate bit. This avoids opening/hydrating
every ordinary file. Volume/share roots use a metadata-only
`FileAttributeTagInfo` handle query because FindFirstFile does not inspect roots.
The .NET 10 public FileSystemInfo API does not expose the tag; LinkTarget is a
target-reading API for supported link formats, not a general data-filter test.
See [Windows tag semantics](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-tags)
and [the runtime API discussion](https://github.com/dotnet/runtime/issues/1908).

Transfer dispatch, overlap capture, archive metadata, filesystem status and
directory traversal consume the capability. WOF/cloud files keep their real
length and native CopyFileEx path; non-surrogate directories remain traversable.
Symbolic links and junctions retain the existing target-based copy behavior.
An unsupported name-surrogate target reports its actual unreadable target rather
than silently becoming an empty ordinary directory in overlap capture.

Focused acceptance is `Msync.Transfer.Tests --node-kind-only`: tag properties
cover all 16 cloud variants, WOF, junction/symlink controls and every low-16-bit
tag value; actual filesystem journeys exercise status, recursive copy and link
preservation. The Windows branch creates a WOF file using `compact.exe /exe:lzx`,
copies it directly and through an overlapping-tree snapshot, and checks junction
classification/traversal/removal. Linux execution, the native filesystem journey
(120 assertions), and a strict Runtime build passed; Windows branch execution and a provider-backed cloud-directory fixture
remain platform acceptance work, not claimed Linux results.

### C04: stable session entry slots veto private function optimization

`SemanticSession.Compile` requests `PreserveLocals: true` for the image.
`Optimizer` correctly skips the entry function in its per-function loop, but
`ScopeScalarization.Optimize` and `CrossBlockMemoryOptimization.Optimize` each
return immediately for **any** function when the module flag is set.
The memory-summary phase has the same module-wide gate.

A focused probe passes the same non-entry function, produced from
`fn get(n) { let s = scope { x: n }; s::x }; get(3)`, through the actual
`ScopeScalarization` implementation with only that flag changed:

| PreserveLocals | Remaining MakeScope instructions |
| --- | ---: |
| false | 0 |
| true | 1 |

This establishes a lost transformation, not an end-to-end timing claim. It
also contradicts the intended entry/private-function distinction documented in
`terminal-library-v080.md`. Express entry slot stability, stable function IDs,
and private frame layout as separate constraints. Local scalar replacement
must retain escaping objects and captured cells; whole-module compaction and
session dispatch identities are different concerns and must not be enabled by
blindly clearing the flag. Start with the local function passes, then assess
specialization/compaction separately.

### C05: unused settings remain execution admission policies

`Configuration.Validate` requires `execution.max_streams > 0`; source references
to that key are only its default and this validation. The runtime concurrency
owner uses `Jobs`, `MemoryLimit` and `BufferSize`, not `max_streams`.
With `[execution]\nmax_streams = 0`, an ordinary literal-to-file script exits 1
before creating its destination. `progress.delay_ms` and `progress.interval_ms`
likewise have validation/default references but no current product consumers.

Do not replace this with unknown-key rejection or remove old config syntax.
Keep existing configurations readable and remove semantic admission checks for
settings that cannot affect this implementation. If a feature is implemented
later, validate its actual effective configuration at its owning boundary.

### C06: one overlap expands staging to the whole replacement cohort

`Transfers/TransferSelection.cs:ReplaceLocalCohortAsync` uses
`sources.Any(TransferOverlap.Related(...))`. Once one source overlaps, it calls
`CaptureTree` and `Materialize` for **every** source and then copies the entire
stage into the destination. In a mixed cohort, unrelated large sources receive
an extra read/write pass and require additional temporary disk capacity solely
because one other member needs retained bytes. This is directly visible from
the selected code path; no throughput number is assigned.

Partition retention by actual publication dependencies: snapshot the endangered
members before removing the target, then stream retained and unaffected members
through one installation path. Account for symlink-mediated overlap, not just
lexical containment. Keep necessary overlap snapshots; this is not a request
to reintroduce rollback, before-images or durable recovery registries.

## Other reviewed boundaries

| Area | Current assessment |
| --- | --- |
| Artifact/HIR validation | Working-tree edits already separate produced writes from externally supplied objects. Do not count those duplicate-write traversals as remaining defects. Keep format/type/reference validation at decode/public construction boundaries. |
| Native call arity / VM operands | Concurrent work already moves direct-call checks toward producer guarantees. Public dynamic invocation is a different boundary; do not delete its diagnostics. |
| Scope field offset hints | `ScopeOffset` checks hinted offset/name because distinct dynamic shapes can order fields differently. Remove only with a stronger shape contract, not because a branch exists. |
| Compiler/session facts | Immutable accepted roots, query forks and rejected-generation rollback serve actual session/editor semantics. They are not the removed transfer recovery protocol. No new defect established here. |
| Portable/contextual optimization | The current working tree is narrowing contextual folding. The executable-layout backedge veto remains broad, but the existing performance-assumption audit already explains why deleting it can alter preparation. Reuse that result. |
| Residual remote dependencies | `Key(PathValue)` uses an endpoint-wide key for unresolved remote paths. It serializes same-endpoint residual work, but avoids speculative authentication and false independence under aliases. Improve using already-selected session metadata, not raw string independence. Not an additional reproduced defect. |
| Transfer durability | Ordinary local publication uses owned staging/replace and one file flush. No restoration of the old registry/syncfs protocol was found in the reviewed production transfer path. |
| Archive framing / WSL receipt | Length and channel-completion checks represent actual wire/platform requirements. Retain them; checks are not interchangeable with arbitrary quotas. |
| Telemetry | Queue submission is nonblocking, but opening a sink is synchronous and disposal joins its writer. JSONL also parses SQLite-only policy. These are observer coupling follow-ups, not measured transfer regressions here. Do not fix by silently dropping diagnostics. |
| Editor | Generation checks and cancellation prevent stale analysis publication; per-document revision ownership is appropriate. No new defensive-cost defect established. |
| Packaging/release | Payload versions, hashes, format and native/editor acceptance identify the bytes being published. Keep those contracts; this audit did not change workflows or run publication. |

## Design synthesis and external context

The desired direction is not unchecked execution. It is a small number of
semantic owners carrying enough information that consumers do not repeatedly
rediscover it:

```text
external input -> decode/classify once -> owned semantic representation
                                         |
                    selected effect -> precise dependency admission -> execute
                                         |
                              retire/invalidate affected facts
```

* [LLVM's New Pass Manager](https://llvm.org/docs/NewPassManager.html) explicitly
  discusses quadratic costs from requesting outer analyses in inner passes.
  Its preservation/invalidation model is relevant to C02/C04: scope facts to
  actual changed inputs rather than rebuilding or invalidating whole contexts.
* [Linux pathname lookup](https://docs.kernel.org/filesystems/path-lookup.html)
  describes cached lookup and concurrency-sensitive resolution. This supports
  handle/namespace ownership, not permanent application-level path caching.
* [InfiniFS, FAST 2022](https://www.usenix.org/conference/fast22/presentation/lv)
  investigates locality and long path-resolution costs in distributed metadata.
  The transferable research question is how resolution cost scales with path
  depth and repeated ancestors. Its distributed architecture and speedups are
  not evidence for msync, nor a reason to build a new metadata service here.
* [Windows reparse tags](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-tags)
  and [name-surrogate classification](https://learn.microsoft.com/en-us/windows/win32/api/winnt/nf-winnt-isreparsetagnamesurrogate)
  provide the concrete platform distinction required by C03.

Implementation order: fix C01's end-to-end admission semantics first; narrow
C04 and remove C05 as independent changes; redesign C02 within execution-time
namespace ownership; fix C03 at the platform classification owner; partition
C06 retention without changing normal-copy/retry semantics. Validate each with
the changed journey and its genuine dependency/alias controls, not a full matrix
or a new performance quality gate.

### C01 local admission correction (2026-11-11)

Selected local cohorts no longer require an owned-snapshot spelling to avoid a
session-wide join. Admission resolves a selected Range thunk and enumerates its
members once on the VM, before acquiring a transfer worker slot. The resulting
owned snapshot, not the lazy producer, reaches transfer execution. Native reads
and callbacks retain their ordinary VM dependency admission; callback captures
are neither replayed nor executed concurrently with subsequent VM dispatch.
Direct remote `PathSelection` values remain unenumerated at this boundary so
metadata and bytes retain their selected native SSH channel.

Existing local member directories use a source-shaped merge footprint. After
relevant namespace publications complete, admission walks source directories
without following source links, projects those relative directories onto the
destination, and adds only physical subtrees outside already registered write
roots. A preorder traversal carries each directory's containing physical root
from its lexical parent; ordinary descendants require one containment check,
not a scan across all member roots. Alias roots use a hash set for deduplication.
It does not scan unrelated destination content. Namespace producers of
those selected aliases complete before their physical identities are resolved
again. Ordinary process-output writers remain dependency edges of the eventual
cohort rather than reasons to join unrelated processes. Source traversal uses
`NativeFileMetadata.IsLink`, including Windows name-surrogate classification.

Focused real-process coverage in
`tests/Msync.EndToEnd/cohort_dependencies_journey.py` exercises all three source,
`.mso`, and `.mexe` routes. Lazy independent cohorts and existing-directory
merges publish before an unrelated gated child completes. Source-dependent lazy
cohorts, native reads inside lazy callbacks, and destination descendant symlink
aliases to the gated output wait for the actual producer. Captured callback
counts prove one evaluation. The isolated managed Release CLI build under
`.temp/cohort-admission/build` completed with zero warnings or errors; the first
five selected cases passed across all three routes (15 journeys). Four additional
selected cases (independent descendant alias, owned snapshot, owned replacement,
and dependent owned replacement) also passed across all three routes (12
journeys), for 27 focused real-process checks in total. After changing alias
containment to parent-carried roots, the three merge cases were extended with
a deeper directory and rerun successfully across all three routes (nine
additional executions). The changed C# file also passed the repository's
`dotnet format whitespace --verify-no-changes` check.

The residual remote/unknown-destination fallback is not claimed fixed here.
Replacing it requires a selected transport-owned footprint for the operation;
materializing a remote selector in a separate metadata channel would violate
one-channel ownership and is not an acceptable shortcut.

## Native semantics and call-site ownership follow-up

The producer/consumer investigation found two unreachable native semantic
fallbacks: `InstructionEffects.NativeEffects` and `MemoryEffects.Native` checked
whether registered behavior was missing, then guessed behavior from name prefixes.
`NativeSignatures.Build` already finalizes every registration before publishing
the table. The fallback implementations were removed. A registry-completeness
assertion belongs in its focused contract test, not on every consumer invocation.
The VM likewise no longer rechecks the fixed arity of direct `math.pow` or
silently replaces a malformed lazy-range Type operand with `Any`: direct-call
arity and Type-constant identity are established by lowering/the artifact reader.
The legitimate absent item-type operand still means `Any`.

The registry itself also conflated lexical path operations with filesystem
failure. `fs.filename`, `fs.has_extension`, other pure lexical `fs` operations,
and the `path` primitives now explicitly declare no operational failure for their
admitted argument types. Real reads, writes, conversions with value constraints,
and enumerator/callback effects retain their contracts. The real CLI journey
`tests/Msync.EndToEnd/lexical_effects_journey.py` reads a path string at runtime,
checks discarded versus retained lexical computations across source, `.mso` and
`.mexe`, changes input bytes between runs, and removes the input to check that
actual I/O failure is still reported before publication. Discarded native lexical
calls decrease from two at O0 to zero at O2; retained filename results remain
correct. This is a code-work removal observation, not an elapsed-time estimate.

Terminal range callbacks exposed a different missing fact. Their signatures
said they enumerate but did not say they invoke the supplied callback.
`NativeElementCallback` now identifies the callable and range argument positions
at registration. Memory analysis maps the actual callback summary rather than
either forgetting its writes or assigning global unknown writes. Repeated
invocations remap until real abstract-memory invalidations stabilize: a captured
cell can redirect later iterations to another object. `ValueInfo.Elements`
describes membership possibilities, not an exact iteration count. Convergence
uses an analysis-local invalidation version, without copying the whole state on
each iteration or incorporating a counter into CFG equality. Pure/read-only
callbacks need one mapping; known empty ranges invoke nothing. Focused tests
observe actual fields at O0/O2/O3 and after artifact roundtrips, including
multi-element captured-cell rebinding.

Tail-call admission now consumes closure identity and capture-count facts from
the call-site analysis. A known closure does not need a new whole-module proof
that no unrelated large or native closure exists. Unknown callees retain the
existing closed-module route; malformed direct-specialization overflow is not
silently converted into a successful loop. The focused tail suite passes 52
contracts, including a serialized 100,000-step known-closure tail loop in a
module containing unrelated native and larger closures.

The tail suite also exposed a source-semantic growth bug before optimization:
an escaping closure written to a capture recursively expanded the specialization
key although the recursive function never read that old callable value. Its
fix separates the identity needed to recognize an **active recursive promise**
from the full input identity required to reuse a **completed instance**. See
the follow-up in [generic specialization](generic-specialization-v080.md).
No depth quota, function-count cap or blanket unknown callable was introduced.

Follow-up acceptance is selected by the changed contracts: 24 recursive-capture
journeys, 15 persistent-session factory checks, nine standard-root materialization
controls, 52 tail-call checks, 92 memory-effect checks, and the real CLI lexical
journey's 12 successful routes plus their missing-input counterparts. Local
cohort and platform checks are recorded in their sections. The combined Release
CLI builds without warnings/errors; changed-file formatting and diff checks pass.
An additional `StdlibLoading --captured-modules` fixture was attempted but does
not pass: its replacement `containers` value omits the currently required
`SnapshotStorage` member and is rejected by structural assignment checking. That
fixture was not changed or counted as successful acceptance.

### C02 intra-observation correction (2026-11-11)

`PlanFileSystem.Resource(..., followLeaf: true)` now reuses the already resolved
non-followed entry when this same filesystem observation owner has already
classified the leaf as nonlink. It reads existing dictionaries only: no extra
stat, longer-lived cache, or invalidation protocol. Unknown and symlink leaves
still resolve normally. Projected paths retain the existing future-state rules
and do not use an initial-kind shortcut. Entry identities themselves are now
canonical: local paths resolve their parent first, then normalize the final
component; remote paths reduce the final `.` or `..` against their already
canonical parent. Normalization does not lexically collapse `link/..` before
following the link.

The frozen audit image and candidate were rerun using `metadata_probe.py` in
separate fresh directories. This isolates the actual selected syscall question,
not an elapsed-time benchmark:

| `fs.exists` calls | Leaf `readlink`, before → after | Fixture-parent `readlink`, before → after | Leaf stat calls, before → after |
| --- | --- | --- | --- |
| 1 | 1 → 0 | 6 → 5 | 2 → 2 |
| 16 | 16 → 0 | 36 → 20 | 32 → 32 |

[The follow-up receipt](../../artifacts/benchmarks/safety-cost-v086/metadata-followup-results.json)
records assembly hashes and counts. The original probe's `input_stat` filter
missed the `/./input.txt` spelling; the receipt counts both spellings from the
same traces. All four runs completed with exit 0. Namespace lifecycle/future
alias checks passed via `SchedulingGraph --namespace-only` in source, `.mso`,
and `.mexe` modes. The real-process cohort journey now also tests a native copy
reading through a leaf symlink to a gated producer's destination; all three
routes correctly waited and read the new bytes.

This removes one redundant full-path walk within each observation. It does not
claim to resolve C02's broader repeated discovery across distinct native calls;
that ownership question remains separate.

The first nonlink-reuse candidate exposed a real trailing-dot regression:
`fs.is_empty("./watched/.")` observed an empty directory before its pending
producer published a file. Canonicalizing the entry representation fixed it.
Focused `native-dot-dependent`, `native-dotdot-dependent`, and
`native-link-dotdot-dependent` process-gated journeys pass in source, `.mso`,
and `.mexe` modes (nine executions). The symlink-parent case ensures `link/..`
selects the physical parent, not the lexical parent. Namespace checks were rerun
after this representation change, and a fresh syscall probe retained the exact
0/0 leaf and 5/20 parent `readlink` counts reported above, with unchanged 2/32
stat counts. The receipt identifies this corrected measured image.
