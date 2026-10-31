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
