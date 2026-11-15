// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CmpInstAnalysis.h/.cpp
// comparison-code conversion and predicatesFoldable.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmCmpInstAnalysis
{
    public static uint GetICmpCode(LlvmCmpPredicate predicate) => predicate switch
    {
        LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpSgt => 1,
        LlvmCmpPredicate.ICmpEq => 2,
        LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSge => 3,
        LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpSlt => 4,
        LlvmCmpPredicate.ICmpNe => 5,
        LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSle => 6,
        _ => throw new UnreachableException("Invalid ICmp predicate")
    };
    public static uint GetFCmpCode(LlvmCmpPredicate predicate)
    { Debug.Assert(predicate <= LlvmCmpPredicate.FCmpTrue); return (uint)predicate; }
    public static bool PredicatesFoldable(LlvmCmpPredicate first, LlvmCmpPredicate second)
        => IsSignedCode(first) == IsSignedCode(second) || IsSignedCode(first) && IsEquality(second) ||
            IsSignedCode(second) && IsEquality(first);
    private static bool IsSignedCode(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;

    public static LlvmConstant? GetPredForICmpCode(uint code, bool signed, LlvmType operandType,
        out LlvmCmpPredicate predicate)
    {
        predicate = code switch
        {
            0 or 7 => default,
            1 => signed ? LlvmCmpPredicate.ICmpSgt : LlvmCmpPredicate.ICmpUgt,
            2 => LlvmCmpPredicate.ICmpEq,
            3 => signed ? LlvmCmpPredicate.ICmpSge : LlvmCmpPredicate.ICmpUge,
            4 => signed ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt,
            5 => LlvmCmpPredicate.ICmpNe,
            6 => signed ? LlvmCmpPredicate.ICmpSle : LlvmCmpPredicate.ICmpUle,
            _ => throw new UnreachableException("Illegal ICmp code")
        };
        return code is 0 or 7 ? ComparisonCodeBool(operandType, code == 7) : null;
    }
    public static LlvmConstant? GetPredForFCmpCode(uint code, LlvmType operandType, out LlvmCmpPredicate predicate)
    {
        Debug.Assert(code <= 15);
        predicate = (LlvmCmpPredicate)code;
        return code is 0 or 15 ? ComparisonCodeBool(operandType, code == 15) : null;
    }
    private static LlvmConstant ComparisonCodeBool(LlvmType operandType, bool value)
    {
        var boolean = LlvmConstantInt.GetBool(operandType.Context, value);
        return operandType is LlvmVectorType vector
            ? LlvmConstantVector.GetSplat(vector.ElementCount, boolean) : boolean;
    }
}
