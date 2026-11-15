# Upstream provenance audit: candidate Native loop transforms

## Decision under the superseding requirement

The candidate Native unswitching and first-iteration reuse implementations are
**custom transformations, not faithful ports of the audited LLVM/GCC algorithms**.
Their optimization names describe related ideas, not implementation provenance.
They must not be counted as delivered upstream-algorithm coverage or carried into
the new primary-LLVM-IR optimizer on that basis. No new product code or build was
performed for this audit.

The previous assignment implemented transforms over mutable Native slots and
instruction-PC arrays. The superseding assignment requires primary LLVM IR and
upstream algorithms, without adopting the LLVM backend or its optimization
pipeline. That is a different representation/algorithm contract; relabeling the
Native helpers cannot satisfy it.

## Pinned upstream material

These are immutable source revisions resolved for the team audit, not floating
links to `main`. Deployment/version selection remains with the integrating owner.

- LLVM: `a55e64432a516fd6507d3ecfba7407cfe425f940`.
  - [SimpleLoopUnswitch.cpp, nontrivial transform](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SimpleLoopUnswitch.cpp#L1955)
  - [LoopPeel.cpp, block cloning](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/LoopPeel.cpp#L904)
  - [LoopPeel.cpp, peelLoop](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/LoopPeel.cpp#L1102)
  - [LoopUnrollPass.cpp, count selection](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/LoopUnrollPass.cpp#L988)
- GCC: `c548a927b535fd8cb003e4b51aa4225613669905`.
  - [tree-ssa-loop-unswitch.cc, documented algorithm](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/tree-ssa-loop-unswitch.cc#L45)
  - [tree-ssa-loop-unswitch.cc, tree_unswitch_loop](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/tree-ssa-loop-unswitch.cc#L1100)
  - [tree-ssa-loop-ivcanon.cc, try_peel_loop](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/tree-ssa-loop-ivcanon.cc#L1113)

Raw audit copies are disposable in
`.temp/native-midend/upstream-loop-transforms/`; the source URLs and revisions
above preserve the useful knowledge independently of that directory.

## Unswitching comparison

LLVM's `unswitchNontrivialInvariants` splits the preheader, clones/remaps loop
versions and transfers dispatch to the split entry. It explicitly maintains
loop/dominator/memory-SSA relationships and handles invariant conditions under
LLVM's value/poison semantics, including condition freezing where required.
Its candidate/cost handling is part of that concrete implementation, not merely
a fixed maximum instruction count.

GCC's `tree_unswitch_loop` delegates to `loop_version` using the condition and
edge probabilities. Its surrounding selection estimates reachable-block costs,
tracks selected predicates and simplifies the resulting versions. This is an
entry-versioning transformation over GIMPLE/SSA, not first-in-loop dispatch.

The Native candidate differs materially:

| Candidate mechanism | Provenance finding |
| --- | --- |
| Original branch stays inside its original first executed iteration and jumps to a chosen clone's successor | Custom dispatch placement; not the audited entry-unswitch implementations |
| Cloned branch becomes a jump; original mutable slots/iterator IDs are shared by mutually exclusive versions | Native-slot representation choice, not LLVM SSA value cloning/PHI repair |
| Recursive `NativeLoopInvariants` classifies single definitions, selected operators and exposed slots | Custom invariance analysis; not an adaptation of LLVM SSA loop-invariance and its eligibility rules |
| First candidate by block count/PC order, 64-instruction loop bound and 192-instruction function growth bound | Locally chosen policies, not pinned upstream cost/selection algorithms |
| Scheduled execution-plan functions skipped | A Native representation limitation, not evidence of upstream handling |

Keeping the first branch at its original demand position was chosen to avoid
speculating msync pending-datum operations before a zero-trip loop. That motivation
does not turn the newly invented control-flow scheme into an upstream port.
LLVM `freeze` is about LLVM value/poison semantics; it is not an equivalent model
for executing, awaiting or registering a language-level future.

## Peeling and first-iteration reuse comparison

LLVM `peelLoop` clones the requested initial iterations, connects them ahead of
the remaining loop and remaps definitions. `cloneLoopBlocks` repairs cloned
header PHIs and relevant loop/dominator/noalias structures; `peelLoop` updates the
remaining header inputs, loop metadata, LCSSA/ScalarEvolution state and canonical
structure. Its count selection has a separate concrete implementation.

GCC's audited `try_peel_loop` chooses an estimated prefix subject to its upstream
profitability/iteration policy and duplicates through the loop preheader edge.
Neither audited API takes a set of arbitrary native instruction PCs whose
results should be retained after the first execution.

The candidate `NativeLoopPeeling.ReuseAfterFirstIteration` instead keeps the
original body as its first iteration, appends a residual clone, redirects original
backedges into that clone and replaces selected cloned definitions with Nops.
The copy direction alone can be a graph-label choice in simple cases, but the
selected-PC reuse policy and same-slot value retention are a custom combination
with LICM. No exact counterpart was found in the audited upstream implementations.
It must not be described as a direct `peelLoop` port or accepted merely because
both create an acyclic prefix followed by a residual cycle.

`NativeLoopCopy` is a Native instruction-array cloning/branch-relocation utility.
It keeps the same slot definitions and uses origin-PC maps for allocation plans.
That is not LLVM's value map, SSA/PHI remapping, alias-scope cloning or analysis
update algorithm. A new LLVM-IR implementation needs the corresponding LLVM-IR
operations rather than retaining this mutable-slot helper behind a new name.

## Unrolling status and evidence limits

There is no delivered `NativeLoopUnroll*` implementation in this assignment.
Unswitching and residual cloning are not unrolling. The pinned LLVM unroll source
contains separate count-selection and transform orchestration; those stages
have not been ported or verified here.

`LoopUnswitchChecks` was written as a focused Native-model equivalence/metadata
check. Even a passing result could establish behavior for its tested inputs,
not the provenance of the algorithm, LLVM-IR conformance or complete upstream
coverage. No additional execution of that test was performed during this audit.

## Audited candidate identities

These hashes identify the superseded candidate code inspected here. They are
not approval hashes or a request to retain the files in the new implementation.

| File | SHA-256 |
| --- | --- |
| `NativeLoopTransforms.cs` | `22295c69dd5ced5e33222d0f9a075ecc9a547c4f4b56b3c19de91809c8a51c27` |
| `NativeLoopUnswitch.cs` | `3b18df63d5eea2916cfda1360fb992c7d69a9eb697e75ff57824938f7a1eb5c3` |
| `NativeLoopTransforms.Copy.cs` | `dc34006a58aa3ed5fe24d9eb5d040faeb06b3c3334f3a401410a8e32fd5859e4` |
| `NativeLoopTransforms.Peeling.cs` | `fd5aa9e42ca9a69591d002f822a25563d80edf46db332b6b8495bad06a8a6187` |
| `LoopUnswitchChecks.cs` | `d5b44c46ef8c7c2dfb3e1890541487c328123147ec6922dd4f2e5b35ffca790c` |

## Required disposition

Select a specific upstream transform/version and preserve its actual selection,
rewrite, value-mapping and analysis-update behavior over the new LLVM IR.
Language/runtime operations must first have explicit LLVM-IR semantics, including
future/effect/ownership behavior. If that makes an upstream transform inapplicable,
record the precise operation/property rather than inventing another transformation
and presenting it as the selected upstream algorithm. The integrating owner
should remove or leave disabled these superseded Native candidates and update
the coverage matrix accordingly; this audit does not authorize product edits.
