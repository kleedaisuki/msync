// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/Loads.cpp canReplacePointersIfEqual/isPointerAlwaysReplaceable;
// ValueTracking.cpp getUnderlyingObject[ Aggressive ]; Value.cpp
// getPointerDereferenceableBytes's represented constant-global branch.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmCmpInstAnalysis
{
    public static bool CanReplacePointersIfEqual(LlvmValue from, LlvmValue to, LlvmDataLayout layout,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(ReferenceEquals(from.Type, to.Type), "values must have matching types");
        Count(metrics, "cmp_analysis.pointer_equal.queries");
        if (!from.Type.IsPtrOrPtrVectorTy()) return true;
        if (to is LlvmConstantPointerNull) return true;
        if (from is LlvmConstantPointerNull && from.Type.GetPointerAddressSpace() == 0) return true;
        // ConstantExpr/GlobalAlias are not represented. In the remaining constant
        // domain only a direct GlobalVariable can have dereferenceable bytes;
        // this is Loads.cpp's alignment-1/size-1 test with no context/assumptions.
        if (to is LlvmGlobalVariable { IsConstant: true } global && global.Type.IsPointerTy() &&
            global.ValueType.IsSized() && global.Linkage != LlvmLinkage.ExternalWeakLinkage &&
            layout.GetTypeStoreSize(global.ValueType).GetFixedValue() >= 1)
        { Count(metrics, "cmp_analysis.pointer_equal.constant_global"); return true; }
        bool equal = ReferenceEquals(UnderlyingObjectAggressive(from, metrics), UnderlyingObjectAggressive(to, metrics));
        Count(metrics, equal ? "cmp_analysis.pointer_equal.same_underlying_object" : "cmp_analysis.pointer_equal.distinct_underlying_objects");
        return equal;
    }
    internal static LlvmValue GetUnderlyingObject(LlvmValue value, Dictionary<string, ulong>? metrics = null)
        => UnderlyingObject(value, metrics);
    private static LlvmValue UnderlyingObject(LlvmValue value, Dictionary<string, ulong>? metrics)
        => LlvmValueTracking.GetUnderlyingObject(value, metrics: metrics);
    private static LlvmValue UnderlyingObjectAggressive(LlvmValue value, Dictionary<string, ulong>? metrics)
        => LlvmValueTracking.GetUnderlyingObjectAggressive(value, metrics: metrics);
}
