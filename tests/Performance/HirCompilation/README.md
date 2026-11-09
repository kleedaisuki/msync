# Typed HIR compiler-cost observations

This probe separates two questions:

1. What does a user pay for a fresh native `check` process?
2. What time and allocation traffic does the compiler itself require after its
   code and embedded standard-source caches are warm?

`measure.py` runs six small, real source shapes: a scalar binding, Boolean
composition, shell cwd selection, List construction/count, a type relationship,
and a B-tree with 24 inserts followed by lookup. O0 exposes construction work;
default O2 includes the optimizer. Compile/check does not execute the source IO
or native storage operations. The returned program and encoded object are
retained, and output sizes/hashes are recorded outside timing.

## Frozen images

The published 0.8.0 baseline is commit
`637bd091e62e013f4003a1fa7e2c8f0b5e917140`. Its Linux archive SHA-256 is
`bd5310545263db3abb2fe25c6c3c2fcf4ebe1e23de22a8ce288a1a4611cc81f2`;
the extracted executable is
`10d27794ccf2dd4c5638fd6d77d3510de5b6f1dd61ac0e0557382e3cf36c32ad`.
The immutable local copy and provenance are under
`.temp/typed-hir-baseline/{native,receipt.json}`. Never use a moving checkout's
output as the before image.

The managed component baseline is built from that exact Git commit under
`.temp/typed-hir-baseline/source`, using isolated outputs and the local pinned
SDK. Its measured runtime/assembly identities are in every report. Managed
results are not Native AOT timings, and whole-process numbers are not summed
with component numbers.

```sh
dotnet build tests/Performance/HirCompilation/Msync.HirCompilation.csproj \
  -c Release -p:FrozenAssemblies=/absolute/frozen/assemblies \
  --artifacts-path .temp/hir-cost/probe-build -o .temp/hir-cost/probe
python3 tests/Performance/HirCompilation/measure.py \
  --binary /absolute/frozen/native/msync \
  --component .temp/hir-cost/probe/msync-hir-compilation \
  --source-revision COMMIT --samples 7 \
  --work .temp/hir-cost/inputs --output .temp/hir-cost/report.json
```

The managed probe references frozen assemblies directly and never builds the
shared product projects. Its ordinary compile-total API works for both released
and candidate compilers. Separate semantic analysis, HIR lowering and encoding
measurements will use the actual new phase APIs once those contracts exist;
the baseline implementation has no independent analysis-only timing to report.

One untimed launch precedes native observations. Each component process records
its first compile, performs three warmups, and retains seven default warm
observations. Tiered compilation is disabled identically for component runs.
Allocated bytes are current-thread managed allocation traffic, not retained
heap size or RSS. Reports keep every sample and p50/interpolated p95; no timing
threshold is a CI gate. Coordinate measurement windows with build owners before
interpreting small differences.

## Initial baseline

The first native observations used five fresh processes per case/level on the
development host with warm OS caches. Current source development was active,
so these are saved product controls, not a claim about an isolated machine's
portable latency limit. Raw data is `.temp/typed-hir-baseline/native.json`.

| Native check, p50 milliseconds | O0 | O2 |
| --- | ---: | ---: |
| Scalar binding | 14.23 | 15.78 |
| Bool | 18.33 | 15.63 |
| Shell cwd | 18.30 | 17.79 |
| List construction/count | 32.00 | 46.17 |
| Type relationship | 15.85 | 19.48 |
| B-tree construction/lookup | 1094.39 | 1562.91 |

Use a matched alternating before/candidate observation for final attribution.
The HIR migration must also measure its own analysis/lowering allocation and
time; a better residual program does not excuse unbounded semantic analysis.

The same-host managed Release component baseline uses SDK 10.0.403, seven warm
observations per source/level, and the exact published source archive. Raw data
and the three product assembly hashes are in
`.temp/typed-hir-baseline/component.json`.

| Warm compile-total | O0 p50 ms / allocated bytes | O2 p50 ms / allocated bytes |
| --- | ---: | ---: |
| Scalar binding | 0.0215 / 9,080 | 0.0682 / 22,064 |
| Bool | 0.2153 / 50,864 | 0.6432 / 159,816 |
| Shell cwd | 0.3350 / 73,960 | 1.0483 / 227,536 |
| List construction/count | 3.9702 / 927,712 | 14.5070 / 4,432,800 |
| Type relationship | 0.0900 / 25,456 | 0.2395 / 51,312 |
| B-tree construction/lookup | 1475.9988 / 146,409,824 | 1991.6555 / 379,472,456 |

The B-tree case retains 577 functions and 33,127 O0 instructions (27,850 at O2).
This is a useful expensive semantic-construction case, not a requirement to run
the historical large mutation matrix during every compiler iteration. Allocation
is especially discriminating here; the concurrent development host's wall-clock
measurements require a fresh matched comparison before attributing a speedup.
