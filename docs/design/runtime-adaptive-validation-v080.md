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

## Decision record

The experiment owners will append their bounded results and an explicit
**adopt**, **retain as prototype**, or **defer** recommendation here. Adoption
requires an implementation decision separate from this research task. Complete
experiment-specific raw records and reproduction commands belong beside each
probe; this note integrates the engineering conclusion without duplicating all
samples.
