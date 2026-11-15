# Builder publication: personal implementation account

## Role and available original context

I am the participant assigned `builder_publication`, not the original compiler
architect or an independent performance reviewer. My original assignment and
my own subsequent implementation context are available in this agent's
conversation. The large compiler-migration history preceding my creation was
inherited context. It is not evidence that I personally participated in those
earlier decisions.

Root first assigned me publication of the already staged TargetFolder and
TransformBuilder prerequisites. Root then explicitly extended my assignment to
integration of the staged integer/FP intrinsic folding dispatcher and host
adapter. A later assignment covered floating vector declarations and builder
result shapes. I did not own the original Range evaluation design, alias
analysis design, SROA selection, whole InstCombine implementation, Function
leaf SSA ABI, Native scalar/vector ABI, or default optimizer pipeline. I cannot
supply the original authors' motives for those choices.

This requested self-statement makes no product edit, build, timing run or
commit. The source and documentation inspections below are not a new
performance experiment.

## What I inherited versus personally changed

| Area | Inherited work | My actual contribution |
| --- | --- | --- |
| TargetFolder and builder | `.temp/llvm-target-folder/` contained the folder, constructor strategy, scalar/memory/intrinsic builder edits, sizes helper and property fixture. | Checked live builder hashes against the staged baseline, inspected the pinned source, published the named files, and integrated them with current dependencies rather than treating the stage directory as a product glob. |
| Integer and FP folders | Other participants staged `LlvmConstantFolding.Intrinsic.cs`, `IntegerIntrinsics.cs`, `Floating.cs` and `FloatingHost.cs`. Core intrinsic identities and APFloat were also other participants' work. | Published and connected these families; added the represented-source intrinsic eligibility switch and fixed-vector lane dispatcher; passed actual calls from ConstantFoldInstruction; separated direct folder context from actual canonicalize caller context. I did not author the inherited APInt/FP algorithms from scratch. |
| Builder GEP construction | The existing scalar adapter rejected vector GEP canonicalization and scalable strides. Core vector and vscale representations became available through other owners. | Ported the source construction flow for zero vector indices that must splat a scalar pointer, vector-shaped index constants and scalable vscale/multiply strides, retaining source nowrap flags. Changed CreateNot to use the existing shape-aware all-ones constant factory. |
| Host math adapter | A staged glibc fenv/errno implementation already existed. | Added one-time actual library/export availability instead of assuming that every Linux host had the named glibc ABI. Retained the source binary-host-fold type restriction and the staged source exception handling. |
| Floating vector declarations | The floating registry/factory was scalar-only. | Extended actual fixed/scalable name mangling and FP type recognition; widened exponent type representation; implemented same-shape i1 for is.fpclass; installed its real ImmArg attribute; added source Type shape helpers and scalar/vector declaration counters. |
| Tests and notes | The original TargetFolder property fixture came from the stage. | Published that fixture and added eight dispatcher checks; later wrote FloatingVectorIntrinsicProperties and the floating vector source map. Updated the TargetFolder note. I did not run these fixtures myself. |

The production files I directly handled are the TargetFolder and
TransformBuilder partials, the named ConstantFolding partials, the intrinsic call
hook in ConstantFolding.cs, LlvmIntrinsic.Floating.cs, LlvmType.Shape.cs and the
one-line partial modifier in LlvmType.cs. I left the independently published
memory builder partial intact. Publication manifests and baselines are retained
under `.temp/llvm-target-folder/` and `.temp/llvm-vector-intrinsics/`.

## Design rationale and working assumptions

The bounded problem was to let source-port consumers build and fold the actual
IR forms they needed. It was not an assignment to invent a better optimizer or
to make a runtime safety mechanism.

I kept the default ConstantFolder behavior distinct from explicit TargetFolder.
An optional builder strategy preserves the existing caller path while allowing
upstream InstCombine-style consumers to request DataLayout-dependent folding.
Builder intrinsic methods return LlvmValue because a successful fold returns a
constant; the static Core call factories continue returning real calls. Successful
folds do not need abandoned detached calls with retained operand uses.

I consulted the vendored pinned TargetFolder.h, IRBuilder.h/.cpp,
ConstantFolding.cpp, Intrinsics.td/Intrinsics.cpp, and DerivedTypes.h. Their
particular control flow mattered: the intrinsic eligibility gate precedes poison
folding; an absent direct context function means strictfp at that entry point;
and a context function passed to TargetFolder does not manufacture an actual
Call for context-dependent canonicalize. These are source rules, not additional
runtime admission checks. The new work did not add a heap solver, alias-identity
constraint, Range thunk, capture mode, or language-level number kind.

For floating vector types I used actual `vN` and `nxvN` source mangling, not
scalar-name aliases. is.fpclass returns scalar-or-same-vector-width i1 with a
scalar i32 mask. I found and reported an important correction to the requested
seam: the pinned ldexp exponent is independent `llvm_anyint_ty`, with an explicit
source TODO to constrain element counts; powi is `llvm_any_scalar_int_ty`.
I preserved this difference rather than imposing the anticipated matching-lane
constraint. The ordinary fixed-vector folder likewise retains the source
scalar-argument switch; it does not invent a scalar-exponent broadcast for
ldexp.

I assumed root owned the only build window and the final user journeys, as
explicitly instructed. Initially all listed builder baseline hashes matched the
live files, which made their publication mechanically straightforward. Shared
files were not copied from unrelated old snapshots. The vector follow-up was
staged during the AOT build and published only after root explicitly announced
that the build was no longer live.

## Limitations, omissions and errors in my contribution

1. **Integration was not independent full-port authorship.** I relied on other
   participants' staged integer and FP bodies and performed local source/API
   inspection. That is not a line-by-line audit of every ConstantFolding branch.
   My handoff wording such as "full staged integer/FP folders" meant the named
   staged represented families, not all LLVM constant folding or all optimizers.

2. **I produced no compile-time or runtime speedup evidence.** I ran no builds,
   timings or real source-to-artifact journeys in this assignment. `git diff
   --check`, baseline hashes and source correspondence do not establish runtime
   correctness, performance, or full upstream equivalence. Root later reported
   foundational acceptance; the retained foundations receipt includes 104
   TargetFolder properties. That is root's execution evidence, not a run I made,
   and it does not by itself prove the later floating-vector cohort was executed.

3. **The host adapter is not a complete cross-platform port.** It supports the
   specific glibc x64/arm64 fenv/errno ABI. Other hosts leave those host-libm
   operations unfolded while exact APFloat families remain available. This is
   an explicit platform adaptation limit, not the whole original upstream
   portability implementation. It should remain recorded as incomplete coverage.

4. **Declarations are not a vector execution backend.** The later cohort can
   construct names/signatures for all seven existing floating type IDs, but the
   APFloat numeric representation supports only its implemented formats. It adds
   no Native vector ABI and does not make x87/quad/PPC arithmetic executable.
   The default intrinsic factory and integer-vector declaration seams had
   separate gaps; I mapped these instead of silently redirecting integer vectors
   through the floating factory. Later repairs by other participants are not my
   work.

5. **Source boundaries can still expose incomplete product integration.**
   Registry representation, builder construction, analysis folding and target
   execution are different steps. Publishing one does not finish the others.
   The staged inherited dispatcher required corrections to source gate ordering
   and context propagation before I froze it. I also initially published some
   mutually dependent files in sequence during the no-build window: an
   intermediate interrupted snapshot could therefore be incomplete. My final
   coherent handoff, not each intermediate filesystem instant, was the claimed
   integration unit.

6. **These changes did not close the user's original latency question.** More
   source-derived folding coverage can be useful, but I did not establish that
   this work was the next highest-benefit remedy for the observed O2 compile
   slowdown. In particular I did not compare the new builder/constant-expression
   allocation costs against any compile-time savings. That prioritization and
   causal analysis remained unfulfilled within my remit, not answered by the
   amount of code integrated.

## Corrections the integrated root narrative should retain

- Attribute the original staged TargetFolder, integer/FP folding bodies and
  numeric/vector representations to their owners; attribute publication,
  dispatcher/context repairs and the later floating-vector declaration changes
  to me.
- Do not describe my SourceGate/strictfp work as a new safety proof framework.
  Conversely, do not treat "pinned source" as proof that the whole managed port
  is complete or fast.
- Keep declaration support, constant-folding support, target execution and
  measured user benefit as separate claims. The same applies to fixed vectors
  versus scalable declarations and to the seven declaration type IDs versus the
  actually implemented numeric formats.
- My files did not change Range initialization, invent by-value captures, adopt
  LLVM's backend, or design the project's HeapAlloc/FileIn/FileOut extensions.
  Any explanation assigning those earlier decisions to me would be reconstructed
  history, not this participant's account.
- The correct conclusion from my work is that named source prerequisites were
  integrated and specific representation gaps were closed. It is not that the
  original O2 regression was fixed or that all requested LLVM/GCC/javac algorithms
  had been completely transplanted.
