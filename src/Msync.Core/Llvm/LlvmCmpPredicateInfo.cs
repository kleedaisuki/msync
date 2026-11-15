// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/CmpPredicate.h and Instructions.cpp.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public readonly record struct LlvmCmpPredicateInfo
{
    private readonly byte encodedPredicate;
    public LlvmCmpPredicateInfo(LlvmCmpPredicate predicate, bool sameSign = false)
    {
        Debug.Assert(!sameSign || (byte)predicate is >= 32 and <= 41);
        encodedPredicate = (byte)((byte)predicate + 1);
        PredicateHasSameSign = sameSign;
    }
    public LlvmCmpPredicate Predicate => encodedPredicate == 0 ? (LlvmCmpPredicate)42 : (LlvmCmpPredicate)(encodedPredicate - 1);
    public bool PredicateHasSameSign { get; }
    public bool HasSameSign => PredicateHasSameSign;
    public bool IsValid => (byte)Predicate is <= 15 or >= 32 and <= 41;
    public LlvmCmpPredicateInfo GetInversePredicate() => new(LlvmCmpInstruction.GetInversePredicate(Predicate), HasSameSign);
    public LlvmCmpPredicateInfo GetSwappedPredicate() => new(LlvmCmpInstruction.GetSwappedPredicate(Predicate), HasSameSign);
    public LlvmCmpPredicate GetPreferredSignedPredicate() => HasSameSign ? GetSignedPredicate(Predicate) : Predicate;
    public static LlvmCmpPredicateInfo Get(LlvmCmpInstruction compare) => new(compare.Predicate, compare is LlvmICmpInstruction { SameSign: true });
    public static LlvmCmpPredicateInfo? GetMatching(LlvmCmpPredicateInfo left, LlvmCmpPredicateInfo right)
    {
        if (left.Predicate == right.Predicate) return new(left.Predicate, left.HasSameSign && right.HasSameSign);
        if ((byte)left.Predicate <= 15 || (byte)right.Predicate <= 15) return null;
        if (left.HasSameSign && left.Predicate == GetFlippedSignednessPredicate(right.Predicate)) return new(right.Predicate);
        if (right.HasSameSign && right.Predicate == GetFlippedSignednessPredicate(left.Predicate)) return new(left.Predicate);
        return null;
    }
    public static LlvmCmpPredicate GetSignedPredicate(LlvmCmpPredicate predicate) =>
        (byte)predicate is >= 34 and <= 37 ? (LlvmCmpPredicate)((byte)predicate + 4) : predicate;
    public static LlvmCmpPredicate GetFlippedSignednessPredicate(LlvmCmpPredicate predicate) => (byte)predicate switch
    {
        >= 34 and <= 37 => (LlvmCmpPredicate)((byte)predicate + 4),
        >= 38 and <= 41 => (LlvmCmpPredicate)((byte)predicate - 4),
        _ => predicate
    };
    public static implicit operator LlvmCmpPredicate(LlvmCmpPredicateInfo predicate) => predicate.Predicate;
    public static implicit operator LlvmCmpPredicateInfo(LlvmCmpPredicate predicate) => new(predicate);
}
