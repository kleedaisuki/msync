// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: KnownFPClass.cpp fpconvert/fpext/fptrunc.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
namespace Msync.Optimization.Llvm;
public partial struct LlvmKnownFPClass
{
    public static LlvmKnownFPClass FPConvert(LlvmKnownFPClass source, LlvmAPFloatSemantics destination,
        LlvmAPFloatSemantics origin, LlvmDenormalModeInfo destinationMode, LlvmDenormalModeInfo sourceMode)
    {
        var mode = destinationMode == LlvmDenormalModeInfo.IEEE && sourceMode == LlvmDenormalModeInfo.IEEE
            ? LlvmDenormalModeInfo.IEEE : LlvmDenormalModeInfo.Dynamic;
        source = ApplyInputDenormalMode(source, mode);
        var known = new LlvmKnownFPClass();
        known.PropagateNonNaN(source);
        int dstMin = LlvmAPFloat.SemanticsMinExponent(destination), srcMin = LlvmAPFloat.SemanticsMinExponent(origin);
        int dstMax = LlvmAPFloat.SemanticsMaxExponent(destination), srcMax = LlvmAPFloat.SemanticsMaxExponent(origin);
        uint dstPrecision = LlvmAPFloat.SemanticsPrecision(destination), srcPrecision = LlvmAPFloat.SemanticsPrecision(origin);
        bool normalFinite = dstMax > srcMax || dstMax == srcMax && dstPrecision >= srcPrecision;
        bool finiteNonZero = dstMin <= srcMin && dstPrecision >= srcPrecision;
        bool normalAndSubnormal = dstMin == srcMin && dstMax >= srcMax && dstPrecision >= srcPrecision;
        if (source.IsKnownNever(C.PosInfinity) && (source.IsKnownNever(C.PosNormal) || normalFinite)) known.KnownNot(C.PosInfinity);
        if (source.IsKnownNever(C.NegInfinity) && (source.IsKnownNever(C.NegNormal) || normalFinite)) known.KnownNot(C.NegInfinity);
        if (normalAndSubnormal)
        {
            known.KnownNot((C.Normal | C.Subnormal) & ~source.GetKnownFPClasses());
        }
        else
        {
            if (source.IsKnownNever(C.PosNormal | C.PosSubnormal)) known.KnownNot(C.PosNormal | C.PosSubnormal);
            if (source.IsKnownNever(C.NegNormal | C.NegSubnormal)) known.KnownNot(C.NegNormal | C.NegSubnormal);
        }
        if (dstMin > srcMin)
        {
            if (source.IsKnownNever(C.PosNormal)) known.KnownNot(C.PosNormal);
            if (source.IsKnownNever(C.NegNormal)) known.KnownNot(C.NegNormal);
        }
        if (srcMax < dstMax && srcMin > dstMin && dstPrecision >= srcPrecision) known.KnownNot(C.Subnormal);
        if (source.IsKnownNever(C.PosZero) && (source.IsKnownNever(C.PosNormal | C.PosSubnormal) || finiteNonZero)) known.KnownNot(C.PosZero);
        if (source.IsKnownNever(C.NegZero) && (source.IsKnownNever(C.NegNormal | C.NegSubnormal) || finiteNonZero)) known.KnownNot(C.NegZero);
        return ApplyOutputDenormalMode(known, mode);
    }
    public static LlvmKnownFPClass FrexpMantissa(LlvmKnownFPClass source, LlvmDenormalModeInfo mode)
    {
        source = ApplyInputDenormalMode(source, mode);
        var known = new LlvmKnownFPClass();
        known.KnownNot(C.Subnormal);
        known.PropagateNonNaN(source);
        known.KnownNot((C.Zero | C.Infinity) & ~source.GetKnownFPClasses());
        if (source.IsKnownNever(C.PosNormal | C.PosSubnormal)) known.KnownNot(C.PosNormal);
        if (source.IsKnownNever(C.NegNormal | C.NegSubnormal)) known.KnownNot(C.NegNormal);
        return known;
    }
    public static LlvmKnownFPClass Subtract(LlvmKnownFPClass left, LlvmKnownFPClass right, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Subtract(ApplyInputDenormalMode(left, mode), ApplyInputDenormalMode(right, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass Divide(LlvmKnownFPClass left, LlvmKnownFPClass right, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Divide(ApplyInputDenormalMode(left, mode), ApplyInputDenormalMode(right, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass AddSelf(LlvmKnownFPClass value, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(AddSelf(ApplyInputDenormalMode(value, mode), LlvmDenormalMode.IEEE), mode);
    public bool IsKnownNeverLogicalZero(LlvmDenormalModeInfo mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.Zero);
    public bool IsKnownNeverLogicalPosZero(LlvmDenormalModeInfo mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.PosZero);
    public static LlvmKnownFPClass DivideSelf(LlvmKnownFPClass value, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(DivideSelf(ApplyInputDenormalMode(value, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass RemainderSelf(LlvmKnownFPClass value, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(RemainderSelf(ApplyInputDenormalMode(value, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass Remainder(LlvmKnownFPClass left, LlvmKnownFPClass right, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Remainder(ApplyInputDenormalMode(left, mode), ApplyInputDenormalMode(right, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass Multiply(LlvmKnownFPClass value, LlvmAPFloat constant, LlvmDenormalModeInfo mode)
    {
        int exponent = constant.ILogB();
        var known = Multiply(value, new LlvmKnownFPClass(constant), mode);
        if (exponent >= (int)LlvmAPFloat.SemanticsPrecision(constant.Semantics) - 1) known.KnownNot(C.Subnormal);
        if (value.IsKnownNeverInfinity() && (exponent < 0 || exponent == 0 &&
            constant.CompareAbsoluteValue(LlvmAPFloat.GetOne(constant.Semantics)) == LlvmFloatCompare.Equal)) known.KnownNot(C.Infinity);
        return known;
    }

}
