# Collections and handwritten SHA-256 across five languages

This Linux x86-64 probe compares application-level collection APIs, not five
ports of a common data structure. The SHA-256 row is a separate comparison of
the same scalar compression algorithm, written in each language.

| Workload | Measured work | Implementations (C++ / C# / Java / Python / msync) |
| --- | --- | --- |
| Red-black tree | Fresh set; insert 8,192 distinct keys; query 8,192 hits and 8,192 misses | libstdc++ set / SortedSet / TreeSet / handwritten left-leaning red-black tree / standard Set |
| Heap | Fresh queue; push 8,192 keys; check the top and pop 8,192 times | priority_queue / PriorityQueue (quaternary) / PriorityQueue / heapq / standard Heap |
| Hash table | Same insertion and membership trace as tree | unordered_set / HashSet / HashSet / built-in set / handwritten fixed-capacity linear-probing set |
| SHA-256 | Full scalar message schedule and 64 rounds for each of the 17 padded blocks of a deterministic 1,024-byte input | Handwritten in all five; no library digest, external process, hardware SHA instruction, or file read in the timed kernel |

The tree and hash traces use a permutation of even integers and interleaved
adjacent odd misses; insertion, lookup, allocation and container growth (where
applicable) are measured. The msync hash table uses a 50% target load, numeric
sentinel, multiplicative hash and no deletion/resizing. Python's tree is pure
Python and its black height, color and order are checked outside timing; its
heap and hash table use CPython's built-in implementations. The red-black
variants, allocation policies, key boxing and heap arities are not held
constant. Do not extrapolate the timings to every language workload.

SHA-256 padding and decoding to big-endian words are done once in Python.
The host reads the same words before timing. msync uses exact binary64
integers and initializes its typed buffers from embedded constants, instead
of native 32-bit bitwise instructions. Each sample checks all 256 result bits
against an independent hashlib oracle outside timing. Both `abc` and a
56-byte message that forces a second padding block are checked first.
The no-lookup control changes only the AND implementation: test/subtract
powers of two from most to least significant bit. XOR is `a+b-2*and(a,b)` in
both variants. This removes the bit lookup table, not the SHA schedule buffer.

Run with Python 3, .NET 10 SDK, GCC and a JDK:

```sh
python3 tests/Performance/LibraryMatrix/run.py --msync /absolute/path/to/msync
python3 tests/Performance/LibraryMatrix/run.py --msync /absolute/path/to/msync --compare-and
```

A release CLI generates optimized msync object bytecode. A separately built
Native AOT harness hosts the repository's VM and times execution of a preloaded
module, including VM creation but excluding source compilation, object-file
load and process startup. The C# column uses CoreCLR JIT with the same harness
and its own C# kernels. Java uses HotSpot; the C++ column uses GCC C++20
`-O3 -march=native`. Each language gets two fresh processes in forward and
reverse order on one logical CPU, warmed then measured in batches appropriate
to its speed. Keep the raw data, report medians, and avoid concurrent builds;
this is not a cross-platform acceptance or a statistical tail estimate.

References: [SHA-256 definition, FIPS 180-4](https://csrc.nist.gov/pubs/fips/180/4/upd1/final),
[Sedgewick's left-leaning red-black trees](https://sedgewick.io/wp-content/themes/sedgewick/papers/2008LLRB.pdf),
and [.NET's quaternary heap](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections/src/System/Collections/Generic/PriorityQueue.cs).

## Observed on the 0.8.6 Linux candidate

GCC 14.2, .NET 10.0.14, OpenJDK 25.0.5 and CPython 3.15.2 ran on the same
KVM Linux x86-64 host. All values below are medians in milliseconds per
complete workload, not per operation. The report pools the two fresh
processes, with the msync tree's individual fork medians shown separately
because the pooled number hides a large drift.

| Workload | C++ | C# | Java | Python | msync |
| --- | ---: | ---: | ---: | ---: | ---: |
| Red-black tree | 3.41 | 8.53 | 2.93 | 46.11 | 37,671 |
| Heap | 0.886 | 7.70 | 1.91 | 6.53 | 25,292 |
| Hash table | 1.37 | 0.459 | 0.317 | 3.28 | 872 |
| SHA-256 (1 KiB) | 0.00595 | 0.0102 | 0.00654 | 4.98 | 3,419 |

The msync tree fork medians were 30.56 and 61.01 seconds (all ten samples:
29.44–66.63 seconds). The cause of the drift was not isolated. A single
independent spot check on a separate logical CPU took 25.63 seconds after
binding outside the timer (0.066 seconds). SHA-256 likewise took 3.28 seconds
from a pre-bound executable after 0.042 seconds of binding. The orders of
magnitude remain without runtime relocatable preparation.

On a second logical CPU, the two checked SHA-256 variants with identical input,
host and O2 settings measured 3.433 s (nibble lookup) and 3.631 s (arithmetic
AND, 5.8% slower). The arithmetic version tests and subtracts one descending
power of two at a time and requires no bit table. Since both variants still
expand each 32-bit AND and XOR into many interpreted operations, the
SHA-256 ratio does not isolate VM dispatch overhead on otherwise identical
primitive operations.

Raw receipts from this run are in
`artifacts/benchmarks/library-matrix-v086/{result.json,and.json,staged-control.log}`.

## Follow-up controls

The table above is the original measurement, not a claim of steady-state
CoreCLR performance. On the unchanged DLL, ten warmup batches left
`PriorityQueue.MoveDownDefaultComparer` and `SortedSet.FindNode` at instrumented
Tier 0 even when the outer loop was already optimized. Twenty to one hundred
batches gave roughly 1.2–1.3 ms for the heap and 2.9–3.3 ms for the tree, versus
8–9 ms under the shorter warmup. Disassembly after longer warmup shows
optimized Tier 1 versions. The driver now defaults to 100 C# warmup batches;
use `--csharp-warmups 10` to reproduce the historical protocol. Per-runtime
warmup should be justified by tier or convergence evidence rather than made
nominally equal across runtimes.

The generated key order interacts with the default hash-table layout. The
tested libstdc++ growth policy settles on 10,273 buckets, of which only 2,710
are occupied by the 8,192 keys. The tested Java HashMap bucket mapping uses
8,192 distinct buckets out of 16,384 and maps every adjacent odd miss to an
empty bucket. In a phase probe, pre-sizing libstdc++ to 16,411 buckets brings
the original C++ hash workload from about 1.35 to 0.86 ms and keeping the
same reserved node layout but specializing the bucket modulus gives about
0.64 ms. These probes isolate important costs in the tested container, not a
language-level ordering. Tree destruction is timed for C++; Java and C# drop
references and pay reclamation on their collectors' schedule.

The [VM continuation ablation](../../../docs/design/contextual-io-backend.md#compute-only-continuation-admission-086-investigation)
explains why the msync timings remain large after initial binding: execution
still repeatedly enters the contextual I/O planner for ordinary call paths.
An 8,192-key bound tree run triggers 985,902 preparations. Disabling that
admission in a diagnostic-only image reduces one matched pair from 26.03 to
5.30 seconds; the remaining cost is real VM and data-structure work, not
source compilation. The original matrix is unchanged as historical data.
