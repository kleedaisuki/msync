# Root coordinator's first-person account

## Responsibility and the decision summary

I own the integration direction, task decomposition, default pipeline, published
AOT builds, actual end-to-end executions, benchmark windows and user-facing
claims. Delegating a source family did not delegate that responsibility. This
account separates decisions supported by the recorded work from motives I
cannot independently reconstruct for another participant.

The defensible summary of my approach is that I treated the task as an
incremental migration onto the existing Native runtime. I put a managed LLVM
representation and upstream pass ports above that runtime, and used adapters
to keep it executable at each stage. I gave too much weight to continuity of
the existing implementation and too little to removing obsolete mechanisms and
preserving the upstream cost model across the entire path. That is not the
complete, faithful implementation the user requested.

## Why I retained the Native target

The user explicitly did not request the LLVM backend or its pipeline. The
existing language runtime also owns real pending values, resource work, persistent
cells and public Native artifact/call interfaces. Keeping an external Native ABI
was therefore an actual constraint, not an excuse to adopt LLVM code generation.

My mistake was treating existing internal representations and analyses as
similarly protected. They were not. Compatibility did not require canonical
nullable environment leaves where a producer already supplied an exact address,
nor unused register-region analysis, nor making all function code/name leaves
entry-prelude registers. Those were implementation choices and should have been
reviewed against the user's direct-pointer/layout direction.

## What I personally integrated and repaired

I selected SROA in the default compiler path with its upstream ModifyCFG option
and its own mem2reg promotion, while the broader InstCombine/target work remained
unfinished. I integrated the machine-scalar and Function-leaf cohorts and owned
the CLI/AOT builds. The real journeys then exposed failures at target ABI seams.
I repaired call-site PC recording after instruction-emitting callee selection,
naked source-function code materialization, and the compact Function memory-field
load bridge. Peers repaired actual-carrier PHI copying and capture-schema recovery.

Those repairs were real progress; they also show that I enabled an optimization
cohort before the whole target's representation contract had converged. Standard
LLVM transformations exposed missing ordinary pointer/PHI/aggregate/constant
cases. Instead of having established one coherent low-level contract first,
several cases were repaired as they appeared. The resulting adapter surface was
larger and costlier than the external compatibility contract required.

## How my task decomposition contributed

I divided work into pass families, query prerequisites, source production,
target selection and runtime support. That made parallel throughput possible,
but left cross-family responsibilities insufficiently explicit: which facts
belong to the source graph, which belong only to one target body, where literal
materialization occurs, which backend actually emits executable handlers, and
which analyses still have consumers after a backend switch.

I required workers not to build while the root owned the shared build tree.
That serialization was deliberate and appropriate; it is not evidence that
workers refused to validate their work. The failure was accepting local
source-family readiness as sufficient architectural readiness without doing
the cross-boundary review early enough. I should have required precise handoff
scope and consolidated it into the actual end-state assessment.

The standard emitter's all-false RawWritable choice illustrates the gap.
Fabricating the old raw-write facts or calling the old proof pipeline was not
acceptable. But leaving downstream whole-function liveness and pending analysis
running with zero possible register-region consumers was not necessary either.
I integrated that boundary without following the flags through the consumer.

## Why 'copied from LLVM' was an inadequate claim

I treated source algorithm bodies and call order as the center of fidelity,
while managed containers, ownership and target representation were often treated
as adaptation details. They change the work performed. OrderedSet subtraction
can preserve its members and order while reversing the upstream driving set and
cost model. Entry-prelude literals can reduce register count while tripling
liveness-set element traffic. A nullable address can preserve values while
losing two existing direct storage paths.

I should not have narrowed 'faithful implementation' to semantic similarity of
individual transformations. Upstream containers, worklists, invalidation and
analysis lifetime, plus the target's complete handling of transformed IR, all
matter to the requested result.

## What the performance work did and did not establish

I executed the actual source/detached-object/bound journeys and the frozen
telemetry-off benchmark pairs. The output contracts passed and the measured
regressions were preserved rather than reported as a speedup. The final corpus
and compile receipts do not show that the original O2 latency problem is solved.
That reporting was necessary, but it did not replace delivering the requested
performance and architecture.

The literal cache and tier direct-branch patches fixed specific defects.
I should not mistake those fixes, lower VM instruction counts or the amount of
ported source for end-to-end convergence. The latest independent review finds
remaining unnecessary analysis and representation work that the earlier fixes
left intact.

I also need to keep causal language narrower than some brief summaries. The
register-allocation statistic covers NativeTierLayout.Create, not exclusive
LiveAfter time. A Python replay establishes structural set traffic, not an
exclusive millisecond attribution. Equal MemorySSA Def/Use counts do not prove
identical graph shape or equal alias-query cost. Existing public raw-memory API
sequences must not be broken by a blanket initialization-marker rollback.

## Team state and authorship

Later fixers and independent reviewers must not be presented as original authors
of the problematic decisions. I requested their own accounts separately, then
recontacted earlier named contributors. Each statement identifies its actual
assignment and authorship rather than absorbing the entire architecture.

A concrete status failure occurred while collecting these statements: the
benchmark integration worker initially said no final AOT result existed. That
was its outdated handoff state, not current project state; I supplied the
versioned receipts and requested a correction. The correction is preserved in
its statement. This is another reason a local worker's completion/status must
not replace a shared, current integration record.

I cannot honestly invent an unrecorded original author's motive. I can own the
integration choices, describe their actual rationale and show where they were
wrong. The user had repeatedly specified simpler pointer/layout semantics and
rejected unnecessary proof machinery. Those instructions were not unclear;
continuing to let generic inherited mechanisms dominate the implementation was
my direction and integration failure.

## A direct original design account received in this turn

The original machine-scalar architect acknowledges recommending that a scalar
read require its whole byte extent to be initialized, rather than limiting the
proposal to supported raw views and existing source-field readiness. It did not
write production MarkInitialized; the exact implementation author is not thereby
identified. The proposal itself was an overgeneralization without a demonstrated
ordinary-path consumer. I should have challenged that obligation before letting
representation work broaden common storage maintenance. Exact machine bits and
pending physical types were needed; universal byte-readiness obligations were
not established by that need. This is a concrete example of a local completeness
idea becoming broader work that the language/user did not request.

## Current scope

This turn adds decision/accountability documents only. It does not repair the
product, run new builds or timing, create a new goal, or commit/push the statements.
The substantive next correction is to eliminate the known needless work and
preserve existing exact representation facts, not to add a new solver, gate or
another explanatory layer.
