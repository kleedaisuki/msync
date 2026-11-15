# LLVM assumptions and memory-effect attributes

Source snapshot: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
This component represents internal LLVM IR assumptions. It adds no source-language
assertion, runtime check, Range behavior, or execution gate.

## Source correspondence

| Managed component | Upstream implementation | Translation boundary |
| --- | --- | --- |
| `LlvmIntrinsicKind.Assume`, descriptor, declaration and call builder | `Intrinsics.td::int_assume`, `Intrinsics.cpp::getDeclaration`, `IRBuilder.h::CreateAssumption` | Exact non-overloaded `void(i1)` declaration; existing ordinary Call instruction and operand bundles |
| `LlvmMemoryEffectsAttribute`, Function/Call `GetMemoryEffects` | `Attributes.h` memory-effects attribute, `Function.cpp::getMemoryEffects`, `Instructions.cpp::CallBase::getMemoryEffects`, `Support/ModRef.h` | Packed two-bit ModRef fields retain all six upstream locations; managed uint stores the source attribute payload, not a second location enum |
| Instruction read/write queries and AA decoding | `Instruction.cpp::mayReadFromMemory/mayWriteToMemory`, `AliasAnalysis.cpp::getMemoryEffects` | All represented ordinary calls consume effective function/call/bundle effects; absence remains unknown |
| `LlvmAssumptionCache` | `Analysis/AssumptionCache.h/.cpp` | Lazy function scan, weak assumption handles, affected-value callback keys, RAUW transfer, deletion, registration, replacement and clearing |
| `FindValuesAffectedByCondition` | `ValueTracking.cpp::addValueAffectedByCondition/findValuesAffectedByCondition` | Scalar integer/FP comparisons, logical operations, casts, NUW squares and registered ctpop; unrepresented vector constants, fabs and is_fpclass are not name-guessed |
| Bundle affected-value indexing | `AssumptionCache.cpp::findValuesAffectedByOperandBundle` | Bundle index retained separately from `ExprResultIdx`; separate_storage uses shared source underlying-object stripping |
| `LlvmUser.IsDroppable` | `User.cpp::isDroppable` | Represented assume branch; pseudoprobe/noalias-scope declaration intrinsics are not registered |
| `Call.IsPassingUndefUB` | `CallBase::isPassingUndefUB`, intrinsic arg attribute | Assume arg0 has source NoUndef; this is a compiler fact, not an emitted check |
| DCE assume handling | `Local.cpp::wouldInstructionBeTriviallyDead`, `AssumeBundleQueries.cpp::isAssumeWithEmptyBundle` | Constant nonzero condition with no bundle except ignore is removable |
| Target assume elision | LLVM assume intrinsic code-generation semantics | Zero Native instructions and no runtime assertion; operands remain ordinary IR dependencies until optimization |

The intrinsic has `IntrWriteMem` and `IntrInaccessibleMemOnly`, not readnone.
Its effective raw memory attribute is 8. An ordinary call annotated inaccessible
ModRef uses raw 12; it does not acquire intrinsic NoUnwind/WillReturn attributes.
Call-site and callee attributes intersect, while operand bundles contribute their
source read/write effects before that intersection. Existing call cloning copies
the optional attribute.

## Cache ownership and costs

The first Assumptions/AssumptionsFor query scans actual instructions once.
Registration before that scan does nothing, matching upstream: the eventual scan
finds the inserted call. Source weak handles become null on logical deletion;
they do not follow RAUW. Affected-value callback handles transfer entries only to
Instruction/Argument replacements and remove deleted keys. Results retain bundle
indices, exact insertion deduplication and the upstream 1024-entry default cap.
Deleted weak entries count toward the cap. RAUW transfer deduplicates by assume
identity, matching ResultElem's source conversion to Value pointer. Updating a
condition with Use.Set does not magically invalidate the cache: callers use
UpdateAffectedValues/UnregisterAssumption as in the source contract.

Managed dictionaries/lists replace DenseMap/SmallVector storage; explicit IR
logical deletion drives the same callback protocol. TTI predicated address-space
facts are absent because this IR slice has no TargetTransformInfo; no fabricated
replacement is supplied. LLVM pass-manager/legacy analysis tracker wrappers are
not claimed as delivered by this cache class. Source debug-only cache invariants
are not production validation gates.

Cache telemetry counts actual instruction scans, cache hits/misses, registrations
before scan, affected insertions/deduplication/cap limits, removals and RAUW
transfers. The supplied detailed trace owns the function/pass context; a disabled
trace creates no metric dictionary. Target records actual assume elision, and DCE
records the constant-condition decision. Nonnull assumption facts used by scalar
ValueTracking queries are a separate source-helper port, not an empty-cache
substitute.

## Real integration opportunities

SimplifyCFG can insert an assume at the actual source UB-rewriting site and
register it in the same function cache. KnownBits/nonzero/speculation can query
only the assumptions indexed for an operand and check source context validity.
An assumption remains an internal IR fact: source pending Bool evaluation uses
an ordinary DemandBool call, whose inaccessible memory-effect attribute does not
make that potentially throwing/nonreturning language operation removable.
