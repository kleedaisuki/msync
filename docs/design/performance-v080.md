# 0.8 native user-journey performance

## Question and workload

The C# rewrite changes frontend, artifact format and host runtime together.
Performance acceptance must therefore compare actual user operations with
equivalent effects, not interpreter-op microbenchmarks or managed/debug code
against optimized Rust. The relevant first questions are CLI latency for small
scripts and warm-cache ordinary file/tree transfers. SSH latency and very large
files require separate evidence and are not inferred from this workload.

[`tests/Performance/journeys.py`](../../tests/Performance/journeys.py) generates
equivalent 0.7/0.8 scripts and source-free objects, freezes both executable images,
records their versions/hashes, and alternates AB/BA process invocations. It checks
bytes and native file metadata outside each timed interval. The maintained
[normal-user contract](normal-user-validation.md) remains authoritative: atomic
same-parent replacement and at most one file fsync, without recovery registries
or filesystem-wide syncfs. A faster copy achieved by removing that contract is
not an equivalent result.

Default cases are startup, check of one/128 transfers, 4 KiB ordinary file
replacement, and a 256-file tree with 4 KiB per file. Source and object execution
are measured separately. There are two warmups and nine samples, with no
page-cache eviction; subprocess launch and normal CLI work are included.
The small check versus 128-operation check separates fixed startup from scaling.
Source versus object separates frontend costs from runtime work. Single-file
versus tree distinguishes fixed overhead from per-member I/O and publication.

## Baseline preparation

The existing root `target/release/msync` initially reported 0.6.6 and was not used
as the 0.7.3 baseline. The preserved tree was rebuilt with:

```sh
cargo build --release --locked --manifest-path legacy/Cargo.toml --bin msync --target-dir target
```

The resulting optimized 0.7.3 image is retained under
`.temp/performance-v080/baseline/msync`; the benchmark makes another frozen copy
and records its SHA-256 before measuring. A repeated real tree transfer confirms
equivalent container semantics (`input-tree -> output-tree/`) and the intended
destination `output-tree/input-tree`. The comparison waits for an actual native
published 0.8 image. The harness's existence is not a performance result.

## Results

Native AOT implementation and integration are in progress. Record the final
image identities, environment, commands, measurements and causal interpretation
here after the actual comparable run. Do not extrapolate temporary managed-build
timings to distributed-package performance.
