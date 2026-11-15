// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp aliasCheck/aliasCheckRecursive.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    internal readonly LlvmDataLayout Layout;
    internal readonly LlvmFunction Function;
    internal readonly LlvmDominatorTree? Tree;
    internal readonly Dictionary<string, ulong>? Metrics;
    internal LlvmBasicAliasAnalysis(LlvmFunction function, LlvmDataLayout layout, LlvmDominatorTree? tree,
        Dictionary<string, ulong>? metrics) => (Function, Layout, Tree, Metrics) = (function, layout, tree, metrics);
    internal void Count(string name)
    {
        if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1;
    }
    internal LlvmAliasResult Alias(LlvmMemoryLocation a, LlvmMemoryLocation b, LlvmAAQueryInfo query, LlvmInstruction? context)
    {
        if (a.Size.IsZero || b.Size.IsZero) { Count("aa.empty_location"); return LlvmAliasResult.NoAlias; }
        var first = LlvmValueTracking.StripPointerCastsForAliasAnalysis(a.Ptr!, Metrics);
        var second = LlvmValueTracking.StripPointerCastsForAliasAnalysis(b.Ptr!, Metrics);
        if (first is LlvmUndefValue || second is LlvmUndefValue) { Count("aa.undef"); return LlvmAliasResult.NoAlias; }
        if (EqualInPotentialCycles(first, second, query)) { Count("aa.equal_pointer"); return LlvmAliasResult.MustAlias; }
        var objectA = LlvmValueTracking.GetUnderlyingObject(first, metrics: Metrics);
        var objectB = LlvmValueTracking.GetUnderlyingObject(second, metrics: Metrics);
        if (objectA is LlvmHeapAllocInst || objectB is LlvmHeapAllocInst) Count("aa.heap.identified_object_queries");
        if (objectA is LlvmConstantPointerNull nullA && !NullIsDefined(((LlvmPointerType)nullA.Type).AddressSpace)
            || objectB is LlvmConstantPointerNull nullB && !NullIsDefined(((LlvmPointerType)nullB.Type).AddressSpace))
        { Count("aa.null_object"); return LlvmAliasResult.NoAlias; }
        if (!ReferenceEquals(objectA, objectB))
        {
            if (IsIdentifiedObject(objectA) && IsIdentifiedObject(objectB))
            { Count("aa.distinct_identified_objects"); return LlvmAliasResult.NoAlias; }
            if (IsArgumentLike(objectA) && IsIdentifiedFunctionLocal(objectB)
                || IsArgumentLike(objectB) && IsIdentifiedFunctionLocal(objectA))
            { Count("aa.argument_vs_local"); return LlvmAliasResult.NoAlias; }
            if (IsEscapeSource(objectA) && query.Captures.GetCapturesBefore(objectB, objectA as LlvmInstruction, true, false).CapturesNothing()
                || IsEscapeSource(objectB) && query.Captures.GetCapturesBefore(objectA, objectB as LlvmInstruction, true, false).CapturesNothing())
            { Count("aa.escape_source_vs_local"); return LlvmAliasResult.NoAlias; }
        }
        if (IsObjectSmallerThan(objectB, a.Size) || IsObjectSmallerThan(objectA, b.Size))
        { Count("aa.access_larger_than_object"); return LlvmAliasResult.NoAlias; }
        // Separate-storage assumptions have no AssumptionCache carrier in this IR.
        return CheckWithCache(first, a.Size, second, b.Size, objectA, objectB, query);
    }
    private LlvmAliasResult CheckRecursive(LlvmValue a, LlvmLocationSize aSize, LlvmValue b, LlvmLocationSize bSize,
        LlvmAAQueryInfo query, LlvmValue objectA, LlvmValue objectB)
    {
        if (a is LlvmGetElementPtrInstruction firstGep)
        {
            var result = AliasGEP(firstGep, aSize, b, bSize, objectA, objectB, query);
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        else if (b is LlvmGetElementPtrInstruction secondGep)
        {
            var result = AliasGEP(secondGep, bSize, a, aSize, objectB, objectA, query).Swap();
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        if (a is LlvmPhiInstruction firstPhi)
        {
            var result = AliasPhi(firstPhi, aSize, b, bSize, query);
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        else if (b is LlvmPhiInstruction secondPhi)
        {
            var result = AliasPhi(secondPhi, bSize, a, aSize, query).Swap();
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        if (a is LlvmSelectInstruction firstSelect)
        {
            var result = AliasSelect(firstSelect, aSize, b, bSize, query);
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        else if (b is LlvmSelectInstruction secondSelect)
        {
            var result = AliasSelect(secondSelect, bSize, a, aSize, query).Swap();
            if (result != LlvmAliasKind.MayAlias) return result;
        }
        if (ReferenceEquals(objectA, objectB) && aSize.IsPrecise && bSize.IsPrecise
            && (IsObjectSize(objectA, aSize.GetValue()) || IsObjectSize(objectB, bSize.GetValue())))
            return LlvmAliasResult.PartialAlias;
        return LlvmAliasResult.MayAlias;
    }
    internal bool EqualInPotentialCycles(LlvmValue a, LlvmValue b, LlvmAAQueryInfo query)
    {
        if (!ReferenceEquals(a, b)) return false;
        if (!query.MayBeCrossIteration) return true;
        if (a is not LlvmInstruction instruction || ReferenceEquals(instruction.Parent, Function.BasicBlocks[0])) return true;
        var successors = LlvmDominatorTree.Successors(instruction.Parent!).ToList();
        return successors.Count == 0 || !LlvmCfgReachability.IsPotentiallyReachableFromMany(successors, instruction.Parent!,
            exclusion: null, tree: query.UseDominatorTree ? Tree : null, loops: null, metrics: Metrics);
    }
    internal static bool IsIdentifiedObject(LlvmValue value) => value is LlvmAllocaInstruction or LlvmHeapAllocInst or LlvmGlobalValue;
    internal static bool IsIdentifiedFunctionLocal(LlvmValue value) => value is LlvmAllocaInstruction or LlvmHeapAllocInst;
    internal static bool IsBaseOfObject(LlvmValue value) => value is LlvmAllocaInstruction or LlvmHeapAllocInst or LlvmGlobalVariable;
    private static bool IsArgumentLike(LlvmValue value) => value is LlvmArgument
        || value is LlvmExtractValueInstruction { AggregateOperand: LlvmArgument };
    private static bool IsEscapeSource(LlvmValue value) => value is LlvmCallBase or LlvmLoadInstruction or LlvmExtractValueInstruction
        || value is LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr };
    private static bool NullIsDefined(uint addressSpace = 0) => addressSpace != 0;
    private bool IsObjectSmallerThan(LlvmValue value, LlvmLocationSize size)
    {
        if (!IsIdentifiedObject(value)) return false;
        var objectSize = LlvmObjectSize.GetBaseObjectSize(value, Layout, nullIsUnknownSize: NullIsDefined(), roundToAlign: true, metrics: Metrics);
        if (objectSize is null || objectSize.Value.IsScalable) return false;
        var extent = LlvmTypeSize.Fixed(size.IsPrecise ? size.GetValue().KnownMinValue : 0);
        return LlvmTypeSize.IsKnownLT(objectSize.Value, extent);
    }
    private bool IsObjectSize(LlvmValue value, LlvmTypeSize size)
    {
        var objectSize = LlvmObjectSize.GetBaseObjectSize(value, Layout, nullIsUnknownSize: NullIsDefined(), metrics: Metrics);
        return objectSize is { IsScalable: false } exact && exact == size;
    }
}
