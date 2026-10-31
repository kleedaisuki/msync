# Linux kernel-assisted byte flows

## Workload, ownership and dispatch

The baseline for this round is commit `d96724d` (msync 0.2.0). The useful
workloads are ordinary configuration-file copies, larger regular-file-to-TCP
flows, and concurrent streams whose reactor must remain responsive. Linux is
executed and measured through WSL 2, not inferred from Windows test results.

`src/io/linux.rs` implements two narrowly selected mechanisms:

- Regular file to an already connected TCP destination: `sendfile`, with an
  explicit input offset and at most 1 MiB per kernel call. It avoids a userspace
  payload bounce buffer. This is not a claim that the receiver or every filesystem
  performs no internal copy.
- Regular file to an exclusively owned unpublished stage: bounded
  `copy_file_range`, followed by a same-handle buffered fallback if that operation
  is unavailable. Atomic publication, permissions and stage cleanup belong to
  the common I/O/runtime layer, not this backend.

The borrowed sendfile source cursor does not advance. A declined kernel path
returns its accepted-prefix count, so the common dispatcher seeks to the original
position plus that count and reuses the same connection. Finite range copies
advance their actual descriptors, making a later fallback resume at their current
cursors. No fallback reconnects a socket or rereads a transferred prefix.

`EINVAL`, `ENOSYS`, `EOPNOTSUPP`, `EXDEV` and syscall-level `EPERM` allow the
ordinary read/write fallback. The latter matters under syscall filtering; normal
file-access permission checks still apply to opened input/output handles.
Other failures retain their `io::Error` cause and the accepted-byte counter.
Same-inode finite copies fail before truncation. Metadata size zero is not used
as a proof of EOF: procfs-like regular virtual files are read through the fallback.

## Reactor responsiveness and interruption

Socket writes are nonblocking and readiness driven. A sendfile call may still
fault regular-file pages and block despite a nonblocking socket. The production
path therefore uses one bounded blocking-pool operation at a time per admitted
flow. Source/socket descriptor clones are owned by that operation, so dropping
an awaiting future cannot invalidate descriptors in an in-flight syscall.

A separate `AsyncFd` registration owns the write-side socket clone. Its readiness
guard is acquired before submitting a worker operation. `WouldBlock` clears that
specific event generation rather than clearing a possibly newer event after a
worker wakes; this avoids the lost-edge race from an after-the-fact
`TcpStream::try_io` call. The original connection remains available to the
one-way peer-close watcher and to the buffered fallback.

Cancellation is checked while waiting and between bounded kernel operations;
an already issued syscall is drained before reporting its exact completed prefix.
It is not falsely described as an interruptible storage syscall. A permanently
stuck filesystem operation remains an OS-level limitation. Other ready flows can
continue on the reactor while the affected flow awaits its worker.

Each hot-file chunk yields cooperatively. EOF emits the TCP write-half shutdown
after queued payload; destination FIN/error follows the existing one-way flow
policy. Linked telemetry spans carry explicit context across awaits, distinguish
kernel selection/fallback/cancellation, retain per-completed-chunk byte progress,
and record typed OS errors before converting them into user diagnostics.

Linux sendfile retains references to source pages after queueing. Those pages must
not be modified until the receiver consumes them. Local atomic stage/rename
publication preserves the old source inode for the application's later local
writes; arbitrary external in-place mutation is outside the filesystem model.
Cross-transport writers must retain the same publication discipline rather than
assuming a queued send is a remote-application acknowledgement.

## Actual io_uring and vectored-I/O investigation

The host was Linux `6.18.33.2-microsoft-standard-WSL2`, Rust 1.97.1, with installed
liburing development files. `/proc/sys/kernel/io_uring_disabled` was `0`.
An actual `io_uring_setup`, opcode registration probe and submitted/completed NOP
succeeded: feature mask `0x3ffff`; READV, WRITEV, SPLICE and SEND_ZC reported
supported. Capability discovery alone is not used as a performance argument.

`benches/linux_io_probe.c` performs actual verified copies using read/write,
four-vector readv/writev, copy_file_range, and io_uring readv/writev. The ring
prototype is deliberately a bounded depth-one per-flow experiment, not an
unimplemented claim about a deep shared submission pipeline. Each request is
completed before its buffer or vector storage is reused. All output is compared
byte-for-byte before accepting a sample.

Build and run:

```sh
gcc -O2 -std=c11 -Wall -Wextra -Werror -pthread benches/linux_io_probe.c -luring \
  -o target/perf-probe/linux_io_probe
mkdir -p .temp/perf-v3/uring-copy-drvfs
taskset -c 0 target/perf-probe/linux_io_probe .temp/perf-v3/uring-copy-drvfs 16
```

For the Linux **memory-filesystem** control, root enters a private mount namespace,
makes propagation private and mounts tmpfs at the repository's `.temp` before
creating the work directory. The executable remains in `target/perf-probe` and
is not hidden by that mount. No distribution configuration is changed. This
initial tmpfs control does **not** measure the distribution's native ext4 disk.

Each mechanism had one warmup and seven timed samples, a 64 KiB data window,
warm source/cache, no `fsync`, and no claim of crash durability. Medians from the
initial controlled run on 2026-10-27:

| Mechanism | 64 MiB tmpfs elapsed / process CPU (ms) | 16 MiB DrvFs elapsed / process CPU (ms) |
| --- | ---: | ---: |
| read/write | 17.143 / 17.210 | 347.101 / 22.107 |
| readv/writev, four contiguous vectors | 17.321 / 17.390 | 307.513 / 20.041 |
| copy_file_range, 1 MiB kernel chunks | 16.312 / 16.338 | 368.517 / 26.259 |
| io_uring readv/writev, depth one | 21.582 / 21.655 | 362.008 / 25.819 |

Median ring setup was 0.038 ms on the tmpfs run and 0.047 ms on the DrvFs run.
The filesystem/WSL boundary dominates the latter. These fixed-order mechanism
probes are not statistically strong before/after application speedup claims:
in particular the vectored DrvFs difference needs paired reruns before adoption.
They do establish working ring I/O with correct bytes, and no benefit from a
per-flow depth-one ring in the measured sequential-copy model. The implementation
keeps Tokio's shared epoll driver and kernel-copy capabilities instead of adding
a second mandatory runtime, per-flow ring setup, pinned buffers and cancellation
ownership machinery. A deep shared ring or hardware-specific zero-copy receive
workload would be a different investigation, not ruled out by this result.

## Native ext4, cache state and bounded queue-depth controls

The user correctly challenged extrapolation from WSL-mounted Windows storage.
WSL's Linux tools accessing `/mnt/d` cross a different filesystem boundary than
files stored in the distribution's ext4 VHDX. The initial tmpfs and DrvFs
experiments therefore cannot establish a native-ext4 storage conclusion.
[Microsoft's filesystem guidance](https://learn.microsoft.com/en-us/windows/wsl/filesystems)
likewise recommends keeping Linux-tool workloads on the Linux filesystem rather
than a mounted Windows drive. A mountpoint's spelling is not filesystem evidence:
the following experiment retains project-local fixture paths while checking the
actual opened source and destination descriptors with `fstatfs` and `fstat`.

`benches/linux_io_matrix.sh` is the reproducible, private-mount-namespace runner.
It makes mount propagation private, binds the distribution's `/tmp` into
`.temp/perf-v3/ext4`, and creates a uniquely owned fixture directory through that
project-local mountpoint. On the measured host, `findmnt` identifies
`/dev/sdd[/tmp]`, ext4; opened descriptors report magic `0xef53`, device `8:48`.
A separate private tmpfs provides the memory control. The unmodified project
drive is the DrvFs control: 9p, `aname=drvfs;path=D:\\`, `cache=0x5`.
Only named fixture files created by this run are removed. Mounts disappear when
the namespace exits; no distribution configuration, existing SSH configuration,
global cache state, or other `/tmp` files are modified.

```sh
# From the repository root inside WSL, compile outside the timed region.
gcc -O2 -std=c11 -Wall -Wextra -Werror -pthread \
  benches/linux_io_probe.c -luring -o target/perf-probe/linux_io_probe
sudo unshare --mount --fork bash benches/linux_io_matrix.sh selfcheck
sudo unshare --mount --fork bash benches/linux_io_matrix.sh pilot
sudo unshare --mount --fork bash benches/linux_io_matrix.sh full
```

The selfcheck covers all five mechanisms, queue depths 1/4/8/16, one/four output
flows, and both cache preparations with a 1 MiB payload: 80 fully verified cases.
The pilot uses 128 MiB and depth endpoints 1/16. The full matrix uses the same
**128 MiB aggregate payload** for every filesystem and flow count, three samples
per configuration, and CPU affinity 0–3. Native ext4 covers depths 1/4/8/16;
tmpfs and DrvFs cover depth endpoints 1/16, avoiding redundant slow-boundary
intermediate controls. The five mechanisms' order rotates deterministically by
sample and depth; it is not a fixed best-first traversal or randomized trial.
There is no separate discarded warmup: explicit cache preparation precedes each
recorded sample. The runner emits environment metadata and JSONL in project
`.temp/perf-v3/results`; the durable method lives in `benches`, not that
disposable directory.

| Mechanism | Bounded work and payload ownership |
| --- | --- |
| `buffered` | Exactly depth synchronous worker slots, each with one 64 KiB buffer and at most one active syscall; a shared allocator assigns disjoint chunks round-robin across output flows. |
| `vector` | Same worker concurrency and aggregate capacity, with four contiguous vectors per read/write. |
| `ring` | One completion-driven ring, depth stable 64 KiB slots; a slot alternates read/write and is reused only after its completion. |
| `fixed` | Same ring pipeline with registered buffers and fixed-buffer opcodes; registration/setup included. |
| `range` | Production-like one worker per output flow and up to 1 MiB per `copy_file_range`; **not** a depth-concurrency-matched control. Unsupported calls retain same-descriptor buffered continuation. |

Ring and buffered/vector controls match both the aggregate payload capacity
`depth * 64 KiB` and the maximum number of outstanding operations, not merely
allocation size. One/four flows split the same source into one/four disjoint
128/32 MiB regions and independent output files. This exposes both shared-inode
contention and available cross-file concurrency. Thread creation and ring setup
are included in `copy_ms`; a persistent executor amortizes setup differently, so
these one-shot mechanism results are not end-to-end application speedups.

Warm preparation explicitly reads the entire source. Guest-cold preparation
performs source `fsync` and per-file `POSIX_FADV_DONTNEED`; because that advice
is nonbinding, each sample records a `mincore` residency snapshot before timing.
The ext4 pilot observed all 32768 source pages resident for warm samples and
zero for every guest-cold sample. **This does not flush the Windows host's VHDX,
NTFS, SSD, or controller caches, and is not physical-device cold storage.**
tmpfs eviction advice does not turn memory into cold disk. Report those controls
as requested eviction plus observed residency, not a fabricated common cold tier.

Source generation, source `fsync`, cache preparation, output reset, and full
output verification occur outside `copy_ms`. Output `fsync` is measured
separately as `flush_ms`; production assignment does not implicitly promise this
durability barrier. Byte validation reconstructs the expected contents from
the **absolute source block offset**, including a unique 64-bit block identity
and its complement in every 64 KiB block. Repeated, skipped or misrouted aligned
blocks therefore cannot pass by matching a repeated payload pattern. Every
accepted sample validates the full output length and all bytes.

The ring prototype owns stable slot, vector and registered-buffer storage until
the corresponding completions finish, including short reads/writes. Its failure
path submits prepared requests before waiting for their cancellation completions;
a submission failure terminates the benchmark instead of hanging while awaiting
an SQE that never reached the kernel. This is benchmark lifetime discipline, not
a replacement for the production cancellation/atomic-publication tests.

### Why these controls matter

The upstream [io_uring API overview](https://man7.org/linux/man-pages/man7/io_uring.7.html)
explicitly notes that a depth-one example cannot demonstrate asynchronous
benefits. The corrected experiment therefore measures an actual outstanding
request pipeline. [Registered buffers](https://man7.org/linux/man-pages/man7/io_uring_registered_buffers.7.html)
amortize page mapping/pinning work; they do **not** by themselves make a
buffered file-to-file read/write copy zero-copy. Kernel-assisted
`copy_file_range` remains a distinct mechanism without that userspace payload
bounce.

The research study [Jasny et al., *io_uring for High-Performance DBMSs: When and
How to Use It*](https://arxiv.org/abs/2512.04859) similarly finds that architectural
parallelism and batching matter more than an interface-name substitution. Its
database/storage and high-speed network workloads differ from this sequential
copy utility; its reported ratios are not predictions for this program.
The question here is whether a bounded ring improves this application's
128 MiB ordinary-file assignments over its selected kernel-copy path, not
whether io_uring is universally faster or slower.

### Completed matched-storage results

On 2026-10-27, the full runner exited successfully after **480 fully verified
128 MiB copies**, comprising 160 configurations with three samples each. All
120 native-ext4 eviction samples observed zero resident source pages; all 120
ext4 warm samples observed 32768 pages. tmpfs retained all 32768 pages after
eviction advice. DrvFs reported zero Linux-resident source pages after both
pre-read and requested eviction; its host-side cache was not controlled.
No `copy_file_range` configuration used its buffered fallback.

The durable [configuration aggregates](io-linux-v3-matrix.csv) retain sample
counts, opened-descriptor filesystem/device identity, capacity, byte count,
residency ranges, copy/CPU min-median-max, flush statistics, ring setup and
verification outcomes. Cleaning `.temp` therefore does not remove the measured
findings. `copy_ms` and guest process CPU below are independent three-sample
medians, in milliseconds; they exclude output `fsync` and verification.

| Native ext4 workload | Range, 1 MiB per-flow / CPU | Buffered QD16 / CPU | Ring QD16 / CPU | Fixed-buffer ring QD16 / CPU |
| --- | ---: | ---: | ---: | ---: |
| Pre-read, one flow | 28.78 / 26.80 | 26.85 / 91.23 | 33.31 / 52.07 | 35.66 / 53.96 |
| Pre-read, four flows | 11.69 / 39.22 | 13.35 / 47.70 | 36.86 / 56.86 | 35.95 / 56.21 |
| Guest-cold, one flow | 65.91 / 43.18 | 63.21 / 111.68 | 66.84 / 68.19 | 66.76 / 67.48 |
| Guest-cold, four flows | 62.98 / 45.76 | 66.17 / 69.61 | 75.83 / 72.40 | 75.91 / 74.80 |

Queue depth genuinely changes the ring result: ext4 pre-read one-flow ring
elapsed/CPU falls from **120.56 / 105.81 ms at QD1** to **33.31 / 52.07 ms at
QD16**. The earlier depth-one experiment was insufficient for evaluating
pipeline benefits. Nevertheless the bounded ring did not produce a compelling
latency-and-CPU improvement over the selected kernel range-copy path on this
native-ext4 workload. Sixteen buffered workers slightly improve one-flow elapsed
time but consume much more CPU and do not justify that default concurrency.
Other queue depths are retained in the CSV; a roughly 4% guest-cold one-flow
QD8 ring median improvement over that cell's range control is smaller than its
observed range variation and comes with higher CPU. This is not a universal
ordering claim.

**DrvFs provides a counterexample to a blanket no-ring conclusion:**

| DrvFs one-flow QD16 workload | Range / CPU | Buffered / CPU | Vectored / CPU | Ring / CPU | Fixed ring / CPU |
| --- | ---: | ---: | ---: | ---: | ---: |
| Pre-read | 1839.33 / 146.09 | 1419.66 / 207.84 | 1617.00 / 212.45 | 971.69 / 219.81 | 1246.78 / 225.18 |
| Requested eviction | 2307.05 / 141.31 | 1562.48 / 228.79 | 1604.58 / 253.09 | 1469.10 / 249.06 | 1189.43 / 227.14 |

Concurrent buffered workers already reduce the single-flow boundary wait, but
the ring's pre-read median also beats the **concurrency-matched** threaded
control. That potential benefit must not be dismissed as only an unmatched
parallelism effect. It is not an established production latency guarantee:
the three pre-read ring samples span **963.93–2338.64 ms**, and vectored samples
span 1414.47–5602.44 ms. Four-flow pre-read range copying is already 918.88 ms,
versus ring 1060.23 ms. Guest process CPU does not account for the Windows
filesystem server's CPU, so these columns do not establish total-host efficiency.

The tmpfs pre-read one-flow QD16 range/buffered/ring/fixed medians are respectively
39.04/29.37/82.54/74.41 ms; four-flow values are 12.89/14.79/81.68/74.72 ms.
tmpfs is neither a substitute for disk nor a predictor of every API's ext4
behavior. This host's native-ext4 and DrvFs paths also need not share an identical
physical backing volume or host-cache state; do not attribute the entire
cross-filesystem timing gap to one isolated software layer.

Output flush medians for the native-ext4 range cells above are separately
187.51/207.50/201.87/240.21 ms. Some other pipeline configurations have second-
scale flush tails, retained in the CSV. These are not hidden inside transfer
time or claimed as production durability performance. The benchmark executable
is loaded from project `target/perf-probe` on DrvFs before fixture/cache
preparation; its process startup is not timed. This is an opened-data-filesystem
comparison, not a native-Linux process-loader benchmark or bare-metal disk study.

**Decision:** retain `copy_file_range` plus same-descriptor fallback for the
default finite-copy path, and `sendfile` for appropriate TCP flows. Keep the
measured large-single-flow DrvFs ring pipeline as a concrete specialization
candidate, not a rejected technique. Adopting it would require an actual msync
end-to-end win under configurable per-flow depth and aggregate payload limits,
stable completion ownership, cancellation/drain and staged-publication tests.
The QD16 experiment reserves 1 MiB of payload per admitted pipeline, versus
64 KiB at QD1; it cannot silently fit into an unchanged 64 KiB reservation.
Config-file latency and normal native storage do not justify imposing that
additional engine and reservation on every user.

### Native guest-cold reactor responsiveness

`io::linux::tests::benchmark_guest_cold_sendfile_reactor` adds a separate
end-to-end responsiveness measurement rather than inferring event-loop behavior
from copy throughput. It runs one current-thread Tokio reactor, a real TCP
receiver, the actual inline/selected-worker sendfile implementations, and an
independent 1 ms heartbeat. Five pairs alternate AB/BA order. Each of the ten
trials creates its own 128 MiB source inode, generates and syncs it outside the
timer, verifies ext-family `fstatfs` magic and **zero mincore-resident pages**,
then validates every received payload byte and normal EOF. The private bind's
`findmnt` identifies `/dev/sdd[/tmp]` as ext4; all trials used CPUs 0–3. All
source descriptors must be closed after the completed flow, checked through
actual `/proc/self/fd` links.

Fresh inodes are important experimental state ownership, not a cold-cache label
applied to a warm source. The initial reuse design really failed its zero-page
precondition after earlier socket transfers retained 512 resident pages despite
repeated eviction advice. Those incomplete runs are not accepted samples. The
revised ten trials each observed zero pages on their **first** eviction attempt.
The fixture model eliminates cross-trial source-page lifetime interference
instead of increasing a sleep or accepting nonzero residency. Physical host
cache remains uncontrolled, as in the preceding disk-copy matrix.

The heartbeat compares actual wake time with an absolute 1 ms target, counts
skipped periods, and advances to the next future target without replaying a
backlog. A pending deadline is also observed at completion, so a completely
starved heartbeat cannot disappear behind task cancellation. Storage preparation
does not run while that heartbeat is active.

The accepted run exited successfully with all ten flows verified on 2026-10-27:

| Mechanism | Median flow elapsed (ms) | Median process CPU (ms) | Median per-trial maximum heartbeat lateness (ms) | Worst observed heartbeat lateness (ms) |
| --- | ---: | ---: | ---: | ---: |
| Inline sendfile | 71.288 | 67.354 | 3.125 | 3.923 |
| Selected blocking-worker sendfile | 76.041 | 76.227 | 1.566 | 2.107 |

The worker trades approximately **6.7% flow time and 13.2% process CPU** for
roughly half the median maximum scheduling lateness in this guest-cold case.
CPU includes receiver byte validation and the concurrently active heartbeat;
this is not a raw sendfile-only CPU microbenchmark. The result supports keeping
page-faulting regular-file operations off the reactor, not a throughput-speedup
claim or a universal tail-latency bound. Full per-trial results, including missed
ticks and observed residency, are retained in [the reactor CSV](io-linux-v3-reactor.csv).

Reproduce after building the optimized test binary outside the measurement:

```sh
CARGO_TARGET_DIR=target/wsl-performance cargo test --release --lib --no-run
sudo unshare --mount --fork bash -c '
  mount --make-rprivate /
  mount --bind /tmp "$PWD/.temp"
  findmnt -T "$PWD/.temp" -o TARGET,SOURCE,FSTYPE,OPTIONS
  taskset -c 0-3 target/wsl-performance/release/deps/msync-<test-hash> \
    --exact io::linux::tests::benchmark_guest_cold_sendfile_reactor \
    --ignored --nocapture --test-threads=1
'
```

Use the exact library test binary emitted by the build, not a wildcard that may
select an old artifact. The binding is private and the test creates/removes only
its owned temporary directory through the repository `.temp` path.

## Correctness and reproduction

Native Linux tests exercise actual sockets and regular files, binary contents,
explicit source positions, cancellation during backpressure, same-file rejection,
virtual size-zero files and fallback on an existing connection.

The partial-fallback regression starts an isolated test subprocess, pretransfers
4096 bytes, then installs a process-local seccomp filter denying only
copy_file_range with `EPERM`. The remaining kernel attempt really fails and the
same descriptors continue buffered I/O. The resulting byte count and whole file
prove that the prefix was neither repeated nor lost. No host security policy is
modified.

```sh
CARGO_TARGET_DIR=target/wsl-performance cargo test --lib io::linux::tests
CARGO_TARGET_DIR=target/wsl-performance cargo test --test linux_io
```

Performance experiments are explicitly ignored in normal runs and skip timing in
debug builds. `tests/linux_io.rs` compares the former 64 KiB Tokio buffered model
with the selected kernel path, including receiver EOF and aggregate process CPU.
The private Linux module additionally compares inline versus worker sendfile in
one optimized test binary. Both use alternating ABBA order, warm source files,
the same connection topology and complete received byte counts.

Two optimized 32 MiB loopback experiments completed on 2026-10-27, eight samples
per variant after warmup, including the receiving task reaching EOF. A current-
thread Tokio runtime makes reactor work and blocking-pool work visible in the
same process CPU accounting. The source was on private tmpfs; sockets were
ordinary active localhost TCP connections. The generic control uses the former
64 KiB Tokio file-read/write model, not a mocked callback or a disk bandwidth
estimate.

| Paired experiment | Elapsed median (ms) | Process CPU median (ms) |
| --- | ---: | ---: |
| Generic Tokio buffered control | 28.704 | 30.360 |
| Selected worker sendfile, same paired round | 8.082 | 10.167 |
| Inline sendfile, separate paired round | 9.499 | 9.478 |
| Worker sendfile, same inline/worker round | 10.356 | 11.858 |

The selected kernel path reduced this representative flow's wall time by about
72% and process CPU by about 67% against the previous buffered design. This is
not a claim about encrypted SSH throughput, physical-disk durability, or whole-
process startup. Different experiment rounds varied; do not combine the 8.082 ms
and 9.499 ms rows as if they were a paired worker-versus-inline comparison.

Within the direct execution-mode pair, worker dispatch costs roughly 0.86 ms
over 32 MiB (about 9% elapsed and 25% process CPU). That explicit trade-off keeps
regular-file page faults off the sole reactor thread and preserves independent
flow responsiveness. It is an API/ownership reason, not a fabricated measured
cold-tail speedup. In particular the benchmark is warm-cache; it does not claim
to bound the latency of an unresponsive filesystem operation.

Reproduce the two separate experiments in a quiet optimized Linux run:

```sh
CARGO_TARGET_DIR=target/wsl-performance cargo test --release --lib \
  io::linux::tests::benchmark_sendfile_execution_modes -- --ignored --nocapture --test-threads=1
CARGO_TARGET_DIR=target/wsl-performance cargo test --release --test linux_io \
  benchmark_buffered_and_sendfile_socket_delivery -- --ignored --nocapture --test-threads=1
```

Compilation is outside the timed regions. The retained C mechanism probe and
the Rust socket-delivery benchmark answer different questions; neither is used
as a substitute for end-to-end CLI/startup measurements owned by the application
performance workstream.

## Primary sources

- [Linux sendfile](https://man7.org/linux/man-pages/man2/sendfile.2.html): explicit offsets, partial progress, nonblocking output and source-page lifetime.
- [Linux copy_file_range](https://man7.org/linux/man-pages/man2/copy_file_range.2.html): filesystem capability failures, short copies and virtual-file caveats.
- [Tokio TcpStream readiness](https://docs.rs/tokio/latest/tokio/net/struct.TcpStream.html#method.try_io): clearing readiness only after the corresponding operation returns WouldBlock.
- [Tokio AsyncFd guards](https://docs.rs/tokio/latest/tokio/io/unix/struct.AsyncFdReadyGuard.html): generation-aware readiness clearing for an owned nonblocking descriptor.
- [Kernel io_uring documentation](https://www.kernel.org/doc/html/latest/io_uring/index.html): submission/completion ownership is a separate model, not automatic zero-copy.
- [io_uring networking by Jens Axboe](https://kernel.dk/io_uring%20and%20networking%20in%202023.pdf): batching and zero-copy completion/lifetime mechanisms.

## v0.6 native-Linux dispatch crossover probe

The new host is native Debian Linux 6.12 on ext4 (`/dev/vda4`), not the historical
WSL environment above. Existing findings remain useful for mechanism selection,
but their timings are not current-host results. Production code already selects
`copy_file_range` with bounded 1MiB requests and same-descriptor buffered fallback;
regular-file TCP output already uses worker-offloaded `sendfile` with an explicit
source offset, retained descriptors, and generation-aware socket readiness.
Windows already has a measured small-file buffered dispatch threshold. Linux
currently attempts the kernel path for every nonempty regular source.

The next discriminating question is whether Linux's per-flow clone/AsyncFd and
blocking-worker setup costs justify a similar size-dependent choice for small
configuration-file TCP flows. It is not whether a second ring reactor can win a
synthetic interface comparison. The historical matched-depth ring matrix did
not justify replacing the ordinary native-ext4 range-copy path, although it did
identify a distinct large-file DrvFs specialization opportunity.

`benches/io_v6.rs` is a release-only ignored probe of that crossover:

```sh
cargo test --release --bench io_v6 --no-run
# Run the exact emitted executable when other builds and timing runs are idle.
<exact-benchmark-test-executable> --exact linux::file_tcp_crossover \
  --ignored --nocapture --test-threads=1
```

It compares the public kernel capability with a same-open-handle Tokio buffered
control over real loopback sockets at 4/16/64/256KiB and 1/16MiB. Each size has
three discarded warmups per arm and 31 alternating AB/BA pairs by default.
`MSYNC_IO_V6_SAMPLES` and `MSYNC_IO_V6_SIZES` select explicit positive counts and
comma-separated byte sizes. Source file creation/open and TCP connection setup
are outside timing; native worker registration/descriptor cloning, transfer, and
receiver EOF are inside. Full byte validation is outside timing. Kernel trials
must complete natively and preserve the borrowed source cursor. Every JSONL
record retains size, pair, execution order, wall time, and process CPU, including
the receiving task. Fixtures are owned under the repository `.temp/io-v6`.

This probe answers steady-state mechanism dispatch, not CLI startup, physical
cold-disk behavior, encrypted SSH performance, or storage publication durability.
Current-host mechanism results are recorded below; whole-CLI effects remain
a separate integration gate.

The upstream [liburing networking guide](https://github.com/axboe/liburing/wiki/io_uring-and-networking-in-2023)
explains why batching and event-loop architecture, rather than a syscall-name
substitution, determine ring benefits. [Stamler et al., zIO (OSDI 2022)](https://www.usenix.org/conference/osdi22/presentation/stamler)
likewise use a per-I/O policy to avoid workloads where copy-elimination tracking
costs exceed their benefit. Their system is not proposed as a dependency here;
the transferable principle is to measure the actual size/workload crossover.

### Current-host results and dispatch decision

The root froze the optimized pre-change probe at
`target/performance-v6/baselines/io_v6-before`, SHA-256
`a420307e84f7e8f03a77e19fbe4d322148375a479bc86f9569dffa4fc2b1c491`.
It was compiled against the frozen v0.6 release library rather than a changing
debug artifact. Both runs were pinned to CPUs 0–3 during root-granted quiet
windows without concurrent builds or other benchmarks. The six-size run passed
in 6.73s; a subsequent 128KiB discriminating run passed in 0.10s. All 434 measured
trials (217 paired comparisons), plus discarded warmups, verified complete bytes
and native borrowed-cursor preservation.

| Payload | Kernel p50 (us) | Buffered p50 (us) | Median paired buffered/kernel | Buffered wins / 31 |
| --- | ---: | ---: | ---: | ---: |
| 4KiB | 199.544 | 175.161 | 0.8893 | 21 |
| 16KiB | 188.717 | 172.134 | 0.8974 | 24 |
| 64KiB | 225.698 | 189.488 | 0.8913 | 22 |
| 128KiB | 259.076 | 314.543 | 1.2040 | 1 |
| 256KiB | 348.804 | 472.258 | 1.4082 | 2 |
| 1MiB | 687.270 | 1558.856 | 2.3571 | 0 |
| 16MiB | 9921.244 | 24223.467 | 2.3346 | 0 |

The [durable CSV](io-linux-v6-crossover.csv) includes process-CPU medians, p90,
and sample counts. `benches/io_v6_summary.py` reconstructs it from retained JSONL
and rejects duplicate/missing paired arms. The raw host outputs remain disposable
under `.temp/performance-v6/io`; the methodology and measured conclusions do not.

These data support a narrow **128KiB Linux file-to-TCP dispatch threshold**:
smaller files avoid descriptor cloning/AsyncFd setup and use the existing
same-open-handle buffered path, while larger files retain worker-offloaded
sendfile. This is about an approximately 11% paired mechanism-latency benefit for
small warm loopback flows, not an 11% whole-CLI speedup. The 64KiB buffered p90
was 308.040us versus 294.415us kernel despite its better median; no tail-latency
SLO or universal small-file superiority is asserted. At 128KiB and above the
kernel result is substantially better and the policy must not route those flows
to buffering. Public capability probes remain unconditional so correctness and
host measurements can still explicitly exercise native I/O.

### Production dispatch follow-up

The same bench also exposes ignored test
`linux::file_tcp_production_dispatch`. It calls the real public
`streams::transfer`, including endpoint resolution, source opening, TCP connection
establishment, selected native/buffered dispatch, and receiver EOF in timing.
Before/after binaries must link this identical harness against their respective
frozen release libraries. Defaults are 31 samples at 4KiB/64KiB/128KiB/1MiB,
with three discarded warmups per size. Fixture creation, listener binding, and
complete byte validation remain outside timing. This is a whole stream path,
not a whole CLI invocation or a filesystem-source-snapshot benchmark.

Run the two exact executable paths serially in alternating order, with no
concurrent build. `benches/io_v6_summary.py BEFORE_LOG --after AFTER_LOG` reports
per-arm medians/CPU/p90 without mislabelling separate process runs as intra-run
paired samples. Capture executable/library hashes and retain execution order
alongside results. The completed runs are recorded below.

### Whole-stream before/after measurements

The identical latest `benches/io_v6.rs` production-dispatch harness was linked
with `rustc --edition=2024 --test -C opt-level=3` against the frozen before and
optimized v0.6 release libraries. Direct dependencies and compiler flags were
identical. Before-library SHA-256 was
`59cebec00f092f1892f90c6d731a8b898d9441a4fa6d6c8a388548c9549ea3e0`;
optimized-library SHA-256 was
`d51469d0c5b31d43110d99e17542a84a8813eca44aacfb6188d80481ee8e3e95`.
The exact build script and terminal build log are retained under
`.temp/performance-v6/io/build-production.{sh,log}`. Probe executable SHA-256:

| Probe image | SHA-256 |
| --- | --- |
| `io-production-before` | `657bfc181cb6269daf01457f21297c898bb50b222b2efd29b78b4941d500913b` |
| `io-production-after` | `2ff88d1d6eee4c0809507bb237d1d20d1de35dd345f2e1f24866b5af38b723f2` |

Each experiment recorded those image hashes before and after execution and
required byte-identical hashes. These are the IO probe images, not the CLI or
other runtime-control probe hashes. Root granted an exclusive timing window;
all runs were pinned to CPUs 0–3. The first experiment ran processes in ABBA
order (before, after, after, before), 31 samples per size per process after three
warmups. All 496 measured stream transfers completed with exact payload bytes
and receiver EOF.

| Payload | Before / after p50 (us) | Before / after p90 (us) | Before / after CPU p50 (us) | After/before p50 |
| --- | ---: | ---: | ---: | ---: |
| 4KiB | 367.522 / 304.619 | 498.637 / 407.638 | 351.350 / 297.526 | 0.8288 |
| 64KiB | 430.718 / 377.665 | 522.857 / 463.833 | 421.374 / 381.815 | 0.8768 |
| 128KiB | 552.185 / 505.370 | 654.263 / 634.472 | 545.661 / 513.146 | 0.9152 |
| 1MiB | 1067.693 / 1132.481 | 1198.208 / 1269.974 | 1253.384 / 1359.617 | 1.0607 |

Each cell aggregates 62 samples per image. The [production-dispatch CSV](io-linux-v6-dispatch.csv)
retains these measurements. The 4KiB improvement was present in both ABBA halves
(about 16–17%). The 64KiB halves ranged from essentially unchanged to about 20%
better. The 128KiB halves reversed direction (+24% and -19%); its aggregate
improvement must not be presented as a stable kernel-path speedup. Both images
still select sendfile at that size. The initial 1MiB result was worse in both
halves (+7.9% and +5.8%), which triggered a targeted discriminator rather than
being dismissed as noise.

The discriminator used the same frozen images and exclusive CPU-pinned ABBA
process order, restricted to 1MiB with 101 samples per process. All additional
404 transfers verified their complete payload/EOF; image hashes again matched.

| 1MiB discriminator | Before / after p50 (us) | Before / after CPU p50 (us) | After/before p50 |
| --- | ---: | ---: | ---: |
| First ABBA half | 1381.349 / 1248.456 | 1514.925 / 1496.325 | 0.9038 |
| Second ABBA half | 1551.873 / 1654.124 | 1750.820 / 1892.862 | 1.0659 |
| Aggregate, 202 samples/image | 1420.753 / 1393.685 | 1588.526 / 1625.353 | 0.9809 |

Aggregate p90 was 2542.829us before and 2685.151us after. The
[discriminator CSV](io-linux-v6-dispatch-large.csv) preserves aggregate counts and
CPU/tail data. Opposite half-directions and the changed absolute latencies do
not establish a consistent 1MiB median regression, but CPU and p90 remain mixed;
this is not evidence of a universal no-regression bound.

**Accepted scope:** use the measured 128KiB Linux dispatch threshold to avoid
native setup for small warm file-to-TCP flows, retaining the existing same-handle
buffered path and unchanged large-file kernel implementation. The measured
whole-stream 4KiB/64KiB medians improved approximately 17%/12% in the first ABBA
experiment. Those are not whole-CLI gains and cannot all be causally attributed
to the threshold: the two linked release libraries also contain the integration's
other changes. The same-library kernel/buffered crossover above is the independent
mechanism evidence for the threshold itself.

No storage-copy algorithm, cancellation protocol, source cursor contract, stage
publication logic, Windows policy, or ring runtime was changed in this workstream.
The public low-level native capability remains available for explicit probes.

The root's selected unit gate (`.temp/performance-v6/unit-gates.log`) passed both
`io::dispatch_tests` boundary/remaining-offset tests and the real-socket
`streams::dispatch_tests::file_to_tcp_dispatch_preserves_bytes_and_eof_around_the_size_boundary`
journey (4KiB, 64KiB, 128KiB−1, 128KiB). These check exact bytes, EOF, unchanged
source contents, threshold inclusivity, saturated offsets, and nonmovement of
the policy probe's borrowed cursor. No broad suite rerun is inferred from those
three targeted successes.

### Final accepted integration image: boxed trace-state repair

The integration subsequently boxed large active telemetry state to repair an
independently measured pure-control regression. The IO algorithm/threshold did
not change. Earlier production tables above remain evidence for their exact
intermediate image only. The final IO harness was relinked with the identical
source/flags/direct dependencies against saved
`libmsync-v060-optimized-3.rlib`, SHA-256
`b36020ff901b728f1ea9914f4704d5300ead3c49f6cb751afcb4fff043c4b9b2`.
This is the library paired with the root's final optimized-3 CLI image
(SHA-256 `aa4749e3759d1cb55f898077e19f32a6cb8d37e8867c0c271f37da9af9adedcf`);
the IO results do not come from timing that CLI executable.
The final IO probe SHA-256 is
`0256c6928cb9ceb58ee10b42927566bf40425db9c0a25d47d172d389dc121e25`;
the unchanged baseline probe remains `657bfc…500913b` from the earlier table.

During one exclusive root-granted CPU-0–3 timing window, the final comparison
repeated the four-size 31-sample-per-process ABBA experiment, then a targeted
1MiB 61-sample-per-process ABBA experiment. All **740** measured production
transfers verified complete bytes and EOF. Begin/end hashes of both probe images
were identical. [Final four-size CSV](io-linux-v6-dispatch-final.csv):

| Payload | Before / final p50 (us) | Before / final p90 (us) | Before / final CPU p50 (us) | Final/before p50 |
| --- | ---: | ---: | ---: | ---: |
| 4KiB | 397.727 / 351.217 | 526.822 / 506.546 | 373.538 / 335.348 | 0.8831 |
| 64KiB | 479.280 / 416.418 | 564.158 / 537.604 | 452.685 / 417.769 | 0.8688 |
| 128KiB | 495.237 / 443.490 | 663.166 / 588.820 | 502.135 / 463.325 | 0.8955 |
| 1MiB | 999.607 / 973.081 | 1284.896 / 1236.994 | 1173.672 / 1217.281 | 0.9735 |

Each cell aggregates 62 samples per image. The final observed small-file
whole-stream median gains are approximately **12% at 4KiB and 13% at 64KiB**,
not the intermediate image's 17%/12% headline. CPU and p90 for those aggregate
small-file cells also improved. However, the 4KiB halves reversed direction
(+17.5% then -25.8%), and the 1MiB halves did likewise (-14.9% then +16.2%).
These short-process results exhibit substantial host drift; the aggregate gains
must be read together with the separate same-library capability crossover, not
as universal per-run guarantees or a causal attribution of every gain to the
threshold.

The [final 1MiB repeat](io-linux-v6-dispatch-final-large.csv), 122 samples per
image, produced:

| 1MiB final discriminator | Before / final p50 (us) | Before / final p90 (us) | Before / final CPU p50 (us) | Final/before p50 |
| --- | ---: | ---: | ---: | ---: |
| First half | 879.175 / 903.936 | 1155.636 / 1153.117 | 1078.621 / 1081.165 | 1.0282 |
| Second half | 1109.093 / 1088.091 | 1325.724 / 1274.315 | 1267.372 / 1280.939 | 0.9811 |
| Aggregate | 980.027 / 1015.708 | 1277.644 / 1248.232 | 1153.384 / 1176.991 | 1.0364 |

Thus the final repeat's aggregate median was 3.6% slower and process CPU 2.0%
higher, while p90 improved 2.3%; half medians again had opposite directions.
Together with the four-size run's 2.7% faster 1MiB aggregate, these do not establish
a consistent large-file median penalty. They also do not justify asserting a
strict no-regression bound. Every half's wall/CPU/p90 measurements, including
all four sizes, are preserved in the [final halves CSV](io-linux-v6-dispatch-final-halves.csv).
The narrow dispatch policy remains accepted; no additional IO machinery or
unmeasured large-file optimization was introduced to chase this variation.
