# Adaptive interpreter experiments for 0.8

## Question and boundaries

The November 9, 2026 follow-up asks for parallel research and independent
validation of JVM/CoreCLR-inspired runtime mechanisms. The starting point is
[the mechanism review](managed-runtime-optimization-v080.md), not an assumption
that a new JIT or every inline cache must be useful.

The common experimental product source is
`887fc6230fa26bd8ed4c9ae39ff76471d78f8b99`. Each experiment freezes that source
under `.temp/runtime-adaptive/`, applies only its recorded prototype patch, and
keeps production `src/` unchanged. Current package/editor acceptance continues
independently. Experimental results do not certify a new publication payload.

## Independent work and acceptance

| ID | Question | Prototype boundary | Required discriminator |
| --- | --- | --- | --- |
| P0 | Can adjusted calls select a code index without allocating a replacement FunctionValue? | Call dispatch only; preserve actual native identity, captures, deferred arguments and invocation ownership | Same code with different captured cells; target reassignment; callee evaluated before an argument changes its binding |
| P1 | Does a site-local receiver/target cache repay its guards and metadata? | A genuinely unresolved call or unhinted field site; existing successful field hints are a control | Layout rebind, current captured-cell payload on every hit, guard miss, generation/context invalidation |
| P2 | Does a precompiled fused handler reduce actual residual O2 dispatch? | One small same-block sequence, retaining original PCs/spans and cancellation obligations | Selected error source, absent pattern, ordinary execution ownership and identical results |

Each leaf owns its own `tests/Performance/Adaptive*` project and patch artifact.
An independent review lane challenges assumptions and executes small falsifiers;
it does not repeat the implementer's complete test matrix. Product code is not
adopted merely because a prototype is correct or one microbenchmark improves.

## Measurement contract

1. Establish a real retained O2 operation before constructing a fast handler.
   Record the residual opcode/call-site counts. An O0-only discriminator is not
   evidence for adopting an optimization in normal O2 execution.
2. Compare source-matched Native AOT images on the same host. Record source,
   patch and executable hashes, framework/build metadata and exact invocation.
3. Measure preparation/first-use separately from prepared execution. A lookup
   moved into plan construction is not a free saving. Report allocation and
   milliseconds p50/p95, with original observations retained under `.temp`.
4. Alternate control/candidate observations. Coordinate native builds and timing
   windows rather than running the three experiments against one another's CPU
   load. Real editor acceptance takes priority over speculative research timing.
5. Use consumed result/byte oracles and relevant codec routes. Include a tiny or
   literal-return negative control, an already successful field hint where
   applicable, and a selected ordinary I/O control. Do not infer startup or I/O
   wins from a prepared-VM dispatch measurement.

## Shared semantic obligations

The actual callable value is evaluated before its arguments. An argument which
changes the source binding must not retroactively change that already-evaluated
callee. A target cache stores resolution, not a closure snapshot: captures and
native identity still come from the actual invocation.

Likewise a layout cache stores a field offset, not a mutable payload. Every hit
must observe the current field/captured Cell. A retained name/layout hint is
already a guarded specialization and is not disabled to manufacture a win.

Any experimental state belongs to the code-image/interpreter generation and
matching binding-context projection. REPL publication and old deferred ranges
must retain the existing latest-code/origin-context pairing. No cache contains
durable filesystem observations, authentication results or transfer outcomes.

Fused operations retain per-execution checks and original diagnostic locations.
Calls, barriers, iterator callbacks and owner-return joins are not transparent
boundaries. Native AOT handlers remain statically compiled C#; no Reflection.Emit
or executable-page machinery is introduced.

## Native results and adoption decision

All three experimental lanes completed functional and Native AOT observations.
The [independent review](runtime-adaptive-review-v080.md) accepted the same 14
raw-IR/codec ABI, capture, guard and error discriminators against control and
each overlay. That review is semantic evidence, not extra timing samples.
Production source was not modified by these experiments.

| Experiment | Consumed workload and VM p50, control → candidate | Allocation / cost | Decision |
| --- | --- | --- | --- |
| P0: no adjusted-call clone | Stable size-256 calls: 0.77566 → 0.74608 ms; captured calls: 0.79815 → 0.78319 ms; changing targets: 1.01520 → 1.04131 ms | Exactly 40 B less per adjusted call, or 20,480 B for the 512 executed calls. Literal/4 KiB allocation controls unchanged | Consider the small integer-target allocation simplification separately; no general adaptive framework justified |
| P1: two-entry field PIC | Actual reordered-layout source, 4,096 iterations: 13.263 → 12.803 ms; unhinted artifact: 11.837 → 11.800 ms | Cold PIC state +288 B. Successful-hint control, which bypasses the PIC, improved more than the candidate's principal case | **Defer adoption**: causal latency benefit is not established |
| P2: same-block addition fusion | Parameterized sum, 4,096 iterations: 3.185 → 2.976 ms; shared-cell sum: 3.140 → 3.337 ms | Long-loop allocations fall about 29%, but plan creation adds 304 B on a site-bearing image and no-pattern first execution adds 48 B | **Retain as prototype, not default**: lower allocation does not compensate for the demonstrated captured-loop slowdown |

These rows compare each experiment with its own source-matched control; their
different workloads and VM-operation boundaries must not be ranked against one
another. P0 includes interpreter construction in its warmed VM operation; P1
deliberately creates a fresh interpreter/cache each operation. P2 separately
records preparation, prepared execution and first execution. None is a measured
startup or whole-command improvement. The native image layouts differ, so small
time changes without a matching mechanism/control result are not attributed to
the prototype.

P0's six executable input hashes match exactly across images. Its deterministic
40-byte reduction follows the removed record allocation, while changing-target
and process-wall observations show why this cannot be advertised as universal
runtime acceleration. P1's successful existing hint remains the preferred
mechanism: the PIC does not help that path, and its unchanged unhinted-artifact
result is an important negative result. P2 also bypasses generic combination
callback allocation on its numeric fast path, so its allocation reduction must
not all be attributed to fewer interpreter switch dispatches.

The complete setup, exact source/patch/native identities, first-run costs,
p50/p95 and original-data locations are preserved with each committed probe:

* [P0 adjusted dispatch](../../tests/Performance/AdaptiveDispatch/README.md)
  (`19da1c2`), seven alternating process pairs per case, three warmed samples
  of eight executions per process.
* [P1 field cache](../../tests/Performance/AdaptiveInlineCache/README.md)
  (`b168bca`), three alternating process pairs and nine samples per process.
* [P2 fused addition](../../tests/Performance/AdaptiveFusion/README.md)
  (`2e8c34b`, results `5fd988d`), three alternating pairs and seven samples per
  process at sizes 1, 32 and 4,096.

No additional experiment is required to state the current recommendation:
first consider the simpler P0 allocation change; leave PIC and fusion disabled.
A future fusion revision could reduce plan lookup cost, but that is an unmeasured
proposal rather than a result of this study. General online tiering, OSR, native
JIT, dense counters and GC replacement remain outside the justified scope.

## Existing compiler failure found by independent validation

The callee-before-argument discriminator exposed an existing optimizer error in
the frozen product control: source O0 returns `AB`, while source O1/O2/O3 return
`AA`, including bound and codec routes. The independently constructed runtime
sequence returns the required `AB`, and P0 preserves that sequence. The failing
source and unoptimized/optimized IR were handed to the language/optimizer owner
for a separate production repair. They are not counted as a passed prototype
equivalence check merely because control and candidate share the error.

This outcome demonstrates the purpose of separate research, implementation and
validation lanes: a plausible upstream mechanism can be correct but unprofitable,
while a new discriminator can find an unrelated existing error. Neither finding
requires adding an adaptive runtime framework to deliver its value.
