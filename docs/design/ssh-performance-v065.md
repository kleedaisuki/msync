# SSH orchestration performance in v0.6.5

## Decision and measured workload

The useful target is authentication/process orchestration, not a larger network
buffer. A real unprivileged OpenSSH fixture found that baseline `d501fd2` uses
eight authenticated connections for one 4 KiB upload and forty for five uploads.
Its configured OpenSSH connection sharing is deliberately disabled, exposing
msync's own invocation behavior rather than an already warmed external master.
Source maps and initial counts are in the [audit](ssh-performance-v065-audit.md);
the [fixture](ssh-performance-v065-fixture.md) records reproducible setup.

The exact baseline source archive, release binary and rlib are retained under
`target/performance-v065/baselines`. The binary SHA-256 is
`dca16a83beb10131ecde492ba6ff587dd90478bfe0dc4450d41088b779fff8e8`.
Its source is `d501fd2`, including the earlier automatic-recovery repair, not the
published v0.6.0 tag. Diagnostic prototype elapsed values are not accepted timing
comparisons. The frozen optimization image and accepted paired window are identified
below, separately from those discarded prototypes.

## Accepted paired result

Five sequential AB/BA rounds used the exact optimization image SHA-256
`e18d9f36b905630b9915cc5634e4ff4833a66ee34924650fc555913482433f7f`
and probe SHA-256
`38eeea3bcb7aff7d1cd6ab334c6f7eddb80670f3c3b1ee0296f4e996d299469f`.
All 60 observations verified bytes, mode, whole-second mtime or the expected
named missing-source failure with unchanged previous target. Image/probe hashes matched at
both ends, and the owned server was stopped. Root and peer builds, tests and
filesystem writes were held during this syncfs-sensitive window. The native
Linux loopback fixture used OpenSSH 10.0p2 / OpenSSL 3.5.7; it is not a native
Windows or real-WAN performance claim.

| Workload | Before → after p50 ms | After wins | Authenticated connections | Remote sessions |
| --- | ---: | ---: | ---: | ---: |
| 4 KiB upload | 1207.744 → 488.249 | 5/5 | 8 → 1 | 8 → 8 |
| Download | 452.433 → 272.958 | 5/5 | 3 → 1 | 3 → 3 |
| Single `exists` | 173.223 → 168.233 | 3/5 | 1 → 1 | 1 → 1 |
| Regex loop, five files | 2314.422 → 733.249 | 5/5 | 16 → 1 | 16 → 16 |
| Five uploads | 5965.800 → 1801.700 | 5/5 | 40 → 1 | 40 → 40 |
| Missing download, target retained | 292.725 → 221.555 | 5/5 | 2 → 1 | 2 → 2 |

The transfer controls improve by approximately 1.66–3.31×, with no evidence for a
stable single-existence-query speedup from five mixed pairs. Remote command and
data-session counts stay unchanged: the main mechanism is authenticated
connection reuse plus responsive process completion, not less user payload or
deleted durability work. Reuse adds five initial master/check client processes
in this fixture, so total client launches do not decrease.

Joined local msync/client CPU medians (user + system) are deliberately separate
from wall time and exclude the still-running sshd/server process tree:

| Workload | Before → after local CPU ms | Change |
| --- | ---: | ---: |
| Upload | 235.243 → 198.881 | −15.5% |
| Download | 105.985 → 127.850 | +20.6% |
| Single `exists` | 43.991 → 42.592 | −3.2% |
| Regex loop | 504.827 → 367.873 | −27.1% |
| Five uploads | 1137.320 → 689.791 | −39.4% |
| Missing download | 65.988 → 112.676 | +70.8% |

The bounded initial master/probe setup trades extra local CPU for faster response
on the two/three-session cases: approximately 22 ms more CPU for a 179 ms p50
download reduction, and 47 ms more CPU for a 71 ms error-path reduction. Bulk
work improves both metrics. This is an explicit local CPU/latency trade-off,
not a claim of universally lower CPU or measured server-CPU savings. One-shot
controls retain the direct native-client path. These controls use whole-second
source times; exact fractional metadata repair is separately verified and may
add a bounded metadata helper when needed.

All matched rows are persisted in
[the observations CSV](ssh-performance-v065-observations.csv), and medians,
paired means/counts and CPU ratios in
[the summary CSV](ssh-performance-v065-results.csv). The full report artifact is
`target/performance-v065/ssh/paired-final.json`, SHA-256
`177611e7218580b46510816b0c2d430ca4660b81dd3c9e137ff83e1efb1372f6`.

## Production ownership and admission

- Retain the existing OpenSSH implementation and `SshContext` session ownership.
  Foreground multi-helper transfers/repeated control acquire the private master
  lazily on the first actual endpoint use. Later helpers share that authenticated
  connection. There is no global pool, credential cache or new remote service.
- A one-shot filesystem control operation does not pay for a separate master.
  An early prototype added needless setup latency to a single `exists`; dispatch
  now reserves reuse for transfer or repeated-control shapes. This is not a new
  user tuning knob and does not change language types or filesystem truth.
- The borrowed HIR backend visitor traverses actual typed operands, nested
  controls/ranges, qualified namespaces and path conversions. It collects
  references, **not permission to contact every possible endpoint**. A dynamically
  unselected branch starts no SSH client.
- Actual SSH flow admission reserves authentication before handing work to a
  released/background client. If optional safe control-socket storage is
  unavailable, only the newly admitted work requires foreground completion.
  Existing work IDs define that scope; all pipeline/cohort members are admitted
  before any required join. The interpreter never turns a possible SSH reference
  into a global wait for unrelated earlier jobs.
- Submission contexts also carry existing owned control-directory identity into
  later local-only captures. No client or authentication is created merely to
  expose that metadata. An owned Unix socket inside cwd must not become user
  payload when syncing the complete cwd.
- The control directory retains a `same_file` identity capability. SnapshotStore
  excludes only that verified identity, not a `.temp/ssh-*` name pattern. A
  similarly named user directory remains data; an unrelated socket remains a
  visible unsupported-node error. Replacement fails validation and is not
  silently excluded or removed. External/user-owned control masters do not grant
  msync directory-exclusion or cleanup authority.

An early real PTY replay caught the incorrect plan-wide foreground fallback:
a false SSH branch completed its local write but waited about two seconds until
an unrelated held child was explicitly released. The accepted implementation
removes that global conversion; the regression test holds the child behind a
file gate until **after** the new prompt returns. A second actual encrypted-key
FIFO journey verifies the selected branch authenticates before background
handoff while the unrelated child stays held. These are semantic ordering
checks, not a flaky millisecond performance threshold.

The first reuse prototype also failed a whole-cwd upload that the baseline
supported: its own `.temp/ssh-.../s1` socket entered source capture. The verified
identity exclusion above fixes both same-unit copies and a later local-only REPL
submission, without hiding user data. Neither prototype is a release candidate.

## Child completion and metadata fidelity

Synchronous transport helpers previously checked completion every 10 ms. The
new waiter uses a Linux pidfd or a Windows process handle to wake at actual
completion, retaining the existing bounded cancellation checks. Standard Child
ownership still performs reaping. Unsupported Linux kernels/sandboxes retain the
old polling fallback, with strategy/fallback errno recorded in transport spans.
No additional timer thread or global SIGCHLD handler is installed. The already
resolved `wait-timeout` crate was not promoted to a runtime dependency: its own
upstream documentation warns about conflicts when an application already handles
SIGCHLD, as Tokio does.
An additional traced upload of that same optimization image verified all five synchronous transport
records actually selected `wait.strategy=pidfd` on this host and recorded one
`ssh.session.admit` phase; the optional mechanism did not silently fall back in
this measurement environment. That diagnostic upload ran after all timing lanes
were released and is not mixed into the accepted disabled-trace samples.

SCP remains the byte transport. The [mtime repair](ssh-performance-v065-metadata.md)
uses the existing GNU tool requirements and source-validation observations to
restore fractional upload timestamps on **private staging**, before publication.
Download receipts now retain GNU find's exact nanosecond/floor-second encoding.
Whole-second timing controls add no timestamp-repair SSH command. Permissions,
no-follow checks, metadata bounds, cancellation, durable intent, independent
SETTLE and recovery are not removed to manufacture a speedup.

The redundant generic `/tmp` staging layer remains a future opportunity. This
release does not replace it with raw SCP into a public destination or weaken
metadata/provenance checks merely to save helper invocations.

## Focused local evidence

- Borrowed backend traversal and child-event status/reaping tests passed.
- The metadata selections passed six receipt cases (including a 96-case signed
  timestamp property) and four upload cases. Real positive/pre-epoch upload and
  download checks retain exact bytes, mode and nanoseconds; their identified
  diagnostic images are recorded in the metadata note.
- The seven SSH-context tests passed, including exact-identity exclusion and
  refusal to exclude/remove a replaced control directory.
- Real native encrypted-key authentication and cancellation, user-owned master
  survival, owned background-FIFO cleanup, lease loss/foreground reauthentication
  and long-cwd native fallback passed in focused selections.
- The selected/unselected dynamic-SSH PTY tests passed, along with the existing
  unavailable-mux FIFO cancellation test after real opening.
- The publication owner ran real remote cohort and whole-cwd capture journeys,
  including same-name user data, an unrelated socket and a later local-only REPL
  submission. Its maintained runner is `tests/ssh_cohort_v065.py`.
- Selected strict Clippy and formatting/diff checks passed through the joint
  lifecycle gate. A final native Windows execution/performance claim is not
  inferred from Linux source compilation or the portable API implementation.

## Primary references

- [OpenSSH connection sharing](https://man.openbsd.org/ssh_config#ControlMaster):
  reuse authenticated sessions with endpoint-distinguishing private control paths;
  preserve user masters and native credential behavior.
- [Linux pidfd_open](https://man7.org/linux/man-pages/man2/pidfd_open.2.html):
  pollable, close-on-exec task descriptors and the unreaped-child ownership rule.
- [Windows WaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject):
  completion of a borrowed live process handle with bounded waits.
- [wait-timeout upstream](https://github.com/alexcrichton/wait-timeout):
  the SIGCHLD ownership caveat explains choosing existing platform mechanisms.

## Post-measurement artifact boundary

The `e18d9f36` timing image is immutable evidence, not a name to reuse for later
delivery bytes. A subsequent native Windows run exposed a shared logical-path
normalization defect outside SSH admission: verbatim drive roots became uncertain
UNC paths and unrelated local resources could overlap. The central fix and its
refreshed delivery image require their own identities and focused native
acceptance. They do not retroactively change these measured rows. The error-path
probe asserts a nonzero exit, a diagnostic naming the missing source, and preserved
bytes/mode/mtime; precise caret/line-column provenance is covered separately, not
proved by this timing harness.

## Delivery refresh after the Windows root repair

The central normalization repair landed in source `8818d11`. Its delivery image
is separately identified as SHA-256
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`.
It is not a relabeling of the earlier `e18d9f36` optimization image. Four exact
positive/pre-epoch upload/download checks passed first, with reports under
`.temp/v065/ssh-delivery-{positive,preepoch}-metadata.json`. The unchanged probe
then ran one final six-case, five-pair AB/BA refresh against the original `d501fd2`
baseline, after the compiler lane released and while peer builds/tests/writes
were held. All 60 observations passed, the fixture stopped, and executable/probe
hashes matched again at the end.

| Workload | Before → delivery p50 ms | Delivery wins | Local CPU before → delivery ms |
| --- | ---: | ---: | ---: |
| 4 KiB upload | 1198.946 → 472.079 | 5/5 | 232.480 → 195.027 |
| Download | 455.398 → 273.301 | 5/5 | 104.069 → 138.035 |
| Single `exists` | 173.319 → 166.715 | 3/5 | 44.248 → 47.158 |
| Regex loop, five files | 2368.714 → 750.264 | 5/5 | 504.398 → 367.763 |
| Five uploads | 5926.831 → 1710.336 | 5/5 | 1125.485 → 670.771 |
| Missing download, old target retained | 298.760 → 213.438 | 5/5 | 64.610 → 108.282 |

The final transfer controls improve by about 1.67–3.47×. Authentications remain
8/3/16/40/2 → 1 for the corresponding multi-session cases, with unchanged remote
session counts; single `exists` stays at one client/authentication/session. Its
3/5 mixed pairs and −1.827 ms paired mean do not establish a stable universal
speedup. No helpers, payload or durability work were removed by the Windows
normalization follow-up.

The explicit local CPU trade-off remains: upload/regex/bulk medians fall by
16.1%/27.1%/40.4%, but download adds 32.6% (about 34 ms), missing-source handling
adds 67.6% (about 44 ms), and the one-shot existence control has a retained +6.6%
point (about 3 ms). The multi-session setup still adds five master/check clients.
These are joined local msync/client CPU measurements, not sshd/server totals or
a claim that every isolated command is cheaper. There is no further tuning or
additional full timing matrix hidden behind these final-source observations.

The earlier report and its two CSVs were hash-verified unchanged. The refreshed
raw report is `target/performance-v065/ssh/paired-delivery.json`, SHA-256
`af2d76e4ea608e0d7e3ea17fd942461f0a984edffcb600d9b1dee669c9fd1259`.
Its independently named [delivery observations](ssh-performance-v065-delivery-observations.csv)
and [delivery summary](ssh-performance-v065-delivery-results.csv) preserve all
rows, paired deltas, counts and CPU ratios. Probe identity remains
`38eeea3bcb7aff7d1cd6ab334c6f7eddb80670f3c3b1ee0296f4e996d299469f`.

## Windows-reported tiny-upload latency audit (2026-11-01)

The reported native Windows REPL statement
`cmd -> (ssh://forge)::cmd` transferred 1,795 bytes in 35,399.5 ms, while direct
SSH use was substantially faster. This is valid workload evidence; the earlier
Linux loopback improvements above never established Windows performance parity.
The following source-path findings explain concrete amplification mechanisms,
not an attribution of those 35 seconds without native phase measurements.

### Serial transport and publication boundaries

A normal finite local-to-SSH statement still performs preparation metadata and
live destination reclassification, followed by ALLOCATE, generic send's temporary
allocation/SCP/REMOTE_COPY/temporary cleanup, PUBLISH and independent SETTLE.
Fractional source mtimes add bounded timestamp-restoration helpers. Thus one
tiny upload includes approximately nine serial SSH/SCP client operations before
optional metadata restoration or recovery. Connection reuse, when available,
reduces authentication but does not remove these remote command/session turns.

`src/execution/endpoint.rs::command_output` currently has no transport/phase span:
its asynchronous ALLOCATE/PUBLISH/SETTLE/RECOVER clients are outside
`runtime.rs::run_cancellable`'s `transport_process` spans. Existing synchronous
transport timing therefore cannot distinguish the most important remote
publication waits. Add named publication-phase spans before asking a user to
interpret a partial trace. Record elapsed/status, endpoint, operation and
nonsecret diagnostics; authentication secrets remain confined to native SSH.

The 35-second `Spec::settle` timer is a cancellation deadline, not an unconditional
sleep. A normal successful SETTLE exits as soon as its client and output readers
finish. Its journal lock has a 30-second timeout, but the parent-directory flock
is acquired first without that bound; unrelated sibling publication can delay
it. Do not infer that a successful approximately-35-second run hit either timer.

### Whole-filesystem flush amplification

GNU [`sync -f`](https://www.gnu.org/software/coreutils/manual/html_node/sync-invocation.html)
uses `syncfs`, synchronizing the **containing filesystem**, not only the named
journal. This couples one small upload to unrelated server writeback. Plain
`sync FILE` uses fsync instead, but replacing every call mechanically is wrong:
fsync of a directory alone does not flush staged file contents, and persistence
of renamed directory entries needs the containing directories as well. See
[`fsync(2)`](https://www.man7.org/linux/man-pages/man2/fsync.2.html).

The baseline successful exact-file path has these barriers:

| Boundary | Missing target | Existing ordinary file |
| --- | ---: | ---: |
| Intent, complete staging and before-image decision | 1 | 1 |
| Copied before-image | 0 | 1 |
| Installed root | 1 | 1 |
| COMMITTED receipt | 1 | 1 |
| Unconditional successful EXIT recovery handler | 1 | 1 |
| SETTLE durable intent retirement before recursive GC | 1 | 1 |
| **Total filesystem-wide flushes** | **5** | **6** |

The EXIT handler is `rollback -> recover_journal`; a COMMITTED journal bypasses
restoration but still reaches its trailing `sync -f` on the parent filesystem.
PUBLISH holds the parent-directory flock throughout this work. This is broader
than network bandwidth: unrelated files under the same parent serialize and
unrelated writes anywhere on the same filesystem can affect each barrier.

A focused local shell probe extracted the actual RECOVERY/ALLOCATE/PUBLISH/SETTLE
constants, staged exactly 1,795 bytes, wrapped `sync` to log and then execute
`/usr/bin/sync` unchanged, and verified installed bytes and journal removal. It
confirmed five calls for a missing target and six for an existing file. On this
host each underlying sync invocation took about 3–12 ms; Python wrapper startup
inflates helper wall time, so this is a **call-count and control-flow probe**, not
a Windows/WAN timing comparison or an explanation of the observed 35 seconds.
Disposable reproduction and results are under
`.temp/ssh-publication-audit/{probe.py,results.json}` while retained.

One safe opportunity is to disarm the rollback EXIT trap only **after** the
COMMITTED barrier succeeds and before printing the final receipt. That removes
a redundant success-only syncfs while retaining failure-path recovery. Do not
skip recovery solely because a COMMITTED pathname exists: the final barrier may
have failed or been interrupted. Keep independent SETTLE and durable intent
retirement; existing crash cuts before/after receipt and during partial GC are
relevant acceptance journeys. A larger replacement of syncfs with explicit
file/directory fsync needs its own durability model and crash experiments, not a
blind `-f` deletion.

### Upload directly into owned journal storage

ALLOCATE already gives the statement an exclusive same-filesystem remote
journal, but generic send allocates another `/tmp/msync.*` parent, sends there,
REMOTE_COPYs into `journal/payload`, then uses another SSH client to delete the
extra parent. This duplicates ownership and serial transport work.

An initially considered ownership-preserving improvement used `journal/upload`
and retained REMOTE_COPY into `journal/payload`. That removes two clients, but
keeps a full remote copy and can increase the destination filesystem's space
requirement when `/tmp` was on a separate filesystem. The implemented refinement
below instead uploads directly to the absent owned payload and normalizes it in
place. Raw SCP output is still not treated as a finished publication payload.

The latter distinction is critical on Windows. The existing
[mode-provenance record](remote-publication-v5.md#mode-provenance-and-windows-permission-translation)
contains actual Windows SCP results: `-rp` produced directory/file modes
`0707/0666` under server umask `077`. An uploaded Linux staging inode does not
establish original POSIX permission provenance. `RemoteSource::uploaded` marks
Windows sources Portable; REMOTE_COPY preserves timestamps, derives fresh file
modes from `0666 & ~umask`, lets directories use creation policy, and only
attenuates rights from source read-only evidence. A direct-finalization helper
must retain those rules, readonly-tree traversal, finite-node verification and
counts. Generic remote source copying must retain its actual source-filesystem
capability handling; already-materialized native upload metadata has explicit
provenance instead. Raw SCP into public destination, an unconditional
`chmod --reference`, or treating newly created ext4 staging as proof of Unix
source modes would regress an already measured user contract.

## Native Windows follow-up delivery (2026-11-01)

### Implemented ownership changes

The user reports v0.6.5, both VPN and non-VPN slowness, and the exact native
Windows REPL journey `cmd -> (ssh://forge)::cmd`: 1,795 bytes in 35,399.5 ms.
That report initiated this follow-up; it is not a request to tune the VPN.

- `Spec::run_inner` now uses `runtime::stage_ssh_source` for finite SSH journal
  uploads. SCP writes only the freshly allocated `journal/payload`; exact
  fractional times are restored there. `REMOTE_FINALIZE_UPLOAD` checks/counts
  the finite tree and normalizes portable Windows metadata in place. There is
  no extra `/tmp` owner, cleanup client, remote payload copy or duplicate complete
  payload on the destination filesystem. The finalizer replaces the old copy
  helper, so the reduction is **two**, not three, authenticated remote sessions.
- Unix upload metadata has already been materialized by the local capture owner;
  a destination filesystem-magic whitelist cannot determine that provenance.
  The private finalizer preserves it, including executable bits on F2FS/ZFS or
  other destinations. Generic REMOTE_COPY for actual WSL/remote sources keeps
  its existing source-capability policy, where synthetic modes remain relevant.
- For ordinary-file overwrite, complete the private before-image copy before
  writing intent. The initial barrier now persists new payload, copied old data
  and decision/identity records together, before any public mutation. An
  interrupted partial copy without intent remains private garbage.
- Disarm the publisher's rollback EXIT trap only after COMMITTED's final sync
  succeeds. Failures/signals before that point still run recovery. The successful
  path no longer repeats a filesystem-wide recovery flush.
- Async ALLOCATE/PUBLISH/SETTLE/RECOVER now emit `transport_process` records with
  explicit phases and spawn/wait/drain timing. An explicitly propagated
  publication TraceContext survives task and blocking-worker boundaries. New
  payload/finalization/time spans record endpoint and session policy; policy is
  refreshed after lazy Unix admission, not guessed before client configuration.

The same actual-helper count probe found missing-target syncfs calls **5 → 4**
and existing-ordinary-file calls **6 → 4**, with correct bytes and completed
journal cleanup. These changes preserve independent settlement, intent retirement
before destructive GC, exact pending discovery and live-owner locks.

### Real SSH observations and identity

Frozen baseline is the published delivery image SHA-256
`f9162114c165da91d5336a51185e7329f25d584e4b4e465519a2e6ddec4498a0`;
the working-tree follow-up image is
`563850aa181b354d1378767f44b81b7123126f615e951042449052ac69b083f7`.
This does not rename or replace published v0.6.5 assets. Source base is `964bc4f`
plus this follow-up working diff. Both executable hashes were checked by the
unchanged `benches/ssh_v065.py` runner at both ends of each run.

Three alternating AB/BA pairs per case used the real private Linux OpenSSH
fixture. All observations verified bytes, modes and whole-second mtimes.
The native-client topology uses a deliberately overlong private control pathname
to exercise the established working foreground fallback, with fixture-configured
ControlMaster disabled. This forces separate authenticated sessions on Linux;
it is **not** native Windows execution or WAN latency. It also pays Unix optional
master-check overhead that Windows does not, so its absolute times must not be
represented as predicted Windows performance.

| Topology / workload | Before → after p50 ms | Authentications | Remote sessions |
| --- | ---: | ---: | ---: |
| Owned mux, 4 KiB upload | 475.857 → 392.443 | 1 → 1 | 8 → 6 |
| Owned mux, five uploads | 1703.772 → 1275.915 | 1 → 1 | 40 → 30 |
| Native clients, 1,795-byte upload | 1207.039 → 914.045 | 8 → 6 | 8 → 6 |
| Native clients, five uploads | 5909.730 → 4392.386 | 40 → 30 | 40 → 30 |

All 12 matched pairs improved in this small diagnostic window. Peer builds and
process probes were held, but one peer reported that a documentation write may
have overlapped the window. Treat timings as paired diagnostic observations,
not a strictly filesystem-write-isolated final performance acceptance. The
structural session counts and verified outputs do not depend on that timing
qualification. Full reports remain under
`.temp/ssh-windows-latency/paired-{owned,native}.json` while retained.

Owned-topology reproduction:

```sh
python3 benches/ssh_v065.py BEFORE .temp/paired.json \
  --after AFTER --samples 3 --case upload --case five-files
```

For the native topology, import the same runner, subclass `Fixture` so its
`parent` is `parent / ('native-client-' + 'x' * 85)`, substitute that fixture,
and set `PAYLOAD = b'x' * 1795` before calling `main` with the same arguments.
The server's accepted-authentication count must equal remote sessions; a short
workspace must instead demonstrate the owned-mux control. These steps preserve
the actual source/protocol code and do not add a production no-reuse knob.

### Focused acceptance

- `tests/ssh_cohort_v065.py` passes 12 actual CLI journeys: coherent cohorts,
  late collision preserving the complete old destination, alias admission,
  empty/literal behavior, same-unit and later-REPL whole-cwd capture, unrelated
  socket visibility, and exact fractional upload timestamps with phase/timer/
  parent ancestry checks in owned and native-client topologies.
- Three actual-helper journal tests pass, including seven ordinary-file normal/
  partial-backup/process-kill cuts and repeat recovery, plus the existing
  readonly-directory/partial-GC crash fixture. Independent reviewer probes
  additionally retain external old hardlinks through the ordinary-file cuts.
- The 31 transport helper tests pass, including portable umask/read-only
  normalization, nanoseconds, native executable preservation without a
  destination-filesystem probe, and cancellation before client contact. Existing
  SSH context tests pass nine cases. Empty-admission recovery and structured
  telemetry pass seven CLI tests.
- Local formatting and strict lib/bin Clippy pass. The Windows GNU target
  lib/bin compile check passes using installed Rust target, Clang/llvm-ar and
  Debian mingw-w64 12.0.0-5 headers extracted under project `.cache/mingw-w64`.
  No native Windows runtime/UI/performance claim follows from that check.

The remaining large transport task is native Windows connection/session reuse,
not a larger byte buffer. Win32-OpenSSH still does not provide the Unix
ControlMaster mechanism. A retained native SSH exec channel with bounded framed
commands/data can preserve configured aliases/agent/host-key behavior without
adding a second credential cache or replacing SSH cryptography. Targeted
file/directory persistence also remains separate work; global syncfs is not
silently weakened here.
