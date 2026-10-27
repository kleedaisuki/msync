# V3: process startup and useful planning waits

## Workload and mechanism

The baseline is commit `d96724d` (0.2.0), preserved as an executable before
changing the implementation. `benches/startup.rs` measures actual CLI process
completion, not just an in-process parser timer. Its timer includes child spawn,
argument processing, the requested command, output handling and process exit.
Stdout is discarded, stderr is drained, and every exit must succeed. The REPL
case sends `/help` and `/quit` over persistent stdin. Fixtures are disposable
children of repository `.temp` and are removed by the harness.

Each ordinary case has five warmups per executable and 30 samples, alternating
AB/BA order. Reported p50/p95 are lower order statistics (`floor((n - 1) * p)`), not confidence intervals or
tail-latency guarantees. The optional Windows WSL case has one warmup and five
samples because each baseline invocation launches many real remote clients.
Compilation and initial fixture creation are outside the timed interval.

The main improvement is reducing work rather than deferring it: per-plan path
resolution/classification caches avoid repeated metadata probes, remote paths
are grouped/deduplicated into bounded metadata-only client invocations, and the
indexed effect planner avoids scanning the entire prior history. These caches
are owned by one preparation, never reused as global filesystem truth. Error
rows still map to the original source/destination operand spans. Offline plans
do not contact remote endpoints.

## Windows paired application measurements

First controlled comparison before ancestor-prefix caching on 2026-10-27, Windows x86_64, Rust 1.99.0,
Intel Core i9-12900H, normal Cargo release profile:

| Journey | Baseline p50 ms | V3 p50 ms | Baseline p95 ms | V3 p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `--version` | 8.269 | 8.884 | 16.966 | 24.832 |
| `--help` | 9.037 | 9.359 | 22.967 | 23.515 |
| `check`, one operation | 10.091 | 9.989 | 16.264 | 24.471 |
| `plan`, one operation | 10.530 | 11.176 | 25.744 | 23.991 |
| `plan`, 100 shared-source operations | 92.078 | 49.752 | 108.985 | 55.096 |
| REPL help then exit | 9.292 | 9.456 | 23.268 | 23.812 |
| `plan`, 16 unique real WSL sources | 1735.715 | 158.902 | 1738.800 | 198.458 |

Raw output is `.temp/perf-v3/startup-windows-first.txt`; useful results are
retained here so deleting disposable files does not erase the finding. These
measurements do **not** say every command became faster. Small process-only
journeys are roughly 8–11 ms with desktop scheduling noise; the measured
single-operation planning increase is 0.646 ms. The material gains are about
42 ms for the 100-operation local journey and 1.58 seconds for the 16-path WSL
metadata journey. The WSL case intentionally uses actual remote metadata, not
an in-memory stand-in or a data-transfer throughput estimate.

Reproduce from the repository root, preserving a baseline executable first:

```powershell
cargo build --release --locked
rustc --edition 2024 -O benches/startup.rs -o target/perf-probe/startup.exe
target/perf-probe/startup.exe target/perf-baseline/msync-d96724d.exe target/release/msync.exe 30 --wsl
```

The harness also builds on Linux; omit `--wsl` there. Native Linux startup
measurements should stage both executables and their working fixtures on the
same native filesystem, rather than comparing executable loading or metadata
through DrvFs against native ext4. WSL startup outside the native harness is
not charged to an individual Linux child process.

Planner-only scaling and heap/probe counters are documented separately in
`performance-planner-v3.md`. Transfer throughput, native filesystem/cache state
and kernel/ring selection are separate questions in `io-linux-v3.md` and
`io-windows-v3.md`; none can be inferred from this startup table.

## Release-profile control

A quiet paired Windows run before ancestor-prefix caching also compared the same V3 sources with default
release code generation versus thin LTO and one codegen unit (30 alternating
samples, the same harness):

| Journey | Default p50 ms | Thin LTO p50 ms |
| --- | ---: | ---: |
| `--version` | 7.746 | 7.989 |
| `--help` | 7.638 | 8.428 |
| `check`, one operation | 8.120 | 8.814 |
| `plan`, one operation | 9.731 | 9.615 |
| `plan`, 100 shared-source operations | 46.037 | 45.794 |
| REPL help then exit | 7.801 | 7.872 |

Thin LTO reduced this Windows image from 3,960,320 to 3,594,240 bytes, but did
not deliver a useful startup/planning improvement in this control. The release
profile therefore remains Cargo's normal profile; smaller code is not itself
evidence of faster user journeys. Raw output: `.temp/perf-v3/startup-windows-lto.txt`.

## Native loader/data control and pathname scope

Both Linux executables, the native harness and its data fixtures were staged
through a private project `.temp` bind of `/tmp`, on `/dev/sdd` ext4 (reported
storage magic `0xef53`, and `findmnt` reports ext4). The native harness, pinned
to CPU 0, measures Linux child processes directly; `wsl.exe` startup is outside
the samples. Baseline and current use the same Rust 1.97.1 toolchain.

Before ancestor-prefix caching, the 30-sample paired control measured:

| Journey | Baseline p50 ms | V3 p50 ms |
| --- | ---: | ---: |
| `--version` | 0.594 | 0.613 |
| `--help` | 0.579 | 0.585 |
| `check`, one operation | 0.584 | 0.600 |
| `plan`, one operation | 2.182 | 2.262 |
| `plan`, 100 shared-source operations | 117.922 | 60.832 |
| REPL help then exit | 0.559 | 0.585 |

This removes DrvFs-backed image/data transfer from those files, but **not every
DrvFs pathname operation**: the project-addressed bind has `/mnt/d/Code/msync`
ancestors. Repeated ancestor-validation metadata calls still cross that mounted
prefix. It is a project-addressed native-data journey, not a claim about pure
`/home`-addressed ext4 planning. That distinction exposed useful remaining work:
one preparation can share successfully validated ancestor chains while keeping
each distinct leaf classification and fresh execution-time checks. Raw output:
`.temp/perf-v3/startup-native-linux.txt`.

The storage matrix in `io-linux-v3.md` times copies on already-open descriptors,
so this pathname-preparation effect does not contaminate its native ext4 copy
intervals. Filesystem residency and process startup are separate controls.

The retained native-loader/data wrapper reproduces the private bind, executable
staging, CPU pinning and cleanup (run as root inside WSL from the repository):

```sh
mkdir -p target/perf-probe
rustc --edition 2024 -O benches/startup.rs -o target/perf-probe/startup-linux
unshare --mount --fork bash benches/startup_linux.sh \
  target/perf-baseline/msync-d96724d-linux target/wsl-performance/release/msync 30
```

`MSYNC_NATIVE_STORAGE` may select a different native ext4 directory if `/tmp`
is not ext4; the wrapper checks the actual mount and rejects tmpfs/DrvFs.

## Final paired application results

The completed preparation additionally owns an `Inspection` ancestor set. Each
distinct leaf is inspected once, and only successfully validated ancestor chains
are shared. A failed chain cannot populate a positive cache. Raw parent steps
and ignored trailing directory markers remain guarded; no canonicalization
collapses a symlink traversal. Execution uses fresh checks, independently tested
by replacing a prepared parent with a symlink before executing its plan.

Final quiet Windows comparison, same 30-sample method and five-sample WSL case:

| Journey | Baseline p50 ms | Final p50 ms | Baseline p95 ms | Final p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `--version` | 8.891 | 9.065 | 10.160 | 10.219 |
| `--help` | 8.904 | 8.225 | 9.661 | 9.751 |
| `check`, one operation | 9.511 | 9.702 | 10.821 | 11.274 |
| `plan`, one operation | 10.893 | 10.602 | 12.335 | 11.758 |
| `plan`, 100 shared-source operations | 87.564 | 23.748 | 96.395 | 25.448 |
| REPL help then exit | 8.953 | 8.807 | 9.825 | 9.908 |
| `plan`, 16 unique real WSL sources | 1672.202 | 157.034 | 1686.138 | 160.501 |

Final project-addressed native-image/native-data Linux control, with the pathname
scope described above and the same CPU-0/30-sample method:

| Journey | Baseline p50 ms | Final p50 ms | Baseline p95 ms | Final p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `--version` | 0.543 | 0.565 | 1.108 | 0.837 |
| `--help` | 0.554 | 0.568 | 0.866 | 1.040 |
| `check`, one operation | 0.574 | 0.565 | 0.915 | 1.104 |
| `plan`, one operation | 2.059 | 1.437 | 4.164 | 2.115 |
| `plan`, 100 shared-source operations | 115.169 | 3.790 | 178.782 | 5.759 |
| REPL help then exit | 0.600 | 0.616 | 0.864 | 0.858 |

The useful improvements are in actual planning waits: Windows 100-operation
planning is about 3.7x faster and the real WSL metadata case about 10.6x faster.
Sharing ancestor validation removes the particularly costly repeated mounted-
prefix probes in the Linux project-addressed journey. That approximately 30x
result is not a claim about pure `/home` ext4 metadata or block-copy throughput.
Simple startup stays mostly unchanged; Windows version/check p50 are respectively
0.174/0.191 ms higher, so a universal startup-speedup claim would still be false. The final tables
retain that cost rather than trading semantics or hiding unfavorable cases.

Raw final outputs are `.temp/perf-v3/startup-windows-final.txt` and
`.temp/perf-v3/startup-native-linux-final.txt`. All measured processes exited
successfully and each timed window excluded builds and other agents' workloads.
