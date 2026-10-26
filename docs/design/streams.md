# Bounded asynchronous stream transfers

## Endpoint model and API

`streams::transfer(&CopyOp, buffer_bytes, CancellationToken)` runs one one-way
flow and returns a byte count plus `StreamEnd::{SourceClosed, DestinationClosed,
Cancelled}`. The scheduler owns task admission, dependency release, global
cancellation and trace correlation; this module owns opened endpoint handles and
its relay buffers. A completed flow never cancels another flow.

| Endpoint | Native transport |
| --- | --- |
| Regular file | Tokio file I/O, with internal file buffer size capped to the configured relay size |
| `tcp://host:port` endpoint | Active Tokio TCP connection on either side; use `wire::/` as the qualified endpoint operand |
| Windows `\\.\pipe\name` | IOCP-backed named-pipe client with source read-only or destination write-only access |
| Unix FIFO | `O_NONBLOCK` file descriptor registered with Tokio `AsyncFd` |
| Unix stream socket | Active Tokio Unix-domain connection |
| WSL/SSH regular file, FIFO or Unix socket | Native client child process carrying a bounded, nonblocking Python 3 relay |
| HTTP(S) source | Controlled native curl stdout, sharing runtime URL/origin/protocol validation; HTTP is read-only |

At least one side is a stream endpoint. A directory is never interpreted as a
byte stream. Endpoint shapes and native path ancestors are validated before
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
- Native FIFO destinations inspect non-consuming `POLLERR/POLLHUP` on an owned
  duplicate descriptor, also at 20 ms intervals. No fake data is injected.
- Broken pipe, connection reset/abort and disconnected I/O are normal flow ends;
  unrelated I/O or helper failures remain located runtime errors.

The cancellation token is selected while connecting, waiting, reading, writing
and finalizing. Dropping a flow releases native handles and kills its owned
client process, rather than waiting for unrelated producers to send another byte.
This directly fixes the reproduced idle-source/closed-destination lifetime leak.

## Buffers, backpressure and resource limits

The native relay owns one `buffer_bytes` data buffer and performs partial writes
until each read chunk is consumed. It never reads the entire source into memory
or launches a task per chunk. Backpressure therefore reaches the source rather
than growing an application queue. Tokio regular-file buffers are explicitly
capped to the configured size as well.

Each WSL/SSH endpoint adds at most one remote relay data buffer of the same size;
the scheduler reserves `(1 + number_of_remote_endpoints) * buffer_bytes` declared
relay capacity. Kernel socket/pipe buffers, interpreter/runtime/process RSS,
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

A bounded stderr reader recognizes a ready marker before allowing a remote
source to cause destination mutation. Helpers carry only payload on stdout for
readers and stdin for writers. A source keeps its unused input channel open as
its lifetime signal; a writer watches its unused output channel for client
disconnect while opening or writing a FIFO/socket. The helper stops without an
unknown PID kill, persistent daemon or detached shell worker. Its nonzero status
is checked at EOF so authentication, missing interpreter and path failures cannot
silently become an empty successful stream. HTTP uses the same owned-child/status
discipline without a Python handshake.

No telemetry guard crosses an await: those guards are thread-bound. The scheduler
provides explicit task context and records structural status/byte counters rather
than paths or source contents.

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
