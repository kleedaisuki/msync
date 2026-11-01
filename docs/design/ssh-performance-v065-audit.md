# SSH orchestration audit for v0.6.5

## Decision

The first useful optimization target is redundant **process/helper orchestration**,
not compression, buffer enlargement or a second SSH implementation. Unix already
owns/reuses authenticated OpenSSH control masters. A finite upload nevertheless
runs a generic remote-copy protocol inside an already allocated publication
journal. Eliminating that redundant staging layer has a concrete opportunity to
remove three client invocations and a remote payload copy without weakening the
statement-publication/recovery boundary.

This is a source-path audit of baseline `d501fd2`, not a measured performance
claim. The SSH lead owns real authenticated baseline/after measurements and the
final implementation decision. The user's observation that network IO is not the
bottleneck is consistent with these paths and does not require a throughput-only
experiment to be taken seriously.

## Existing ownership and mechanism

- `src/runtime/ssh.rs::SshContext::ensure_unix` recognizes an existing master or
  owns a private `ssh -M -N` child/socket. `configure_client` passes that socket to
  metadata commands and SCP, setting `BatchMode=yes` only after authentication.
  Owned masters are closed with their context; user-owned masters are not killed.
- Warm owned-master admission still launches `ssh -O check`. Initial creation
  repeatedly checks the private master and sleeps 50ms after unsuccessful checks.
  `check_master` polls child completion every 10ms.
- `src/runtime.rs::run_process` launches a stdout-drain thread and stderr-drain
  thread, then polls child completion with a 10ms sleep. A successful remote
  metadata helper that finishes just after a poll can therefore spend nearly one
  interval waiting locally. This is an orchestration floor, not a proven fixed
  10ms cost for every invocation.
- `src/runtime/inspection.rs::inspect_remote_batch` already groups metadata paths
  into bounded 8KiB commands. There is no case for replacing that existing batch
  with one SSH client per leaf. Its trace records `client.invocations` and paths.
- `src/execution/remote.rs::execute` reclassifies the destination (and a remote
  source) immediately before choosing finite publication versus stream ownership.
  This is distinct from earlier preparation. Any attempt to reuse observations
  must preserve live endpoint/stream classification rather than treating old
  filesystem observations as permanent truth.

## Client map

Counts below are process launches of `ssh`/`scp` directly requested by these
layers, not shell-internal `stat`, `find`, `dirname`, `sync` or SFTP subprocesses.
They exclude authentication, cold recovery and preparation unless stated.

| Operation / layer | Explicit transport clients | Code path |
| --- | ---: | --- |
| One remote metadata batch | 1 per bounded chunk | `runtime/inspection.rs::inspect_remote_batch` |
| `exists`, `delete`, or regex name enumeration | 1 per operation | `execution/control_fs.rs::remote` |
| Ordinary finite SSH download payload | 2 | `runtime.rs::receive`: `REMOTE_VALIDATE` receipt, then SCP |
| Finite regex download selecting N children | `1 + 2N` | `receive_selected`: one `REMOTE_LIST`; each selected child uses `receive` |
| Generic local-to-SSH send | 4 | `send`: remote `/tmp` allocation, SCP, `REMOTE_COPY`, remote cleanup |
| Finite journaled local-to-SSH upload payload/publication | 7 | `Spec::run_inner`: ALLOCATE + generic send (4) + PUBLISH + SETTLE |
| Tiny sole ordinary-file local-to-SSH statement including normal admission | approximately 9, plus authentication clients | one preparation batch + destination reclassification + the seven above; no extra WSL-origin conversion |

`Spec::run_inner` already owns the allocated same-parent remote journal, its local
pending-recovery record and `journal/payload` destination. Sending into a second
`/tmp/msync.*` directory and copying that data into the journal is redundant for
this private, already-owned destination. Normal user-destination sends must not
be globally rewritten as if they had that ownership proof.

For shell-helper CPU diagnosis, separately count/attribute the server's external
`dirname`, `stat`, `find`, `cp`, `mv`, `chmod` and `sync` calls. Transport counts
alone do not establish that their local polling dominates whole operations.

## Prioritized implementation choices

1. Add an owned-private SSH staging operation or equivalent specialization at the
   journal owner, using mature SCP/SFTP for bytes. Reuse the already allocated
   journal and preserve publication/report accounting. Do not expose this bypass
   for arbitrary public targets.
2. Preserve the current uploaded-source metadata policy. The generic copy path
   uses `RemoteSource::uploaded` / `SourcePermissions::Portable`: remote filesystem
   type is not proof of source permission provenance. Direct `scp -p` by itself
   is **not** semantically equivalent to the existing normalization, especially
   for Windows-origin upload modes. Keep portable umask/readonly behavior and
   portable timestamps, including directory handling.
3. Measure the synchronous process wait floor before changing the waiter. Prefer
   an established event-driven process mechanism over shrinking a polling sleep.
   Preserve bounded stdout/stderr, cancellation ownership, native Windows process
   jobs, child joining and task-local SSH/auth context. A new background waiter
   that kills only by a possibly reused PID is not a safe simplification.
4. Examine duplicate metadata admission only after the first measured slice.
   A statement-scoped proof/observation is different from a cross-block cache;
   path existence and endpoint kind can change between blocks.

## Boundaries that must remain intact

- PUBLISH and independent SETTLE are not redundant calls. A killed SSH client does
  not prove that its remote shell has stopped. SETTLE takes the remote lock before
  deciding COMMITTED versus ABORTED and cleanup, even after caller cancellation.
- Remote intent, before-image, installed identity and commit receipt remain
  durable in their required order. Current shell helpers use GNU `sync -f` at
  several phases; it flushes the **containing filesystem**, not one file. This
  may amplify unrelated dirty-write latency but is an established correctness
  fallback, not permission to delete flushes in a speed patch.
- Local/remote recovery registries remain the authorities for interrupted owned
  state. No new daemon/database/global transaction manager is justified.
- SCP recursively follows symlinks according to upstream documentation. Existing
  source validation/name framing and no-follow publication policy cannot simply
  be removed because SCP is mature.
- Keep user SSH configuration, explicit config selection, host-key checks,
  interactive/native credential UI, and cancellation behavior. Do not disable
  security checks to produce lower loopback timings.

## Primary references inspected

- [OpenSSH ssh_config: ControlMaster/ControlPath](https://man.openbsd.org/ssh_config#ControlMaster)
  specifies sharing sessions over an existing connection and protecting control
  socket placement. This supports retaining existing OpenSSH ownership rather
  than replacing SSH with a home-grown transport.
- [OpenSSH ssh: -O and -S](https://man.openbsd.org/ssh#O) documents control-master
  checks and socket selection. A multiplexed command still has a client process
  and session-channel operation.
- [OpenSSH scp](https://man.openbsd.org/scp) documents modern SFTP transport,
  `-p` timestamps/modes and recursive symlink traversal. These details constrain
  direct-private-staging equivalence and validation requirements.
- [GNU coreutils sync implementation](https://github.com/coreutils/coreutils/blob/master/src/sync.c)
  identifies `-f` as filesystem synchronization and dispatches to `syncfs` where
  supported. Existing project analysis is in
  [remote-publication-v5.md](remote-publication-v5.md).

Official manual pages were inspected on 2026-10-31. The GNU standalone manual
endpoint timed out; the upstream implementation supplied the relevant evidence.
No performance ratio or native Windows/OpenSSH acceptance is implied by this
source audit.

## Native Windows tiny-upload follow-up (2026-11-01)

### User workload and causal distinction

The reported retained-REPL operation is `cmd -> (ssh://forge)::cmd`: one file,
1,795 bytes, 35,399.5 ms. Manual SSH/copy is materially faster on the same Windows
side. This is a valid actual-workload observation; no local loopback result
supersedes it. Current source inspected for this follow-up is `964bc4f`.
The high-confidence source defect is **many independently authenticated native
Windows clients for one publication**, not proof that a specific network or disk
phase accounts for all 35 seconds. The existing accepted Linux optimization
measured eight upload authentications becoming one; that mechanism is not active
on Windows.

- `SshContext::with_foreground_reuse` is a no-op outside Unix.
- `SshContext::ensure_optional` returns `Ok(None)` outside Unix before admitting
  a session. Windows never inserts an owned/existing entry in `Sessions`.
- `configure_client` therefore finds no lease and supplies neither a control
  socket nor the post-admission `BatchMode=yes` setting.
- `configure_graphical` supplies `SSH_ASKPASS_REQUIRE=force` and contextual native
  UI, not a credential cache or authenticated connection. Password/key prompts
  may consequently recur per client. It honors an externally configured
  `SSH_ASKPASS` rather than replacing it.
- `runtime::send` still executes temporary allocation, SCP, generic remote copy,
  and temporary cleanup inside `Spec`'s allocation/publication/settlement. The
  client map above remains the relevant amplification model; fractional mtime
  restoration can add another helper. Actual count must come from the current
  path because planner/cohort choices affect admission.

Every independent client can repeat DNS/proxy establishment, SSH key exchange,
authentication, server session setup, and remote account shell startup. Repeated
short session cost can dominate 1.8 KiB of payload. Server `sync -f` waits and
local durable captures remain a separate explanation to discriminate rather
than conflating all pre/post-byte work with SSH bandwidth.

### Why a Windows ControlMaster flag is not the repair

The upstream [Win32-OpenSSH project scope](https://github.com/PowerShell/Win32-OpenSSH/wiki/Project-Scope)
explicitly excludes Client ControlMaster. The still-open upstream
[ControlMaster request](https://github.com/PowerShell/Win32-OpenSSH/issues/1328)
explains the missing Unix-domain ancillary-data mechanism. Inspected on
2026-11-01, the active upstream `latestw_all`
[Win32 descriptor compatibility implementation](https://github.com/PowerShell/openssh-portable/blob/latestw_all/contrib/win32/win32compat/w32fd.c)
still rejects listening/binding its non-socket Unix-domain representation, and
[descriptor passing](https://github.com/PowerShell/openssh-portable/blob/latestw_all/monitor_fdpass.c)
retains unsupported-platform fallbacks. A parsed `ControlMaster` option or the
mere existence of Windows AF_UNIX support does not establish working OpenSSH
multiplexing. Do not replace `cfg(unix)` with `cfg(any(unix, windows))` or tell the
user to set `ControlPersist` as if that delivered reuse.

### Client identity and wait ownership

`runtime::ssh_client` independently resolves bare `ssh` and `scp` through process
search rules and optionally passes `MSYNC_SSH_CONFIG` through `-F`. It does not
pin a known client pair or guarantee that PowerShell's manual command selected
the same executable. A Windows diagnostic should show `Get-Command ssh,scp -All`,
`where.exe ssh`, `where.exe scp`, `ssh -V`, and the active explicit-config setting;
then compare the same endpoint and executable family, not change the user's SSH
configuration. Git/MSYS, Cygwin, native Win32 and WSL clients have different
configuration/path/agent contexts. Auto-switching to another family breaks the
existing opaque `Host` alias and authentication contract.

`runtime::run_cancellable` already records `transport_process` spans, requested
program, PID, wait strategy, byte counts and process-open/exit events. Windows
`ChildWait` uses `WaitForSingleObject` on the owned process handle. The historical
10 ms polling-floor note above describes the old baseline, not a plausible
35-second current timer. Windows `ProcessJob` enrolls the suspended client and
its descendants before resume and closes the owned tree after completion;
retain this cancellation/helper-cleanup ownership in any transport change.

### Practical implementation order

| Choice | Benefit for this workload | Compatibility / implementation consequence |
| --- | --- | --- |
| Upload directly into already owned journal staging | Removes three independent clients and one remote payload copy | Reuse statement owner; preserve Windows portable permissions, directory timestamps, fractional mtimes and receipt/report semantics |
| Keep a native `ssh -T` subprocess for a complete finite statement, with framed commands and payload | Amortizes native connection/auth over publication phases without Win32 ControlMaster | Requires duplex framing, exact byte ownership, stderr draining, cancellation and interrupted-statement recovery; remote shell/helper is session-scoped, not a daemon |
| Keep native subprocess for repeated helper requests | Amortizes control operations across statements | Session health and request serialization become explicit; selected endpoint admission and real-process crash/reconnect journeys are mandatory |
| Swap in PuTTY/Plink sharing | Mature Windows named-pipe sharing exists | Not transparent: saved sessions, host keys, key/agent formats, SSH config and client CLI differ; inappropriate as an automatic replacement |
| Require WSL/Cygwin OpenSSH | Unix multiplexing can be available there | Adds an installation/runtime boundary and changes paths/config/agents; not a native-Windows product fix |
| Replace OpenSSH with an SSH library | Native shared channels possible | Highest integration cost: configuration, proxy/jump behavior, agents/hardware keys and native auth UX must be reconstructed; no evidence yet justifies this first |

The retained-native-process design uses an ordinary SSH exec channel and the
client's standard streams; it is application framing, not a replacement SSH
cryptographic implementation. [RFC 4254](https://www.rfc-editor.org/rfc/rfc4254)
distinguishes channel lifetime from transport/authentication lifetime. Keep local
and remote existing journal owners as the only publication/recovery authorities;
losing a connection must leave their established recovery contract intact.
[PuTTY's upstream sharing implementation](https://git.tartarus.org/?p=simon/putty.git;a=blob;f=sshshare.c;hb=HEAD)
is evidence that Windows connection sharing is feasible with different client
architecture, not evidence that OpenSSH accepts a compatible control socket.

The immediate discriminating journey is a traced 1.8 KiB upload plus a same-client
manual one-shot SSH control, with per-phase duration/count attribution. Repeat the
upload in one REPL to distinguish repeated-auth overhead from cold initialization.
Validate the resulting bytes and metadata, then interrupt during staging and
publication and restart against the same recovery registry. Do not strip
settlement, durability or error diagnostics merely to match plain SCP latency.
