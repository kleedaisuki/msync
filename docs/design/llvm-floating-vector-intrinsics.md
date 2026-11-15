# Floating intrinsic vector types and overloads

This cohort extends the existing pinned intrinsic declaration representation;
it is not a new optimizer or a Native vector execution implementation. Source
commit: `a55e64432a516fd6507d3ecfba7407cfe425f940`.

| Representation change | Pinned source |
| --- | --- |
| Fixed/scalable overload names | `lib/IR/Intrinsics.cpp:86–169`, `getMangledTypeStr` |
| FP unary/binary/FMA matching signatures | `include/llvm/IR/Intrinsics.td:1226–1253` |
| Powi scalar integer exponent | `Intrinsics.td:1237–1239`, `llvm_any_scalar_int_ty` |
| Ldexp independent integer overload | `Intrinsics.td:1298–1301`, `llvm_anyint_ty` |
| FP class predicate result and scalar immediate mask | `Intrinsics.td:1381–1387` |
| SameVecWidth result decoding | `lib/IR/Intrinsics.cpp:610–617` |
| Type replacement preserving vector shape | `include/llvm/IR/DerivedTypes.h:805–816` |
| Builder inferred return type before folder | `lib/IR/IRBuilder.cpp:977–1003` |

Ordinary matching FP types remain identical across operands and return. The
overload mangler recursively prefixes fixed vectors with `vN` and scalable
vectors with `nxvN`, using each actual scalar element ID. Examples:
`llvm.fabs.v4f32`, `llvm.powi.nxv2f64.i16`,
`llvm.ldexp.v4f32.v4i32`, `llvm.is.fpclass.nxv2bf16`.
The existing scalar names remain byte-identical.

The is.fpclass result substitutes i1 for the floating element while retaining
the scalar/fixed/scalable shape. Its class mask remains scalar i32 and carries
the actual ImmArg attribute on the declaration. Builder inference and Core
declaration construction use the same source Type::getWithNewType operation,
so a folded vector constant and an emitted call agree on their result type.

Powi independently overloads its scalar integer exponent width. Ldexp
independently overloads any integer scalar/vector exponent: the pinned source
explicitly has not constrained this parameter's lane count. Preserving the
source means not adding a compatibility check that changes this type contract.
Its ordinary well-shaped vector folding uses the existing lane-wise source
algorithm; no scalar broadcast rule is invented.

Function identity remains an actual LLVM intrinsic identity. Element classes
and name suffixes are used to recognize the corresponding declarations, not
to reinterpret an ordinary source-language function as a builtin. Native
Number remains binary64 and no language-level numeric kind is added.

Caller-owned declaration counters retain the existing source/function context
from the compiler/pass owner and distinguish scalar versus vector declaration
requests. Recording-disabled calls allocate no telemetry dictionaries. Core
type and mangling helpers have no global analysis state or proof solver.

`FloatingVectorIntrinsicProperties.Run()` checks declarations and the actual
builder-to-TargetFolder flow, not mocked scalar substitutes. A root-owned build
and focused execution receipt are still required; a staged file is not evidence
of completed execution support.
