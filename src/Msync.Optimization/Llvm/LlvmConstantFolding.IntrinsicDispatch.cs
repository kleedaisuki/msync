// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// canConstantFoldIntrinsic and ConstantFoldFixedVectorCall's lane loop.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static bool CanConstantFoldIntrinsic(LlvmIntrinsicKind kind, bool strictFp) => kind.Name switch
    {
        "llvm.bswap" or "llvm.ctpop" or "llvm.ctlz" or "llvm.cttz" or "llvm.fshl" or "llvm.fshr" or
        "llvm.launder.invariant.group" or "llvm.abs" or "llvm.smax" or "llvm.smin" or "llvm.umax" or "llvm.umin" or
        "llvm.scmp" or "llvm.ucmp" or "llvm.sadd.with.overflow" or "llvm.uadd.with.overflow" or
        "llvm.ssub.with.overflow" or "llvm.usub.with.overflow" or "llvm.smul.with.overflow" or "llvm.umul.with.overflow" or
        "llvm.smulh" or "llvm.umulh" or "llvm.sadd.sat" or "llvm.uadd.sat" or "llvm.ssub.sat" or "llvm.usub.sat" or
        "llvm.sshl.sat" or "llvm.ushl.sat" or "llvm.bitreverse" => true,
        "llvm.fabs" or "llvm.copysign" or "llvm.is.fpclass" or "llvm.ceil" or "llvm.floor" or "llvm.round" or
        "llvm.roundeven" or "llvm.trunc" or "llvm.nearbyint" or "llvm.rint" or "llvm.canonicalize" => true,
        "llvm.minnum" or "llvm.maxnum" or "llvm.minimum" or "llvm.maximum" or "llvm.minimumnum" or "llvm.maximumnum" or
        "llvm.log" or "llvm.log2" or "llvm.log10" or "llvm.exp" or "llvm.exp2" or "llvm.exp10" or "llvm.sqrt" or
        "llvm.sin" or "llvm.cos" or "llvm.sinh" or "llvm.cosh" or "llvm.atan" or "llvm.pow" or "llvm.powi" or
        "llvm.ldexp" or "llvm.fma" or "llvm.fmuladd" => !strictFp,
        _ => false
    };

    private static LlvmConstant? FoldFixedVectorIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstant> operands,
        LlvmFixedVectorType type, Dictionary<string, ulong>? metrics, LlvmCallBase? call)
    {
        var result = new LlvmConstant[type.NumElements];
        var lane = new LlvmConstant[operands.Count];
        for (uint element = 0; element < type.NumElements; ++element)
        {
            for (int argument = 0; argument < operands.Count; ++argument)
            {
                bool scalar = argument == 1 && (kind == LlvmIntrinsicKind.Abs || kind == LlvmIntrinsicKind.CTLZ ||
                    kind == LlvmIntrinsicKind.CTTZ || kind == LlvmIntrinsicKind.IsFPClass || kind == LlvmIntrinsicKind.PowI);
                LlvmConstant? value = scalar ? operands[argument] : operands[argument].GetAggregateElement(element);
                if (value is null) return null;
                lane[argument] = value;
            }
            LlvmConstant? folded = FoldScalarIntrinsic(kind, lane, type.ElementType, metrics, call);
            if (folded is null) return null;
            result[element] = folded;
            Count(metrics, "constant_fold.intrinsic.vector_lanes");
        }
        Count(metrics, "constant_fold.intrinsic.vector_folded");
        return LlvmConstantVector.Get(result);
    }
}
