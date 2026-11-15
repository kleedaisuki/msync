# Frame and GC reviewer self-statement

## My role and starting point

I was an independent reviewer of frame layout, environment ownership, call arguments and managed allocation. I was not the designer or implementer of the representation, frame pool, environment view, SROA integration or register planner. I cannot supply those authors' original intentions, and I do not claim ownership of their design defects or repairs.

I received the frozen benchmark cohort, measured performance results, known repaired defects and investigation restrictions in `artifacts/diagnostics/llvm-sroa/reviews/BRIEF.md`. I treated those results as supplied experimental evidence, not as measurements I performed. My assignment explicitly prohibited production edits, builds and new wall-time benchmarks. I first read the brief and relevant design contracts, then inspected implementation paths and preserved diagnostic JSONL/artifact inventories.

## Why I used this method

The concrete question was whether the extra managed allocation came from per-callback frames, environment wrappers, argument snapshots or changed lifetime/ownership. I traced where each object is created, reused and cleared, and matched those paths to executed callback counts and allocation counters. That is more discriminating than treating large cumulative stack-frame counters, struct copies or opcode totals as heap allocation. I also inspected the allocation-measurement boundary so that profiling work would not be mistaken for normal execution behavior.

I used small read-only parsing commands against existing receipts. I did not replay the production liveness equations, collect allocation stacks, run a heap profiler, or execute an end-to-end workload myself. The separate register review's structural replay is not my work.

## What I actually established

For the observed Unicode and policy workloads, callback frame shapes, frame/activation reuse, argument-copy counts and zero argument-snapshot counts matched between cohorts. The main frames became smaller. These observations exclude increased callback-frame allocation or snapshot creation as explanations of the recorded allocation delta; they do not show that callback execution is free.

I distinguished two Function operations. The hot ready environment projection moves structs and existing references without creating an environment wrapper. Composition can allocate a `NativeEnvironment` view, but only hundreds of ready compositions were recorded, not one per hot callback. The view preserves the capture storage address rather than copying capture contents. I found no demonstrated new frame/capture lifetime or GC-retention defect in those paths and observations.

I confirmed an allocation-heavy shared waste outside the callback hot path: register planning runs CFG, LiveAfter and PendingSlots before testing whether any region is eligible, although the current backend's all-false RawWritable makes every region ineligible. I specifically identified the per-visit heap-allocated NativeInstruction record clone in PendingSlots, alongside liveness HashSet traffic. I did not establish that the clone count increased in the candidate or assign the observed byte delta to it.

I also established that the execute allocation metric is process-wide and includes first-use tier compilation and observer work during execution. Profiling forces allocation of a NativeWorkScope for each callback despite frame reuse. That is a shared diagnostic-mode cost with matching callback counts, not a demonstrated explanation of the cohort difference. Final profile publication is outside the sampled execute allocation bracket.

## Limits and shortcomings of my contribution

My review narrows attribution rather than completes it. I did not measure exclusive liveness allocation, normal telemetry-off allocation, GC retained size or marginal CPU cost of generic Function projection. The reported +7,299,664 Unicode bytes and +4,255,984 policy bytes remain incompletely attributed. The policy observation belongs to the literal-cache cohort specified in the brief; it is not an independently observed final-candidate profile.

My recommendation to reject an empty register plan before running its analyses is supported by the execution path. Its recoverable milliseconds and bytes were not measured by me. A smaller frame or unchanged counter also cannot exclude every possible lower-level CPU effect, and matching observations for these workloads do not establish a universal ownership theorem.

My original report used the inherited term “register-allocation time” for the diagnostic metric. It should not be read as exclusive LiveAfter or PendingSlots duration: the metric covers broader tier-layout construction. Likewise, my statement that no retention defect was found is a bounded review result, not a heap-profiler measurement or a claim that retention was exhaustively tested.

## Corrections required if the integrated narrative goes further

The root should not describe me as an author confessing why I designed these mechanisms, as having reproduced the benchmark, or as having proven that all extra allocation or the complete latency regression comes from liveness. I established allocation sites, matching executed counters, ownership boundaries and a confirmed unnecessary analysis path. The separate structural-growth probe and any later product fix or measured improvement belong to their actual authors and experiments.

I made no production edits, builds, benchmarks or commits. My deliverables were the frame/GC review report, coordination summaries and this self-statement. I have no additional evidence that overturns the root's narrative; the corrections above state the boundaries needed to represent my contribution accurately.
