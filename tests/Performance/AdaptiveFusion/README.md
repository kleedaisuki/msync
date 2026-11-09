# P2: precompiled same-block addition fusion

This is an experimental overlay, **not an installed runtime feature**. Product
`src/` is untouched. `prepare.py` archives one exact revision into root `.temp`,
adds `Interpreter.Fusion.cs.template` to only the candidate, and records source,
probe and modified-file hashes. Keep control and candidate builds separate.

The source-generated O2 workload is a real parameterized Range accumulator. Its
callee cannot be inlined because it owns iteration; its parameterized loop is
not certified for compile-time evaluation. In the residual executable the
`LoadLocal; LoadLocal; Add` sequence remains in one basic block. A second source
uses an explicitly imported mutable outer location. These are not O0-only or
already constant-return performance cases.

The candidate builds site-local flags once per code image. The handler reads
both live values in source order, preserves all three cancellation checks, and
uses the original Add instruction/span for exceptions. Number addition uses the
same sequential double operation; String/deferred operands use the original
generic combination semantics with the values already read. It does not cross
block entries, calls, barriers, iterator callbacks or invocation owners. Session
image changes rebuild the plan. Serialized bytecode is not modified.

The earlier suggested `LoadLocal; GetMember` pair was deliberately rejected:
GetMember is a MayFail boundary and starts its own current basic block. A claim
of same-block fusion for that sequence would have measured zero eligible sites.

```text
python3 tests/Performance/AdaptiveFusion/prepare.py
# In each frozen control/candidate tree:
dotnet publish tests/Performance/AdaptiveFusion -c Release -r linux-x64 -o NATIVE_OUT
NATIVE_OUT/msync-fusion-probe --verify --sizes 1,32
```

Run timing only after all builds have stopped. Alternate the two native images,
record their hashes, and retain both preparation and prepared/first-execution
costs; do not report a warm-loop gain as a startup or file-copy improvement.
`last`, `literal`, and 4 KiB `copy` are negative controls with no eligible site.
All scenarios consume their result or check actual file bytes. The probe checks
source, relocatable, executable and binary round-trip execution separately.

Selected arithmetic errors, String fallback, IEEE negative zero/NaN and
cancellation have bounded mechanism discriminators. Independent review is
separate from this experiment's own byte/value oracle.

## Native AOT experiment: November 9, 2026

Decision: **retain as an experimental prototype; do not enable by default**.
Allocation decreases are real, but latency improvements are not consistent
across ordinary and captured-local loops, and preparing a plan has a cost.

The exact product revision is `887fc6230fa26bd8ed4c9ae39ff76471d78f8b99`.
The source archive SHA-256 is
`bc623ae1bbe26e95e470f08466022d669ef97db52e6e452a0a35cd7fa962be3c`.
Only the candidate's experimental Interpreter hook and helper differ:

- Control Native AOT executable:
  `b625584a93fe8792fa83dca1c712e80c6182c66b58b3bafb7a41ef2d83da052b`.
- Candidate Native AOT executable:
  `1d7d2c8d70c95e746ab648e7586a433a2d31b1f8d6220a57ed3d4d7eb08c6ace`.
- Candidate Interpreter SHA:
  `c5253ec5603a69f17fb6087ac150a62e1e7c7d2ca6108375c4776eda53ddc986`.
- Helper SHA:
  `8e2fb942be2bbd82d549e20899c50de80bf650c2c37ca1ce7e4d53d81a322794`.

Both images used .NET 10 Native AOT on this Linux x64 host. Builds completed
before measurement. Three process pairs alternated control/candidate and
candidate/control order. Each process collected seven timed samples after three
warmups, giving 21 observations per reported cell. Iterations were calibrated
per stage, with allocation measured separately. Every image passed 75 source,
relocatable, executable, codec, shape, actual-copy, IEEE, error-span and
cancellation checks. The independent review passed 14 additional raw-IR guards.
These are different contracts, not a combined universal correctness claim.

Prepared-VM results (ms, bytes per execution):

| Case | Control p50 / p95 | Candidate p50 / p95 | Allocation before / after |
| --- | ---: | ---: | ---: |
| Sum, 1 item | .00703 / .01090 | .00821 / .01100 | 2,160 / 2,008 |
| Sum, 32 items | .03051 / .04718 | .02998 / .04481 | 18,592 / 13,728 |
| Sum, 4,096 items | 3.18460 / 4.09650 | 2.97600 / 4.09390 | 2,164,552 / 1,541,960 |
| Captured sum, 1 item | .00587 / .00669 | .00415 / .00581 | 2,448 / 2,296 |
| Captured sum, 32 items | .02687 / .04610 | .02602 / .02955 | 18,880 / 14,016 |
| Captured sum, 4,096 items | 3.13980 / 3.72130 | 3.33690 / 13.62560 | 2,164,840 / 1,542,248 |
| Literal return (no site) | .00108 / .00126 | .00075 / .00103 | 432 / 432 |
| 4 KiB copy (no site) | 1.62910 / 2.78655 | 1.72530 / 2.90860 | 11,848 / 11,848 |

The long ordinary sum saves about 6.6% at the median, while the captured long sum
is about 6.3% slower and its observed tail is worse. Both save approximately
29% allocation. The no-pattern `last` loop also varies, so unrelated timing
movements are not presented as fusion wins. The copy has identical prepared-VM
allocation; these samples do not establish a file-copy speedup or a causal IO
regression.

The plan increases preparation allocation by 304 bytes for a sum function with
one eligible site, and by 48 bytes for the no-site controls. For the long sum,
construction p50 changes .03032 -> .03950 ms; first execution changes
3.21330 -> 3.05110 ms. For the long captured sum, construction changes
.02347 -> .04409 ms and first execution changes 3.42290 -> 3.07690 ms. Warm
execution, first execution and plan construction are therefore separate results,
not timings to add together or mix into one claimed improvement.

The canonical plain sum has 14 callee instructions, including the retained
same-block triple at PCs 7/8/9. With N supplied items it executes that triple N
times. The source-derived canonical dispatch count is `8*N + 12`; the fused loop
executes `6*N + 12` handlers while retaining the same logical operations and all
cancellation checks. At 4,096 items that is 32,780 -> 24,588 dispatch iterations.
This does not imply a corresponding whole-program speedup.

A specific prototype overhead remains: site flags are looked up by function
reference at each block entry, and the interpreter tests the flag on every
instruction. Moving plan selection to function entry would be a sensible next
bounded refinement, but it was not measured here. The numeric helper also
avoids the generic arithmetic callback allocation; the current experiment does
not attribute the allocation change exclusively to fewer switch dispatches.
No product source, artifact format, CPU code generator or garbage collector was
changed.

Raw reports, image hashes and provenance are retained in root
`.temp/runtime-adaptive/fusion/manifest.json`, `control-verify.json`,
`candidate-verify.json`, and `results/summary.json` with the six process logs and
unaggregated measurements. Reproduce the pairing with `compare.py --control ...
--candidate ... --output .temp/runtime-adaptive/fusion/results` only after the
other experiments/builds release the measurement host.
