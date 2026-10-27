# Windows file and stream I/O investigation

## Workload and boundary

The user asked for observable improvements to ordinary copying, file-to-stream
flows, and startup/waiting, while retaining correct interruption and cancellation.
This note owns the Windows transport mechanisms; planner and CLI timing have
separate measurements. The committed application baseline is `d96724d` (0.2.0).
The implementation host is Windows 11 build 26200, x86-64, with Rust 1.99.0.

### Native pathname compatibility

Direct Win32 calls must retain the long-path support supplied by Rust's filesystem
APIs. A runtime regression test exposed raw UTF-16 paths failing with OS error 3
once a valid path exceeded 260 characters, despite `std::fs::write` succeeding.
`windows::wide_path` is now the single conversion boundary for `CopyFileExW`,
no-clobber `MoveFileExW` publication, and readonly-stage `SetFileAttributesW` cleanup.
It rejects interior NUL, preserves existing verbatim/device namespaces, and uses
`std::path::absolute` before adding extended drive or `\\?\UNC\` prefixes. This
normalizes ordinary slash/dot and drive-relative/current-drive syntax without
requiring the destination to exist, changing a registry setting, or assuming an
application `longPathAware` manifest. All path data stays UTF-16; even unpaired
surrogates are retained rather than passing through lossy UTF-8 conversion.

Native tests cover both replacement policies beneath a parent exceeding 260
characters, a 234-character source component, a concurrently created no-clobber
target, and abandoned readonly-stage cleanup. The runtime test additionally
executes overwrite and container-target journeys with long source/target names.
Pure units cover UNC conversion (without requiring a network share), verbatim and
device preservation, slash/dot normalization, relative paths, NUL and UTF-16.

`src/io/windows.rs` implements two paths:

- `CopyFileExW` performs a finite file flow into an exclusively owned staging
  pathname. Its native progress callback observes the cancellation token and
  records cumulative progress. A cancelled or failed stage is not published.
- `TransmitFile` moves an already-open file's data through the Windows cache
  manager to an already-connected TCP socket, without copying payload through a
  Rust relay buffer. This is an opportunity to avoid userspace copies, not a claim
  that every storage/network driver performs zero physical memory copies.

Tokio already uses IOCP for native Windows sockets and named pipes. Replacing it
with a custom polling reactor would add machinery rather than introduce event
drivenness for the first time. `TransmitFile` uses a bounded blocking bridge only
for kernel completion/cancel events; no `WaitForSingleObject(INFINITE)` runs on
the async reactor, and no worker is created per chunk.

## Handle, offset and completion ownership

The fast path receives borrowed source/destination handles but retains no borrow
after the initiating future ends. The socket is independently duplicated; the
source is reopened **by its existing handle**, not by its pathname, using
`ReOpenFile` with `FILE_FLAG_OVERLAPPED | FILE_FLAG_SEQUENTIAL_SCAN`.

This distinction was found through independent end-to-end review: `File::try_clone`
shares the synchronous file object's offset. A `TransmitFile` attempt could read
ahead and move the caller's cursor even when it returned an unsupported-operation
fallback. The new file object has an independent cursor, and each transmit uses
an explicit `initial_offset + completed_bytes`. The original cursor remains
unchanged on success, fallback, cancellation and future drop. The generic fallback
continues on the same connection and original source handle after seeking to the
captured start plus the accepted prefix; it never reconnects or repeats a prefix.

`WSAIoctl(SIO_GET_EXTENSION_FUNCTION_POINTER, WSAID_TRANSMITFILE)` obtains the
function for the actual socket provider. A private manual-reset event belongs to
each flow. The low bit of `OVERLAPPED.hEvent` suppresses IOCP packet delivery:
Tokio/Mio must never receive a foreign `OVERLAPPED` as if it were its own internal
completion record. Transfers use at most 16 MiB per native request, below the API's
2,147,483,646-byte call limit.

One worker owns the file object, duplicated socket and completion event. A separate
owned stop event is signalled by token cancellation, destination FIN, or dropping
the async future. `CancelIoEx` requests cancellation, then the worker waits for the
**specific completion event** before freeing the request or its handles. A
cancellation request is not treated as completion. A failed completion after a
request was submitted is not replayed through buffered fallback, because it may
have delivered an unreported partial prefix.

The private completion event is owned with full access, cannot be concurrently
closed, and is waited non-alertably without a timeout. An unexpected failure of
that wait is an internal handle invariant violation, not ordinary cancellation or
an I/O failure. The implementation records the OS error and fails the process
without unwinding a possibly live `OVERLAPPED`; it does not mistake an unchecked
`WSAGetOverlappedResult` failure for proof of completion. Normal operation-aborted
completion is fully drained and returned as cancellation, without this fail-stop.

Cancellation may terminate TCP with a reset rather than graceful EOF. Tests accept
that normal cancelled-flow boundary, require bounded termination, and verify that
the unfinished 64 MiB body is not silently transmitted to completion. Byte counts
on interrupted native requests conservatively include completed/reported prefixes;
they do not promise application-level acknowledgement of kernel-queued bytes.

## Actual I/O Ring probe

`tests/windows_io.rs` contains an explicit, dynamically loaded I/O Ring experiment.
Production startup does not load a Windows-11-only API DLL or call a ring capability
query. On the implementation host, the real calls produced:

```text
QueryIoRingCapabilities:
  MaxVersion = 400
  MaxSubmissionQueueSize = 65536
  MaxCompletionQueueSize = 131072
  FeatureFlags = 2
CreateIoRing(advertised version): success
IsIoRingOpSupported(READ): true
IsIoRingOpSupported(WRITE): true
BuildIoRingReadFile(offset=7, length=65536) + Submit + Pop: byte-exact
```

The test retains its source handle and allocation until each submitted completion
is reaped. `CloseIoRing` alone does not cancel in-flight buffer accesses; it is not
a substitute for completion ownership. Missing APIs on an older Windows version
are an explicit unsupported probe outcome, not a mandatory-loader startup failure.

### Sequential warm-read comparison

Command:

```powershell
cargo test --release --test windows_io io_ring_read_latency_experiment -- --ignored --nocapture
```

Each case alternates I/O Ring and ordinary positioned reads over a warm regular
file, for 41 repetitions. Both include a newly allocated destination buffer; the
ring retains its source clone across repetitions, rather than charging it an
extra per-read handle duplication. Medians include submission/completion overhead.
They are a sequential-copy workload, not a random-read/high-queue-depth benchmark.

| Read size | I/O Ring median | Positioned read median |
| --- | ---: | ---: |
| 4 KiB | 2.500 µs | 1.700 µs |
| 64 KiB | 5.700 µs | 5.500 µs |
| 4 MiB | 1,267.700 µs | 1,228.600 µs |

The probe establishes real availability and correctness, but this workload shows
no gain that pays for a second production file queue, registered-buffer lifetimes,
and cancellation engine. I/O Ring is therefore **not** the selected file-copy
backend. In particular, read-into-buffer/write-from-buffer batching is not itself
a zero-userspace-copy file-to-socket transfer. Windows cache-manager copy and
TransmitFile address the dominant workload more directly. A future high-depth
storage workload should remeasure before changing that decision.

Raw output is reproducible at `.temp/perf-v3/windows-ring-read.txt` in this checkout;
the setup/results above are durable even when temporary logs are cleaned.

## Native file-to-TCP comparison and dispatch decision

Command:

```powershell
cargo test --release --test windows_io windows_file_tcp_latency_and_throughput_experiment -- --ignored --nocapture
```

This compares real `TransmitFile` with the former 64 KiB Tokio read/write loop on
localhost TCP. Each source contains nontrivial repeating binary data, is warm in
the file cache, and has one warmup through each method. Nine measured repetitions
alternate method order. Connections and source handles are opened before timing;
the measured interval ends only after the receiving peer has counted the complete
body and observed EOF. This is steady-state transfer latency, **not CLI startup**
or a cold-disk/network benchmark. The test asserts that native trials completed
in the kernel path rather than silently falling back.

The final clean run used the already-built release test executable, after the
other agents' compiler and timing processes finished:

| Source size | TransmitFile p50 | Buffered p50 | TransmitFile p90 | Buffered p90 |
| --- | ---: | ---: | ---: | ---: |
| 4 KiB | 0.3495 ms | 0.2807 ms | 0.8842 ms | 0.5003 ms |
| 64 KiB | 0.3485 ms | 0.2803 ms | 0.5542 ms | 0.6482 ms |
| 1 MiB | 0.8891 ms | 1.1503 ms | 1.6920 ms | 2.0292 ms |
| 16 MiB | 14.3267 ms | 15.4689 ms | 22.6362 ms | 16.8903 ms |
| 64 MiB | 61.8402 ms | 83.9734 ms | 70.7924 ms | 91.4732 ms |

At 64 MiB this is about **1.36×** transfer-latency improvement. Mean application
process CPU measured with `GetProcessTimes` was **22.57 ms versus 93.75 ms** per
trial, including the in-process receiving peer. That is not total machine CPU:
driver/DPC or other-process work may move elsewhere. The CPU timer is coarse;
zero/sub-millisecond readings for small files are not evidence of zero CPU cost.
The nine-sample p90 is a small-sample diagnostic, not a reliable tail-latency SLO.
The 16 MiB native upper sample was worse and is retained above rather than hidden.

The small-file setup cost is real: an independent overlapped file object, socket
duplication, event handles and a worker are not free. Therefore the production
stream adapter chooses the kernel path only for **at least 1 MiB remaining** on
Windows, the smallest measured case with a useful clean-run gain. Smaller flows
use the same-open-handle buffered fallback before allocating those resources.
The public low-level capability API remains callable for explicit probes, and
neither dispatch nor fallback reconnects the stream. This avoids accepting a
demonstrated configuration-file latency regression merely to claim “zero copy.”

A preliminary run overlapped another agent's correctness build and is not the
final comparison. Both raw logs remain in `.temp/perf-v3/windows-transmitfile*.txt`;
the clean table above is the retained evidence. Windows desktop editions may also
limit outstanding TransmitFile work; these single-flow results do not establish
multi-flow/server-version scaling.

Explicit `TraceContext` is carried into the async kernel scope and attached only
while the blocking worker runs synchronously. Selection, fallback stage/OS code,
completion/end reason, cancellation and known-byte counters are structural events;
no thread-local span guard crosses an await. An aborted future marks its kernel
scope cancelled while its owned worker still drains safely.

## Correctness gates

```powershell
cargo test --lib io::windows::tests -- --nocapture
cargo test --test windows_io -- --nocapture
cargo test --test runtime native_copy_preserves_long_path_support -- --nocapture
cargo test --test review_v3 windows_kernel -- --nocapture
cargo test --test windows_io actual_io_ring_capabilities -- --ignored --nocapture
```

The Windows I/O target covers byte-exact 8 MiB native transfer, cancellation under
backpressure, aborting the initiating future while I/O is pending, destination
FIN, staging/no-publication on cancellation, and actual I/O Ring reads. The review
target adds a nonzero source cursor and same-handle fallback check. Native transfer
tests explicitly require a terminal kernel result and byte count, so successful
buffered fallback cannot masquerade as proof that TransmitFile ran.

The private native-copy unit deterministically cancels from the real CopyFileExW
progress callback **after it reports positive transferred bytes**. It verifies an
Interrupted outcome with progress, the unchanged previously published target and
cleaned staging file. The injection hook exists only in test builds; this is not
a timer that might cancel before either file opens. Final 0.3.0 checks passed that
unit plus four path units, seven ordinary Windows I/O tests, the runtime long-path
regression, five ordinary independent review tests, strict library/Windows-I/O
Clippy and per-owned-file whitespace checks. The three
host/timing experiments stay explicitly ignored by the ordinary suite.

## Primary references

- [Maximum Path Length Limitation](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation) and [Rust path::absolute](https://doc.rust-lang.org/std/path/fn.absolute.html): extended path syntax and normalization before conversion.
- [TransmitFile](https://learn.microsoft.com/en-us/windows/win32/api/mswsock/nf-mswsock-transmitfile): cache manager, provider lookup, offsets, call limits and desktop/server resource considerations.
- [ReOpenFile](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-reopenfile): independent file object and overlapped positioning without resolving a filename again.
- [GetQueuedCompletionStatus](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatus): event low-bit suppression of completion-port notification.
- [CancelIoEx](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex): cancellation requests and overlapped resource lifetime.
- [QueryIoRingCapabilities](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-queryioringcapabilities): process-local capabilities and minimum supported Windows build.
- [BuildIoRingReadFile](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-buildioringreadfile), [SubmitIoRing](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-submitioring): asynchronous buffer/reference and completion semantics.
- [CloseIoRing](https://learn.microsoft.com/en-us/windows/win32/api/ioringapi/nf-ioringapi-closeioring): in-flight operations remain active after closing the ring.
