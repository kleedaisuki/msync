# 0.8 copy-mechanism investigation

## Question and equivalent contract

The next I/O decision is not which API has fewer calls. It is which mechanism
reduces elapsed milliseconds or CPU for ordinary file/tree work while retaining
owned same-parent staging, exact bytes, metadata, cancellation, one final explicit
file flush and atomic replacement. Very large uncancellable `File.Copy` is not
an equivalent implementation. Root CLI/compiler/LSP measurements remain in
[performance-v080](performance-v080.md); native timestamp correctness is recorded
separately in [io-metadata-v080](io-metadata-v080.md).

The baseline is the accepted 0.8 Linux Native AOT image SHA-256
`ca5c2eb2aebe1397f4ec3a745a6e76dcfcf598723ba1046523f183012263f38b`, source
`364de751`. The preserved optimized Rust 0.7.3 image remains the product baseline.
I/O-mechanism probes hold frontend, host initialization and publication constant
rather than attributing their costs to the copying loop.

## Windows: select the existing native copy facility

The upstream [.NET Windows implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Windows.cs)
already delegates `File.Copy` to the Win32 copy facility, but without a
cancellation callback. The old Rust runtime already used a progress-cancellable
`CopyFileExW` implementation. The C# rewrite's overlapped FileStream loop copied
only the main byte stream, losing ordinary NTFS alternate data streams.

`tests/IO/WindowsCopyProbe` was run as Native AOT on actual Windows in GitHub
Actions run `42467143650`. Raw output is
`artifacts/ci/42467143650/windows/native-win-x64-51689bc4e198548b33fb0ba67ccdcab771b927d1/.temp/platform-results/windows-copy-probe.json`.
There are two warmups and nine alternating paired samples. Both mechanisms time
staging, data, metadata, one explicit flush and replacement. Hash/mtime checks,
source generation and directory enumeration are outside this component interval.

| Component | Overlapped median ms | Native median ms | Change |
| --- | ---: | ---: | ---: |
| 4 KiB file | 0.5370 | 0.5301 | -1.3% |
| 1 MiB file | 2.0997 | 1.3421 | -36.1% |
| 64 MiB file | 297.0929 | 247.7616 | -16.6% |
| 256 × 4 KiB files | 147.3530 | 119.5431 | -18.9% |

The native path also retained the NTFS named stream; the managed main-stream
copy did not. Readonly, long-path and empty-file cases passed. Cancellation after
actual positive progress returned in 0.331ms after 64KiB for the managed loop and
0.844ms after 1MiB for the native callback; both preserved the old published
bytes. These are component observations on that Windows host, not full CLI
startup timings or hard latency gates.

The selected product path owns one worker per tree and calls CopyFileEx into its
unique sibling stage. The first-stream callback retains the already-open writable
destination handle. After native completion it flushes that retained handle once,
closes it, checks cancellation and replaces the target. This avoids reopening a
readonly stage for write. No restartable/unbuffered flag or persistent copy state
is used. Cleanup may clear readonly only on its owned temporary inode, never on
source, unrelated hard links or the user's destination to force success.
`tests/IO/windows_file_metadata.py` checks the actual compiled CLI as source,
`.mso` and `.mexe`, including readonly source, fresh/overwrite destinations,
named streams and unrelated destination-hardlink ownership.

The actual initial Windows tree CLI failed with AccessDenied because directory
mtime used the File API instead of the Directory API. Its failed result is not a
performance baseline; the directory API repair has a separate real-user fixture
and must pass before accepting whole-tree product timings.

## Linux: reject universal copy_file_range for this workload

The initial 128MiB native CLI trace found 2,048 pwrite64 and 2,052 pread64 calls in
C#, versus 129 copy_file_range calls in Rust. Both performed one fsync and zero
syncfs. A bounded candidate reduced the C# data calls to 129 copy_file_range
calls, also retaining one fsync. The normal path attempted 1MiB kernel windows,
checked cancellation between them, and resumed the same FileStream cursors on
actual unsupported-operation errors. FileStream maintains a separate logical
cursor, so fallback explicitly seeks both streams to the already-copied prefix.
Zero-metadata-size procfs files use the existing read loop rather than treating
size as EOF. Real I/O failures are not reported as unsupported-operation success.

The host is Debian 13, Linux 6.12.113, native ext4, glibc 2.41 and .NET 10.0.14.
FICLONE returned EOPNOTSUPP on this filesystem. There was no clone acceleration
to assume from a lower syscall count. A same-source Native AOT candidate was
built from the baseline source plus only the copying-loop change; exploratory
whole-CLI 7-pair measurements showed filesystem scatter and were not used to
select an arbitrary size threshold.

The decisive controlled component probe runs both loops inside the **same native
process**, with one reusable 64KiB buffer. It includes source metadata, staging,
copy, metadata installation, fsync and rename. It records CLOCK_PROCESS_CPUTIME_ID
nanoseconds as well as monotonic wall time. Two warmups and 21 alternating paired
samples were collected, with exact bytes/mode/mtime checked outside intervals.
Raw samples: `.temp/io-v080/component-copy-21.json`.

| Component | Buffered p50/p95 ms | Kernel p50/p95 ms | Median paired delta ms |
| --- | ---: | ---: | ---: |
| 4 KiB file | 1.386 / 1.691 | 1.320 / 1.718 | -0.007 |
| 1 MiB file | 3.999 / 5.339 | 3.748 / 4.335 | -0.189 |
| 128 MiB file | 224.212 / 249.444 | 221.199 / 233.676 | -9.299 |
| 256 × 4 KiB files | 309.466 / 358.860 | 322.954 / 353.879 | +11.564 |

For 128MiB, process CPU medians were 166.713ms buffered and 168.812ms kernel;
for the small-file cohort they were 69.948ms and 71.268ms. The universal kernel
path did not establish a large-file CPU win and worsened the dominant small-file
cohort's median by 4.4%. It is **not shipped**. Linux keeps the existing single
worker, pooled-buffer loop. No unmeasured threshold policy is invented to salvage
the candidate. The mechanism remains a reproducible research probe under
`tests/IO/Shared/KernelFileCopy.cs`, not a product dependency.

```
dotnet publish tests/IO/LinuxCopyProbe/LinuxCopyProbe.csproj -c Release -r linux-x64 \
  --artifacts-path .temp/build-io-component -o .temp/io-v080/component
.temp/io-v080/component/LinuxCopyProbe --samples 21 \
  --output .temp/io-v080/component-copy-21.json
dotnet run --project tests/IO/LinuxCopyContracts/LinuxCopyContracts.csproj \
  --artifacts-path .temp/build-io-contracts -- --partial-fallback
```

The syscall contract probe uses an isolated seccomp filter to allow actual full
kernel windows and deny the final short range. It verifies positive-prefix
fallback without repeating or losing bytes, plus zero-size-metadata virtual-file
contents. It installs no filter in the product process or parent shell.

## Engineering and research interpretation

The [.NET PAL source](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.c)
and [Linux copy_file_range contract](https://man7.org/linux/man-pages/man2/copy_file_range.2.html)
explain the opportunity for filesystem/kernel acceleration and the need to handle
partial results and unsupported operations. They do not establish a performance
win on a particular filesystem. [Go's production fallback implementation](https://go.dev/src/internal/poll/copy_file_range_linux.go)
also records real CIFS/NFS/container lessons; our probe preserves actual errors
rather than treating every failure as a reason to restart or reconnect.

[Zhan et al., *How to Copy Files*, FAST 2020](https://www.usenix.org/conference/fast20/presentation/zhan)
explains how clone creation, reads, later writes and space use trade off rather
than providing one universal free-copy mechanism. That distinction matters here:
shared-data cloning, kernel movement and ordinary buffered copying are different
mechanisms, and an ext4 byte-copy result should not be reported as a reflink win.
The concrete decision is to use the demonstrated Windows native facility,
retain the measured Linux buffered path, and preserve exact metadata and bounded
cancellation independently of throughput claims.
