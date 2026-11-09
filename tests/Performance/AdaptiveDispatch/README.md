# P0: selected-target dispatch without an adjusted FunctionValue

This is an experimental overlay, not a product change. `prepare.py` archives an
exact product revision twice under repository-root `.temp`, adds this same probe
to both, and applies `no-clone.patch` only to the candidate. The manifest records
the source archive, patch and probe SHA-256 identities. Do not benchmark a live
checkout in place of the frozen images.

```text
python3 tests/Performance/AdaptiveDispatch/prepare.py \
  --ref REVISION --destination .temp/runtime-adaptive/dispatch/frozen
```

Publish the probe project in each snapshot with Native AOT and the same release,
RID and SourceRevisionId settings. `compare.py --control FILE --candidate FILE
--output DIRECTORY` alternates the images. Reports distinguish compilation plus
binding/codec preparation, the first execution, warmed VM execution including
interpreter construction, and complete subprocess wall time. These cumulative
phases must not be added together. A 4 KiB file copy and a literal-return image
are negative controls, not synthetic arithmetic speed claims.

## Prototype boundary

The existing interpreter lazily parses textual dispatch tables once, but creates
an adjusted `FunctionValue` for a selected B or textual target on every call.
The overlay resolves the target to an integer and passes it through the existing
scheduling entrypoint. Actual captures, native identity, argument normalization
and `RunFunctionAsync` invocation ownership remain unchanged. It does **not** add
a second dense predecode table or a polymorphic cache merely to justify a label.
`SpecializeClosure` still constructs a real adapted callable where one is needed.

The source workloads retain actual O2 calls: a generic invocation wrapper calls
a runtime-selected function, with stable targets, changing targets, and functions
sharing one code identity but carrying different captured locations. Every timed
iteration consumes and checks its final String. The report includes residual
direct/textual call counts and the complete bound artifact hash, so an experiment
whose calls were removed by static optimization is visible.

`--check` also exercises source, relocatable and executable codec routes, actual
native B/text dispatch, missing dispatch errors, invalid B/callee behavior, an
explicit raw-IR callee-before-argument mutation sequence, and old deferred REPL
callers retaining directory A while new explicit captured Paths retain B.
The independent `AdaptiveReview` project can run against either snapshot using
`-p:MsyncSourceRoot=ABSOLUTE_SNAPSHOT_ROOT`.

## Separate existing compiler discriminator

`--check-source-order` deliberately remains a separate failing discriminator for
the frozen `887fc62` product. Its source evaluates a callee A, evaluates an
argument that changes the same binding to B, then invokes the binding again.
O0 returns the required `AB`; O1/O2/O3 return `AA` in both control and candidate.
The independent raw-IR sequence returns `AB`. This result is not counted as a
successful prototype-equivalence check: it was handed to the frontend/optimizer
owner as a pre-existing static optimization issue. Keeping the discriminator
separate permits measuring the other correctly executed O2 workloads without
silently replacing their semantic oracle with the broken implementation.

## Native experiment result (November 9, 2026)

Product source: `887fc6230fa26bd8ed4c9ae39ff76471d78f8b99`. The only product
overlay is `no-clone.patch`. Both images use SDK 10.0.403, Release linux-x64
Native AOT, the same SourceRevisionId and exactly the same probe. Image hashes:

* Control: `0366cd3fbb3777805b6584c8b8ab18c188f619d40832bbc331cbe55c600bfbd0`
* Candidate: `149e7db487f7e9a66d866c6470b57fd381847472bcee31ca0048c2405ff6502b`

Seven alternating process pairs per case each produce three warmed observations
with eight executions per observation. The complete executable artifact hashes
match for all six cases. At size 256 there are two residual call sites (one B,
one textual dispatch) and 512 dynamically executed adjusted calls. The generic
source chooses its initial target with one real `exists ./flag` operation; this
is not represented as a pure call-handler-only benchmark.

| Case | Prepared VM p50 control / candidate ms | Allocated bytes control / candidate |
| --- | ---: | ---: |
| Stable call, size 1 | 0.06116 / 0.06048 | 7,472 / 7,392 |
| Stable call, size 256 | 0.77566 / 0.74608 | 380,016 / 359,536 |
| Target changes, size 256 | 1.01520 / 1.04131 | 528,288 / 507,808 |
| Captured callable, size 256 | 0.79815 / 0.78319 | 382,104 / 361,624 |
| Literal negative control | 0.00385 / 0.00391 | 1,672 / 1,672 |
| 4 KiB copy negative control | 1.53344 / 1.61829 | 17,754 / 17,754 |

The deterministic gain is **40 bytes per adjusted call**, exactly accounting
for the removed FunctionValue. Stable/captured-call median changes are small;
the changing-target case does not become faster. Complete process medians also
do not consistently improve (stable-256: 119.873 / 122.815 ms; literal:
54.011 / 55.868 ms). The IO control's tail is essentially unchanged while its
median varies; these samples do not establish a universal no-regression claim.

Recommendation: **a small allocation-reduction candidate, not evidence for an
adaptive execution framework**. The product can consider the simpler integer
target flow separately; a general predecode/PIC layer needs its own evidence.
Do not sell this result as faster startup or file copying, and do not count the
known source compiler failure as a passed equivalence test. Independent raw-IR
review accepted 14 ABI/guard/capture/error/codec cases against the overlay; both
Native probes accepted the default functional route including deferred REPL
directory/capture ownership. No production files were changed.

Raw observations, all first-run/preparation/process times and the frozen input
manifest are under `.temp/runtime-adaptive/dispatch/paired-final/summary.json`
and `.temp/runtime-adaptive/dispatch/frozen/manifest.json`. Disposable data can
be removed; this description and the committed overlay/probe preserve the
experiment's setup and conclusion.
