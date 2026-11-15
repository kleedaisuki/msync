// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueLattice.h/.cpp; IR/Constants.cpp Constant::toConstantRange.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public readonly record struct LlvmLatticeMergeOptions(bool MayIncludeUndef, bool CheckWiden, uint MaxWidenSteps)
{
    public LlvmLatticeMergeOptions() : this(false, false, 1) { }
    public LlvmLatticeMergeOptions WithMaxWidenSteps(uint steps = 1) => this with { CheckWiden = true, MaxWidenSteps = steps };
}

public sealed class LlvmValueLatticeElement
{
    private enum Kind : byte { Unknown, Undef, Constant, NotConstant, ConstantRange, ConstantRangeIncludingUndef, Overdefined }
    private Kind tag;
    private LlvmConstant? constant;
    private LlvmConstantRange range = null!;
    public string StateName => tag.ToString();
    internal string Describe()
    {
        if (IsConstantRange()) return StateName + " i" + range.BitWidth + " [" + Bits(range.Lower) + "," + Bits(range.Upper) + ") widen=" + NumRangeExtensions;
        if (IsConstant || IsNotConstant)
            return StateName + " " + (constant switch
            {
                LlvmConstantFP fp => "fpbits=" + Bits(fp.Value.BitcastToAPInt()),
                LlvmConstantInt integer => "i" + integer.BitWidth + " " + Bits(integer.Value),
                LlvmConstantPointerNull => "null",
                LlvmGlobalValue global => "@" + global.Name,
                _ => constant!.GetType().Name
            });
        return StateName;
    }
    private static string Bits(LlvmAPInt value) => "0x" + string.Concat(value.ToWords().Reverse()
        .Select(word => word.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
    public uint NumRangeExtensions { get; set; }
    public bool MayHaveDifferentProvenance { get; set; }
    public bool IsUnknown => tag == Kind.Unknown;
    public bool IsUndef => tag == Kind.Undef;
    public bool IsUnknownOrUndef => IsUnknown || IsUndef;
    public bool IsConstant => tag == Kind.Constant;
    public bool IsNotConstant => tag == Kind.NotConstant;
    public bool IsOverdefined => tag == Kind.Overdefined;
    public bool IsConstantRangeIncludingUndef => tag == Kind.ConstantRangeIncludingUndef;
    public bool IsConstantRange(bool undefAllowed = true) => tag == Kind.ConstantRange ||
        tag == Kind.ConstantRangeIncludingUndef && (undefAllowed || range.IsSingleElement);
    public LlvmConstant GetConstant() { Debug.Assert(IsConstant); return constant!; }
    public LlvmConstant GetNotConstant() { Debug.Assert(IsNotConstant); return constant!; }
    public LlvmConstantRange GetConstantRange(bool undefAllowed = true)
    { Debug.Assert(IsConstantRange(undefAllowed)); return range; }
    public LlvmValueLatticeElement Clone()
    {
        return new() { tag = tag, constant = constant, range = range,
            NumRangeExtensions = IsConstantRange() ? NumRangeExtensions : 0,
            MayHaveDifferentProvenance = MayHaveDifferentProvenance };
    }
    private void Assign(LlvmValueLatticeElement other)
    {
        tag = other.tag; constant = other.constant; range = other.range;
        NumRangeExtensions = other.IsConstantRange() ? other.NumRangeExtensions : 0;
        MayHaveDifferentProvenance = other.MayHaveDifferentProvenance;
    }
    public static LlvmValueLatticeElement Get(LlvmConstant value)
    { var result = new LlvmValueLatticeElement(); result.MarkConstant(value); return result; }
    public static LlvmValueLatticeElement GetNot(LlvmConstant value)
    { Debug.Assert(value is not LlvmUndefValue); var result = new LlvmValueLatticeElement(); result.MarkNotConstant(value); return result; }
    public static LlvmValueLatticeElement GetRange(LlvmConstantRange range, bool mayIncludeUndef = false)
    {
        if (range.IsFullSet) return GetOverdefined();
        var result = new LlvmValueLatticeElement();
        if (range.IsEmptySet) { if (mayIncludeUndef) result.MarkUndef(); return result; }
        result.MarkConstantRange(range, new() { MayIncludeUndef = mayIncludeUndef });
        return result;
    }
    public static LlvmValueLatticeElement GetOverdefined()
    { var result = new LlvmValueLatticeElement(); result.MarkOverdefined(); return result; }
    public LlvmAPInt? AsConstantInteger() => IsConstant && constant is LlvmConstantInt integer ? integer.Value :
        IsConstantRange() ? range.GetSingleElement() : null;
    public LlvmConstantRange AsConstantRange(uint bits, bool undefAllowed = false) => IsConstantRange(undefAllowed) ? range :
        IsConstant ? ConstantToRange(constant!) : IsUnknown ? LlvmConstantRange.GetEmpty(bits) : LlvmConstantRange.GetFull(bits);
    public LlvmConstantRange AsConstantRange(LlvmType type, bool undefAllowed = false)
    { Debug.Assert(type.IsIntOrIntVectorTy()); return AsConstantRange(type.GetScalarSizeInBits(), undefAllowed); }
    internal static LlvmConstantRange ConstantToRange(LlvmConstant value)
    {
        if (value is LlvmConstantInt integer) return new(integer.Value);
        uint bits = value.Type.GetScalarSizeInBits();
        if (value.Type.IsVectorTy() && value is LlvmConstantAggregateZero)
            return new(new LlvmAPInt(bits, 0));
        // The represented constants have no ConstantVector/DataVector/ConstantByte variants.
        return LlvmConstantRange.GetFull(bits);
    }
    public bool MarkOverdefined()
    { if (IsOverdefined) return false; tag = Kind.Overdefined; constant = null; range = null!; return true; }
    public bool MarkUndef()
    { if (IsUndef) return false; Debug.Assert(IsUnknown); tag = Kind.Undef; return true; }
    public bool MarkConstant(LlvmConstant value, bool mayIncludeUndef = false)
    {
        if (value is LlvmUndefValue) return MarkUndef();
        if (IsConstant) { Debug.Assert(ReferenceEquals(constant, value)); return false; }
        if (value is LlvmConstantInt integer)
            return MarkConstantRange(new(integer.Value), new() { MayIncludeUndef = mayIncludeUndef });
        Debug.Assert(IsUnknown || IsUndef); tag = Kind.Constant; constant = value; return true;
    }
    public bool MarkNotConstant(LlvmConstant value)
    {
        if (value is LlvmConstantInt integer)
            return MarkConstantRange(new(integer.Value + new LlvmAPInt(integer.BitWidth, 1), integer.Value));
        if (value is LlvmUndefValue) return false;
        if (IsNotConstant) { Debug.Assert(ReferenceEquals(constant, value)); return false; }
        Debug.Assert(IsUnknown); tag = Kind.NotConstant; constant = value; return true;
    }
    public bool MarkConstantRange(LlvmConstantRange next, LlvmLatticeMergeOptions? options = null)
    {
        var opts = options ?? new();
        Debug.Assert(!next.IsEmptySet);
        if (next.IsFullSet) return MarkOverdefined();
        Kind oldTag = tag;
        Kind nextTag = IsUndef || IsConstantRangeIncludingUndef || opts.MayIncludeUndef
            ? Kind.ConstantRangeIncludingUndef : Kind.ConstantRange;
        if (IsConstantRange())
        {
            tag = nextTag;
            if (range.Equals(next)) return tag != oldTag;
            if (opts.CheckWiden && ++NumRangeExtensions > opts.MaxWidenSteps) return MarkOverdefined();
            Debug.Assert(next.Contains(range)); range = next; return true;
        }
        Debug.Assert(IsUnknown || IsUndef || IsConstant);
        Debug.Assert(!IsConstant || next.Contains(ConstantToRange(constant!)));
        NumRangeExtensions = 0; tag = nextTag; range = next; constant = null; return true;
    }
    public bool MergeIn(LlvmValueLatticeElement rhs, LlvmLatticeMergeOptions? options = null)
    {
        var opts = options ?? new();
        if (rhs.IsUnknown || IsOverdefined) return false;
        if (rhs.IsOverdefined) { MarkOverdefined(); return true; }
        if (IsUndef)
        {
            if (rhs.IsUndef) return false;
            if (rhs.IsConstant) return MarkConstant(rhs.constant!, true);
            if (rhs.IsConstantRange()) return MarkConstantRange(rhs.range, opts with { MayIncludeUndef = true });
            return MarkOverdefined();
        }
        if (IsUnknown) { Assign(rhs); return true; }
        if (IsConstant)
        {
            if (rhs.IsConstant && ReferenceEquals(constant, rhs.constant))
            {
                bool previous = MayHaveDifferentProvenance;
                MayHaveDifferentProvenance |= rhs.MayHaveDifferentProvenance;
                return previous != MayHaveDifferentProvenance;
            }
            if (rhs.IsUndef) return false;
            if (constant!.Type.IsVectorTy() && constant.Type.GetScalarType().IsIntegerTy())
            {
                var left = ConstantToRange(constant);
                return MarkConstantRange(left.UnionWith(rhs.AsConstantRange(left.BitWidth, true)),
                    opts with { MayIncludeUndef = rhs.IsConstantRangeIncludingUndef });
            }
            MarkOverdefined(); return true;
        }
        if (IsNotConstant)
        {
            if (rhs.IsNotConstant && ReferenceEquals(constant, rhs.constant)) return false;
            MarkOverdefined(); return true;
        }
        Kind oldTag = tag;
        Debug.Assert(IsConstantRange());
        if (rhs.IsUndef) { tag = Kind.ConstantRangeIncludingUndef; return oldTag != tag; }
        return MarkConstantRange(range.UnionWith(rhs.AsConstantRange(range.BitWidth, true)),
            opts with { MayIncludeUndef = rhs.IsConstantRangeIncludingUndef });
    }
    public LlvmConstant? GetCompare(LlvmCmpPredicate predicate, LlvmType type, LlvmValueLatticeElement other,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        if (IsUnknown || other.IsUnknown || IsUndef || other.IsUndef) return null;
        if (IsConstant && other.IsConstant)
            return LlvmConstantFolding.ConstantFoldCompareInstOperands(predicate, constant!, other.constant!, layout, metrics: metrics);
        if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe &&
            (IsNotConstant && other.IsConstant && ReferenceEquals(constant, other.constant) ||
                IsConstant && other.IsNotConstant && ReferenceEquals(constant, other.constant)))
            return LlvmConstantInt.GetBool(type.Context, predicate == LlvmCmpPredicate.ICmpNe);
        if (!IsConstantRange() || !other.IsConstantRange()) return null;
        if (range.ICmp(predicate, other.range)) return LlvmConstantInt.GetTrue(type.Context);
        if (range.ICmp(LlvmCmpInstruction.GetInversePredicate(predicate), other.range)) return LlvmConstantInt.GetFalse(type.Context);
        return null;
    }
    public LlvmValueLatticeElement Intersect(LlvmValueLatticeElement other)
    {
        if (IsUnknown) return Clone();
        if (other.IsUnknown || IsOverdefined) return other.Clone();
        if (other.IsOverdefined) return Clone();
        if (IsConstant || IsConstantRange() && range.IsSingleElement) return Clone();
        if (other.IsConstant || other.IsConstantRange() && other.range.IsSingleElement) return other.Clone();
        if (!IsConstantRange() || !other.IsConstantRange()) return Clone();
        return GetRange(range.IntersectWith(other.range), IsConstantRangeIncludingUndef || other.IsConstantRangeIncludingUndef);
    }
}
