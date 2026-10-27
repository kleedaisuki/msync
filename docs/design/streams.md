# Bounded asynchronous stream transfers

## Endpoint model and API

`io::transfer(&CopyOp, buffer_bytes, CancellationToken)` is the common one-way
byte-flow entry. `io::transfer_known` accepts the planner's inspected endpoint
types and explicit trace context, avoiding metadata re-probes. Ordinary files are
finite stream resources, not a separate meaning of `->`. `FlowReport` returns a
byte count plus `FlowEnd::{SourceClosed, DestinationClosed, Cancelled}`; the old
`streams::StreamReport`/`StreamEnd` names are compatible aliases. Directories are
structural collections of child file flows, never opaque directory byte streams.
The scheduler owns task admission, dependency release, global
cancellation and trace correlation; this module owns opened endpoint handles and
its relay buffers. A completed flow never cancels another flow.

| Endpoint | Native transport |
| --- | --- |
| Regular file | Kernel-assisted finite copy, or a bounded file/IPC relay; guarded per-file staging and publication |
| `tcp://host:port` endpoint | Active Tokio TCP connection on either side; use `wire::/` as the qualified endpoint operand |
| Windows `\\.\pipe\name` | IOCP-backed named-pipe client with source read-only or destination write-only access |
| Unix FIFO | `O_NONBLOCK` file descriptor registered with Tokio `AsyncFd` |
| Unix stream socket | Active Tokio Unix-domain connection |
| WSL/SSH regular file, FIFO or Unix socket | Native client child process carrying a bounded, nonblocking Python 3 relay |
| HTTP(S) source | Controlled native curl stdout, sharing runtime URL/origin/protocol validation; HTTP is read-only |

The common contract supports regular-file → regular-file flows as well as IPC.
A directory is never interpreted as a byte stream. Endpoint shapes and native path ancestors are validated before
opening a destination. Native named-pipe paths are deliberately recognized before
ordinary filesystem metadata checks. Exact regular-file destinations overwrite;
a trailing `/` retains container semantics for sources with a basename and uses
atomic no-clobber creation. An unnamed TCP source needs an exact destination
filename rather than a directory container.

Source readers and destination writers open separately and are independently
owned. No process-wide environment, working directory, detached task or unbounded
queue is created here. Pipe-busy/first-reader rendezvous waits yield asynchronously
and can be cancelled. A missing Windows pipe is an error, while an existing busy
instance is retried without allocating another flow. FIFO readers wait for the
first writer rather than mistaking the pre-connection state for EOF.
Source TCP/Unix sockets retain both transport halves until the flow ends; merely
opening a reader does not send a premature FIN to its producer.

## Lifetime and close semantics

The DSL is one-way, not a general bidirectional application protocol:

- Source EOF finishes the flow after flushing and shutting down its destination.
- Destination closure finishes that flow even when its source is idle.
- TCP/Unix destination monitors retain the read half. Peer EOF/FIN, including a
  sending-half shutdown, is an **explicit logical end-of-flow**. FIN is not claimed
  to prove that a peer also closed its receive half. A receiver that wishes to
  keep this DSL flow alive must not half-close its sending side prematurely.
- Reverse-direction bytes on a destination socket are outside this arrow's
  data contract; its close watcher drains/discards them with a fixed one-byte
  probe buffer rather than forwarding them or accumulating a response body.
- Windows write-only named-pipe destinations use a duplicated owned handle and
  `NtQueryInformationFile(FilePipeLocalInformation).NamedPipeState`, polled at
  20 ms intervals. No probing write, extra connection, or read permission is
  needed. `GetNamedPipeInfo` alone was experimentally insufficient: it succeeds
  after a server handle closes because it exposes attributes but not pipe state.
- Linux FIFO destinations register their owned duplicate descriptor for Tokio
  error readiness (`EPOLLERR` when the last reader disconnects), with no polling
  timer or probing data. Other Unix platforms retain the bounded 20 ms fallback.
- Destination broken pipe/reset/disconnection is a normal peer-close outcome.
  Source reset/abort is **not EOF**: it is an I/O failure and must not publish an
  incomplete file. Only an actual successful zero-byte read authorizes completion.

The cancellation token is selected while connecting, waiting, reading, writing
and finalizing. Dropping a flow releases native handles and kills its owned
client process, rather than waiting for unrelated producers to send another byte.
This directly fixes the reproduced idle-source/closed-destination lifetime leak.

### One publication policy for file sinks

Both finite copies and file sinks fed by TCP, FIFO, sockets or HTTP write an
exclusively owned sibling stage. A normal EOF flushes data, drains physical file
I/O, closes the file and atomically publishes it. Cancellation, source failure
and future drop discard the unpublished stage and preserve an old target (or
leave a new target absent). Prior completed files in a directory flow remain;
this is a per-file transaction, not whole-directory rollback. Existing target
write capability is checked without truncation, so staging does not bypass
read-only/ACL failure semantics. Ordinary copies preserve source permissions;
stream-fed outputs retain existing target permissions where supported.

`ManagedFile`/`FileFinish` encode physical completion ownership. Token cancellation
awaits Tokio's outstanding file I/O before discarding the stage. If a future is
aborted while file I/O is pending, one owned cleanup future retains the file and
stage until `into_std().await` drains the operation; no cleanup is spawned per
chunk. Native kernel workers likewise own their submitted handles and buffers
until completion/cancellation drain. Successful publication consumes the stage
name; its destructor never removes the target.

## Buffers, backpressure and resource limits

The native relay owns one `buffer_bytes` data buffer and performs partial writes
until each read chunk is consumed. It never reads the entire source into memory
or launches a task per chunk. Backpressure therefore reaches the source rather
than growing an application queue. Tokio regular-file buffers are explicitly
capped to the configured size as well.

Each WSL/SSH endpoint adds at most one remote relay data buffer of the same size;
each remote destination additionally owns one local bounded DATA-frame buffer.
The scheduler reserves `(1 + remote_endpoints + remote_destination_frame) *
buffer_bytes` declared payload capacity: local → remote uses 3 buffers and
remote → remote uses 4. Fixed eight-byte framing headers, kernel socket/pipe
buffers, interpreter/runtime/process RSS,
Tokio file internals, and fixed bounded close-watch/error-report overhead are not
represented as exact RSS by the memory-limit option. Helpers have bounded stderr
capture (8 KiB recorded output and an 8 KiB partial line) and a finite number of
tasks/processes per admitted flow. Stream byte counts mean bytes accepted by this
relay's write boundary, not filesystem crash durability or an application-level
remote acknowledgement.

## Remote helper and client ownership

Remote streaming requires installed Python 3.7+ in addition to the existing native
WSL/SSH clients. It is optional for ordinary copies. The embedded helper has full
Python function/field annotations and only uses the standard library. It checks
path ancestors and file type, uses nonblocking descriptors/socket connections
plus `select.poll`, and does not create a remote script file or staging tree.
Arguments pass through the runtime's existing shell-quoting and SSH `-F` policy;
the Python program is a fixed argument, never interpolated with an untrusted path.

A bounded stderr reader recognizes a typed file/stream ready marker before allowing a remote
source to cause destination mutation. Helpers carry only payload on stdout for
readers. Writer stdin uses exact, bounded DATA frames (eight-byte big-endian
length plus payload) and an explicit zero-length FINISH frame. User payload is
never escaped or confused with a magic marker. Transport EOF or a truncated or
oversized frame is an error, not permission to publish a file. A source keeps its unused input channel open as
its lifetime signal; a writer watches its unused output channel for client
disconnect while opening or writing a FIFO/socket. The helper stops without an
unknown PID kill, persistent daemon or detached shell worker. Its nonzero status
is checked at EOF so authentication, missing interpreter and path failures cannot
silently become an empty successful stream. HTTP uses the same owned-child/status
discipline without a Python handshake.

Remote ordinary-file writers use a same-directory `.msync-flow-*.part` stage,
not in-place truncation. Only verified normal source EOF plus successful source
helper exit reaches `AsyncWrite::shutdown`, which submits FINISH. The helper
closes its data file and atomically replaces/links the target, then emits a
COMMITTED acknowledgement. Dropping/cancelling a flow before FINISH cannot
mistake closed stdin for successful completion. Once regular-file finalization
starts, cancellation is shielded only through the bounded 30-second commit/ack
fence; lost acknowledgement is reported as **commit outcome unknown**, with
advice to verify the destination. An acknowledgement proves publication even if
the transport subsequently fails. FIFO/socket finalization is never shielded:
even an empty FINISH waiting for a FIFO reader remains cancellable. The ready
metadata distinguishes these cases before choosing a commit policy.

The final transport shutdown is not itself the publication oracle. If shutdown
fails after FINISH authorization, the caller still drains the helper's bounded
commit/ack fence. A COMMITTED acknowledgement proves publication even when that
shutdown reported BrokenPipe and the transport subsequently exits nonzero. A
regression drives both failures with an actual child stderr acknowledgement;
it must report SourceClosed, not advise retrying an already committed file.
Both finalization branches share `RemoteProcess::wait_commit`: after a bounded
wait it rechecks COMMITTED even if child waiting failed, the client never exited,
or stderr draining timed out. A known commit remains successful and records the
later transport/cleanup failure separately. The owned client is killed on a
failed wait, with Tokio's child owner retaining its kill/reap-on-drop policy.
Focused real-child tests emit an acknowledgement and then hold stdout/stderr
open indefinitely. They synchronize on observed stderr markers before applying
a short injected deadline, distinguish acknowledged from unknown publication,
and require physical stdout EOF after cleanup. An otherwise identical child
without COMMITTED must still produce an unknown-outcome diagnostic.

The destination close-watch future is pinned once per relay rather than rebuilt
per chunk. This preserves the Windows pipe watcher's 20 ms timer instead of
querying kernel state on every buffered read. The data loop allocates one buffer,
handles partial writes and keeps cancellation/peer closure live during reads,
writes and frame flushes.

Thread-local guards never cross awaits. Send-safe linked spans carry explicit
task context through capability selection and native workers. Events record
source/destination/stage paths, engine choice, typed OS errors and cause chains,
accepted/committed/discarded progress and commit/cleanup state; source file
contents are not logged. A dropped active span is incomplete/cancelled rather
than falsely successful.

## Kernel capability dispatch

The file-to-TCP path opens its source and connection once, then offers those
owned resources to the platform adapter. Linux uses bounded `sendfile`; Windows
uses cancellable `TransmitFile` with owned handles and completion drain. Windows
chooses that path only at a measured remaining size of at least 1 MiB; smaller
files keep the buffered path. The forced low-level capability API remains
available for reproducible backend tests. Unsupported capability results include
an accepted prefix count, so fallback seeks the existing file to that position
and reuses the same socket—never reconnecting or duplicating bytes.

Finite local copies use bounded `copy_file_range` on Linux and cancellation-aware
`CopyFileExW` on Windows, with the same staged-file publication policy. Every
failure retains typed progress in `CopyFailure`, rather than parsing English
messages to determine cancellation or discarded bytes. See the platform I/O
notes for capability probes, measured thresholds and rejected I/O-ring paths;
this note does not claim that a newer queue API is automatically faster.

## Real verification

On 2026-10-26, `cargo test --test streams` passed 10 real Windows tests covering
binary TCP/file transfers, backpressure, independent flow completion/cancellation,
source/destination EOF, explicit TCP half-close behavior, directional native
named pipes, idle named-pipe destination disconnection, and HTTP-body-to-socket
streaming. Strict Clippy passed for the library and stream test target.

The Linux target was actually built with WSL Rust 1.97.1. All 10 Unix stream
tests passed, including first-writer FIFO rendezvous, FIFO idle-destination
disconnect, and Unix-domain socket binary transfer. The NTFS-backed WSL project
mount rejects `mkfifo`, so the test command uses a **private mount namespace** to
put tmpfs at the project's `.temp` path; no temporary file is moved outside the
project, and other processes keep their original mount view:

```sh
cd /mnt/d/Code/msync
unshare --mount --fork /bin/bash -c '
  set -eu
  mount --make-rprivate /
  mount -t tmpfs -o size=128m,mode=1777 msync-stream-tests .temp
  CARGO_TARGET_DIR=target/wsl-streams cargo test --test streams -- --nocapture
'
```

This wrapper needs WSL root/mount-namespace support. On a native Unix filesystem,
the ordinary test command needs no mount wrapper. The initial WSL index/download
probe took roughly 16 minutes; subsequent actual verification first fetched Linux
dependencies with native Windows Cargo and used its registry cache via explicit
per-command `CARGO_HOME`/offline variables. No Cargo configuration file was modified.

An additional explicitly invoked Windows test streams a 64 KiB WSL regular file
directly into a native TCP destination through the same helper and verifies every
byte. The helper starts its stderr ready marker on a fresh line, keeping WSL
startup diagnostics separate from the metadata/ready handshake.

The independently owned isolated SSH fixture also passed a real Windows SSH to
WSL chroot journey with 256 KiB binary FIFO and Unix-socket transfers **in both
directions**, EOF, and idle-flow cancellation followed by a zero-surviving-helper
process audit. Its optional Python runtime is copied only into the private test
tmpfs. See `runtime.md` and `tests/ssh.rs` for that reproducible environment.

## Primary engineering references

- [Tokio Windows named-pipe clients](https://docs.rs/tokio/latest/tokio/net/windows/named_pipe/struct.ClientOptions.html): directional access and pipe-busy retry behavior.
- [Tokio AsyncFd](https://docs.rs/tokio/latest/tokio/io/unix/struct.AsyncFd.html): readiness-driven nonblocking I/O. Tokio 1.54's explicit unsafe `register` contract is satisfied by a privately owned stable `File` descriptor, never exposed or replaced while registered.
- [Microsoft FILE_PIPE_LOCAL_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_pipe_local_information): connected, closing and disconnected pipe states.
- [Microsoft synchronous query contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ioisoperationsynchronous): information queries are synchronous even on asynchronously opened file objects. The local state query therefore uses stack outputs and no speculative pending-I/O wait or allocation machinery.
- [Microsoft named-pipe client access](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-client): outbound/inbound server direction determines read-only/write-only client access.
- [Python poll](https://docs.python.org/3/library/select.html#polling-objects): bounded readiness and hangup/error observation without blocking FIFO opens.

These tests establish actual platform behavior for the implementation host, not
a promise of hostile concurrent path mutation safety or every network protocol's
duplex semantics. Those are distinct contracts rather than hidden special cases.
