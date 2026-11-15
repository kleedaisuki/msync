# Vector ConstantRange queries

Source: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`Constants.cpp::Constant::toConstantRange` and
`ValueTracking.cpp::computeConstantRange/setLimitsForBinOp`.

The bounded InstCombine vector-arithmetic journey needs ranges for vector
arguments, constants and add operands through the signed/unsigned overflow
queries. The published slice changes the range bit width to scalar element
width and implements the real Core constant conversion. This does not add a
vector code-generation backend or per-lane solver.

Constant conversion preserves the source direct ConstantInt case, scalable and
fixed splats, ordered unions of data-vector lanes and ConstantVector lanes.
Poison lanes are skipped in the latter loop; undef returns full. Canonical
whole-vector poison follows the source full fallback, not an invented empty
range. Constant conversion permits poison in splats; the separate m_APInt
matcher for binary bounds forbids poison by default, as upstream does.

All existing assumption, domination-condition and CondContext query parameters
remain unchanged. The constant-vector path has its own query counter, while
existing visits, recursion cutoff and binary-limit counters remain in place.

This publication does not activate the broader SelectPattern, NonZero or shift
vector-query pipeline. Broader source intrinsic/attribute facts and focused
range properties remain staged in `.temp/llvm-instcombine/range-vector` for a
separate coherent publication. The integration owner runs the existing real
`InstCombineVectorProperties` journey; this worker did not run a build.
