# Vector KnownBits demanded elements

Pinned LLVM: `a55e64432a516fd6507d3ecfba7407cfe425f940`.

The existing `Compute(value, ...query)` now selects all fixed-vector lanes, or the source `APInt(1,1)` convention for scalars and scalable vectors. The overload `Compute(value, demandedElements, ...query)` returns **element-width** facts common to demanded lanes, not a packed vector integer. The private query carries this mask through elementwise recursion and PHI edge contexts without creating instructions or altering uses.

Source correspondence:

- `Analysis/ValueTracking.cpp:2665–2828`: demanded-element entry, scalar/splat constants (`PatternMatch.h` `ap_match<APInt>`, with poison forbidden for `m_APInt`; the separate constant-vector lane intersection skips poison), constant-data/vector intersections, undef, argument ranges, depth, operator dispatch and contextual refinements.
- `ValueTracking.cpp:1562–1900`: existing arithmetic/cast/select transfers reused with demanded recursion; vector-integer bitcast lane grouping and endianness at 1695–1770.
- `ValueTracking.cpp:2539–2623`: shuffle, insert-element and extract-element. Demanded poison shuffle lanes produce no facts, while poison constant-vector lanes are skipped by the source constant intersection. These are deliberately different mechanisms.
- `Analysis/VectorUtils.cpp:515–550`: shuffle output demand to input lane masks.
- Scalar-only lerp and Euclidean-remainder pattern refinements remain scalar-only, matching explicit source guards rather than trying to reinterpret vector conditions as scalar conditions.
- GEPs thread demands to vector operands/indices, retaining scalar broadcasts and actual data-layout pointer/index widths.

Current Core constant-expression operators are handled directly for casts, add/sub/xor, GEP and element insertion/extraction without temporary instruction registration. The source explicitly leaves non-splat ConstantExpr shuffles without operator facts.

The published validation entry is the existing `InstCombineVectorProperties.Run()` group, selected by `--instcombine-vector-only`; its assertions remain unchanged. A broader standalone query fixture was staged during exploration, not published or counted as executed coverage. Root owns the coherent build and execution receipt.

Cross-owner dependencies: source vector NumSignBits; source demanded NonZero; demanded FPClass for FP-to-int and FP bitcasts; intrinsic KnownBits dispatch. The integer vector paths in this cohort must not be used to claim those independent query families have been implemented.

## Bounded existing InstCombine vector journey closure

`tests/LlvmIr/InstCombineVectorProperties.cs` exercises pointer broadcast GEP, integer Add moved through vector reverse, identical/select shuffles, constant unshuffling, VP reverse, and left/right splice. All arithmetic element types are fixed `v4i8`. After the vector rewrite, the newly exposed Add must still pass the ordinary source visitAdd path; suppressing that analysis would hide the integration bug. Its NoCommonBits query, signed/unsigned overflow queries, and their KnownBits/NumSignBits/ConstantRange dependencies form the required closure.

- KnownBits supports vector arguments/constants, Add, shuffle and reverse (ValueTracking.cpp:2365: reverse the demand mask before recursing).
- `ComputeNumSignBits(vector)` needs scalar element width, followed by its real source KnownBits fallback for arguments/shuffles; no fake `return 1` vector adapter is needed.
- Signed/unsigned overflow use the source ConstantRange query on vector arguments/constants. The source vector-constant-to-range implementation must be retained, not scalar casts or fabricated Full for all vector constants.
- VP reverse and splice have no KnownBits intrinsic transfer case in the pinned source, so their source operator-default path is genuinely unknown; no bespoke facts are invented.
- This journey does not introduce shifts, multiplication, FP conversion/bitcasts, NonZero or power-of-two queries. Those independent vector query families are not prerequisites for this bounded closure and remain separately scoped.

The bounded query cohort was published after root released the benchmark freeze. Validation entry: `tests/LlvmIr --instcombine-vector-only`. This executes the existing complete vector assertion group without skipping assertions or re-running unrelated groups.

Root's coherent CLI/LlvmIr build completed without warnings or errors, followed
by `--instcombine-vector-only`: all nine existing InstCombine vector scenarios
passed (`artifacts/diagnostics/llvm-instcombine/final-instcombine-vector-only.log`).
The broader vector-query boundaries above remain unchanged.
