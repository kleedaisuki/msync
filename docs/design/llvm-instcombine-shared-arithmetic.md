# InstCombine shared arithmetic helpers

Pinned LLVM: `a55e64432a516fd6507d3ecfba7407cfe425f940`.

- `InstructionCombining.cpp:1609–1798`: `foldFBinOpOfIntCastsFromSign`
  and `foldFBinOpOfIntCasts`. The implementation preserves the unsigned-then-
  signed attempts, shared lazy KnownBits cache, floating significand precision,
  source overflow-width shortcut, signed-multiply nonzero requirement, and
  identity-based constant roundtrip. Vectors are excluded by upstream, not by a
  new project restriction. No floating fast-math permission is invented.
- `InstCombineCompares.cpp:5392–5545`: `hasNoWrapProblem` and
  `matchCommonBinOpOperands`. Add/disjoint-or and subtraction cancel shared
  terms; nonwrapping multiplication requires a nonzero unsigned common factor
  or a known-sign signed factor (negative reverses comparison operands).
  Fact queries use the caller's complete SimplifyQuery including AC/DC/CC.
- `InstCombineCompares.cpp:6909–6935`: `computeOverflow` dispatches the six
  actual ValueTracking add/sub/mul queries through the existing source wrappers.
- `OptimizeOverflowCheck` is owned and published by the ICmp facts owner; its
  initial shared draft is retained in `OverflowCheck.handoff.txt` only.

Counters report query visits, cancellation categories, signedness attempts,
rewrites, and overflow outcome on the actual work object. The recording-disabled
path retains the existing null-metrics behavior. The floating cast fold uses
one two-entry cache across both signedness attempts, as upstream does.

`InstCombineSharedArithmeticProperties.Run()` contains four focused complete
pass journeys: bounded unsigned add, bounded unsigned subtraction producing a
signed conversion, preservation of potentially-negative zero in signed floating
multiply, and SCmp common-add cancellation. These are staged for the root-owned
build and execution; this worker did not run MSBuild or claim execution results.
