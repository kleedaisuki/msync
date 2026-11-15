// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownFPClass.cpp.
using System.Numerics;
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public enum LlvmFPMinMaxKind : byte { MinNum, MaxNum, Minimum, Maximum, MinimumNum, MaximumNum }

public partial struct LlvmKnownFPClass
{
private static LlvmKnownFPClass FMAImpl(LlvmKnownFPClass KnownLHS,
                             LlvmKnownFPClass KnownRHS) {
  LlvmKnownFPClass Known = new();
  if (KnownLHS.CannotBeOrderedLessThanZero() &&
      KnownRHS.CannotBeOrderedLessThanZero()) {
    Known.KnownNot(LlvmKnownFPClass.OrderedLessThanZeroMask);
    if (KnownLHS.IsKnownNever(C.Zero | C.PosSubnormal) ||
        KnownRHS.IsKnownNever(C.Zero | C.PosSubnormal))
      Known.KnownNot(C.Zero | C.PosSubnormal);
  }
  if (KnownLHS.CannotBeOrderedGreaterThanZero() &&
      KnownRHS.CannotBeOrderedGreaterThanZero()) {
    Known.KnownNot(LlvmKnownFPClass.OrderedGreaterThanZeroMask);
    if (KnownLHS.IsKnownNever(C.Zero | C.NegSubnormal) ||
        KnownRHS.IsKnownNever(C.Zero | C.NegSubnormal))
      Known.KnownNot(C.Zero | C.NegSubnormal);
  }
  return Known;
}

public static LlvmKnownFPClass MinMaxLike(LlvmKnownFPClass KnownLHS_,
                                      LlvmKnownFPClass KnownRHS_,
                                      LlvmFPMinMaxKind Kind, LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);
  LlvmKnownFPClass Known = new();
  Known.PropagateNonNaN(KnownLHS, KnownRHS);
  if ((KnownLHS.IsKnownNeverNaN() || KnownRHS.IsKnownNeverNaN()) &&
      (Kind == LlvmFPMinMaxKind.MinNum || Kind == LlvmFPMinMaxKind.MaxNum ||
       Kind == LlvmFPMinMaxKind.MinimumNum || Kind == LlvmFPMinMaxKind.MaximumNum))
    Known.KnownNot(C.NaN);
  switch (Kind) {
  case LlvmFPMinMaxKind.MaxNum: {
    if (KnownLHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyLess(KnownLHS.GetKnownFPClasses(),
                                         false));
    if (KnownRHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyLess(KnownRHS.GetKnownFPClasses(),
                                         false));
    break;
  }
  case LlvmFPMinMaxKind.MinNum: {
    if (KnownLHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyGreater(KnownLHS.GetKnownFPClasses(),
                                            false));
    if (KnownRHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyGreater(KnownRHS.GetKnownFPClasses(),
                                            false));
    break;
  }
  case LlvmFPMinMaxKind.MaximumNum: {
    if (KnownLHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyLess(KnownLHS.GetKnownFPClasses(),
                                         true));
    if (KnownRHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyLess(KnownRHS.GetKnownFPClasses(),
                                         true));
    break;
  }
  case LlvmFPMinMaxKind.MinimumNum: {
    if (KnownLHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyGreater(KnownLHS.GetKnownFPClasses(),
                                            true));
    if (KnownRHS.IsKnownNeverNaN())
      Known.KnownNot(OrderedStrictlyGreater(KnownRHS.GetKnownFPClasses(),
                                            true));
    break;
  }
  case LlvmFPMinMaxKind.Maximum: {
    Known.KnownNot(OrderedStrictlyLess(KnownLHS.GetKnownFPClasses(),
                                       true) |
                   OrderedStrictlyLess(KnownRHS.GetKnownFPClasses(),
                                       true));
    break;
  }
  case LlvmFPMinMaxKind.Minimum: {
    Known.KnownNot(OrderedStrictlyGreater(KnownLHS.GetKnownFPClasses(),
                                          true) |
                   OrderedStrictlyGreater(KnownRHS.GetKnownFPClasses(),
                                          true));
    break;
  }
  }
  C PossibleClasses =
      KnownLHS.GetKnownFPClasses() | KnownRHS.GetKnownFPClasses() | C.NaN;
  Known.KnownNot(~PossibleClasses);
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Canonicalize(LlvmKnownFPClass KnownSrc,
                                        LlvmDenormalModeInfo DenormMode) {
  LlvmKnownFPClass Known = new();
  Known.SetKnownFPClasses(KnownSrc.GetKnownFPClasses() | C.Zero | C.QNaN);
  if (KnownSrc.IsKnownNeverNaN())
    Known.KnownNot(C.NaN);
  else
    Known.KnownNot(C.SNaN);
  if (DenormMode == LlvmDenormalModeInfo.IEEE) {
    if (KnownSrc.IsKnownNever(C.PosZero))
      Known.KnownNot(C.PosZero);
    if (KnownSrc.IsKnownNever(C.NegZero))
      Known.KnownNot(C.NegZero);
    return Known;
  }
  if (DenormMode.InputsAreZero || DenormMode.OutputsAreZero)
    Known.KnownNot(C.Subnormal);
  if (DenormMode == LlvmDenormalModeInfo.PreserveSign) {
    if (KnownSrc.IsKnownNever(C.PosZero | C.PosSubnormal))
      Known.KnownNot(C.PosZero);
    if (KnownSrc.IsKnownNever(C.NegZero | C.NegSubnormal))
      Known.KnownNot(C.NegZero);
    return Known;
  }
  if (DenormMode.Input == LlvmDenormalMode.PositiveZero ||
      (DenormMode.Output == LlvmDenormalMode.PositiveZero &&
       DenormMode.Input == LlvmDenormalMode.IEEE)) {
    if (KnownSrc.IsKnownNever(C.NegZero))
      Known.KnownNot(C.NegZero);
    if (KnownSrc.IsKnownNever(C.PosZero | C.Subnormal))
      Known.KnownNot(C.PosZero);
  }
  return Known;
}

public static LlvmKnownFPClass FMA(LlvmKnownFPClass KnownLHS_,
                               LlvmKnownFPClass KnownRHS_,
                               LlvmKnownFPClass KnownAddend_,
                               LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);
  LlvmKnownFPClass KnownAddend = ApplyInputDenormalMode(KnownAddend_, Mode);
  LlvmKnownFPClass Mul = Multiply(KnownLHS, KnownRHS, LlvmDenormalModeInfo.IEEE);
  LlvmKnownFPClass Known = FMAImpl(Mul, KnownAddend);
  if (KnownLHS.IsKnownNever(C.SNaN) && KnownRHS.IsKnownNever(C.SNaN) &&
      KnownAddend.IsKnownNever(C.SNaN))
    Known.KnownNot(C.SNaN);
  if (KnownLHS.IsKnownNeverNaN() && KnownRHS.IsKnownNeverNaN() &&
      KnownAddend.IsKnownNeverNaN()) {
    bool NeverZeroMulInf =
        (KnownLHS.IsKnownNever(C.Infinity) || KnownRHS.IsKnownNever(C.Zero)) &&
        (KnownRHS.IsKnownNever(C.Infinity) || KnownLHS.IsKnownNever(C.Zero));
    bool NeverExactPosInf =
        (KnownLHS.IsKnownNever(C.PosInfinity) ||
         KnownRHS.IsKnownNever(C.PosInfinity | C.PosNormal | C.PosSubnormal)) &&
        (KnownRHS.IsKnownNever(C.PosInfinity) ||
         KnownLHS.IsKnownNever(C.PosInfinity | C.PosNormal | C.PosSubnormal)) &&
        (KnownLHS.IsKnownNever(C.NegInfinity) ||
         KnownRHS.IsKnownNever(C.NegInfinity | C.NegNormal | C.NegSubnormal)) &&
        (KnownRHS.IsKnownNever(C.NegInfinity) ||
         KnownLHS.IsKnownNever(C.NegInfinity | C.NegNormal | C.NegSubnormal));
    bool NeverExactNegInf =
        (KnownLHS.IsKnownNever(C.PosInfinity) ||
         KnownRHS.IsKnownNever(C.NegInfinity | C.NegNormal | C.NegSubnormal)) &&
        (KnownRHS.IsKnownNever(C.PosInfinity) ||
         KnownLHS.IsKnownNever(C.NegInfinity | C.NegNormal | C.NegSubnormal)) &&
        (KnownLHS.IsKnownNever(C.NegInfinity) ||
         KnownRHS.IsKnownNever(C.PosInfinity | C.PosNormal | C.PosSubnormal)) &&
        (KnownRHS.IsKnownNever(C.NegInfinity) ||
         KnownLHS.IsKnownNever(C.PosInfinity | C.PosNormal | C.PosSubnormal));
    if (NeverZeroMulInf &&
        (KnownAddend.IsKnownNever(C.PosInfinity) || NeverExactNegInf) &&
        (KnownAddend.IsKnownNever(C.NegInfinity) || NeverExactPosInf))
      Known.KnownNot(C.NaN);
  }
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass FMulAdd(LlvmKnownFPClass KnownLHS,
                                   LlvmKnownFPClass KnownRHS,
                                   LlvmKnownFPClass KnownAddend,
                                   LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass fused = FMA(KnownLHS, KnownRHS, KnownAddend, Mode);
  LlvmKnownFPClass MulAdd = Add(Multiply(KnownLHS, KnownRHS, Mode), KnownAddend, Mode);
  return fused.IntersectWith(MulAdd);
}

public static LlvmKnownFPClass FMASquare(LlvmKnownFPClass KnownSquared_,
                                      LlvmKnownFPClass KnownAddend_,
                                      LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass KnownSquared = ApplyInputDenormalMode(KnownSquared_, Mode);
  LlvmKnownFPClass KnownAddend = ApplyInputDenormalMode(KnownAddend_, Mode);
  LlvmKnownFPClass Squared = Square(KnownSquared, LlvmDenormalModeInfo.IEEE);
  LlvmKnownFPClass Known = FMAImpl(Squared, KnownAddend);
  Known.PropagateNonSNaN(KnownSquared, KnownAddend);
  if (KnownSquared.IsKnownNeverNaN() && KnownAddend.IsKnownNeverNaN() &&
      (KnownSquared.IsKnownNever(C.Infinity) || KnownAddend.IsKnownNever(C.NegInfinity)))
    Known.KnownNot(C.NaN);
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass FMulAddSquare(LlvmKnownFPClass KnownSquared,
                                          LlvmKnownFPClass KnownAddend,
                                          LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass FMA = FMASquare(KnownSquared, KnownAddend, Mode);
  LlvmKnownFPClass MulAdd = Add(Square(KnownSquared, Mode), KnownAddend, Mode);
  return FMA.IntersectWith(MulAdd);
}

public static LlvmKnownFPClass Exp(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Negative);
  Known.PropagateNonNaN(KnownSrc);
  if (KnownSrc.IsKnownNever(C.NegNormal | C.NegInfinity))
    Known.KnownNot(C.PosZero);
  if (KnownSrc.IsKnownNever(C.NegNormal))
    Known.KnownNot(C.PosSubnormal);
  if (KnownSrc.IsKnownNever(C.PosNormal | C.PosInfinity))
    Known.KnownNot(C.PosInfinity);
  return Known;
}

public static LlvmKnownFPClass Log(LlvmKnownFPClass KnownSrc_, LlvmDenormalModeInfo Mode,
                               bool IsMultiUnitFPType) {
  LlvmKnownFPClass KnownSrc = ApplyInputDenormalMode(KnownSrc_, Mode);
  LlvmKnownFPClass Known = new();
  if (!IsMultiUnitFPType)
    Known.KnownNot(C.NegZero | C.Subnormal);
  Known.PropagateNonSNaN(KnownSrc);
  if (KnownSrc.IsKnownNever(C.PosInfinity))
    Known.KnownNot(C.PosInfinity);
  if (KnownSrc.IsKnownNeverNaN() && KnownSrc.CannotBeOrderedLessThanZero())
    Known.KnownNot(C.NaN);
  if (KnownSrc.IsKnownNever(C.Zero))
    Known.KnownNot(C.NegInfinity);
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Sqrt(LlvmKnownFPClass KnownSrc,
                                LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.PosSubnormal);
  if (KnownSrc.IsKnownNever(C.PosInfinity))
    Known.KnownNot(C.PosInfinity);
  Known.PropagateNonSNaN(KnownSrc);
  if (KnownSrc.IsKnownNeverNaN() && KnownSrc.CannotBeOrderedLessThanZero())
    Known.KnownNot(C.NaN);
  Known.KnownNot(C.NegInfinity | C.NegSubnormal | C.NegNormal);
  if (KnownSrc.IsKnownNeverLogicalNegZero(Mode))
    Known.KnownNot(C.NegZero);
  return Known;
}

public static LlvmKnownFPClass Sin(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.PropagateNonSNaN(KnownSrc);
  Known.KnownNot(C.Infinity);
  if (KnownSrc.IsKnownNeverNaN() && KnownSrc.IsKnownNeverInfinity())
    Known.KnownNot(C.NaN);
  return Known;
}

public static LlvmKnownFPClass Cos(LlvmKnownFPClass KnownSrc) {
  return Sin(KnownSrc);
}

public static LlvmKnownFPClass Tan(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Infinity);
  Known.PropagateNonSNaN(KnownSrc);
  if (KnownSrc.IsKnownNeverNaN() && KnownSrc.IsKnownNeverInfinity())
    Known.KnownNot(C.NaN);
  return Known;
}

public static LlvmKnownFPClass Sinh(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  if (KnownSrc.IsKnownNever(C.Negative))
    Known.KnownNot(C.Negative);
  if (KnownSrc.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity))
    Known.KnownNot(C.PosSubnormal | C.PosNormal | C.PosInfinity);
  Known.PropagateNonNaN(KnownSrc);
  return Known;
}

public static LlvmKnownFPClass Cosh(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Negative | C.Zero | C.Subnormal);
  Known.PropagateNonNaN(KnownSrc);
  return Known;
}

public static LlvmKnownFPClass Tanh(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Infinity);
  if (KnownSrc.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity)) {
    Known.KnownNot(C.NegSubnormal | C.NegNormal);
    if (KnownSrc.IsKnownNever(C.NegZero))
      Known.KnownNot(C.NegZero);
  }
  if (KnownSrc.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity))
    Known.KnownNot(C.PosSubnormal | C.PosNormal);
  Known.PropagateNonNaN(KnownSrc);
  return Known;
}

public static LlvmKnownFPClass ASin(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Infinity);
  Known.PropagateNonSNaN(KnownSrc);
  if (KnownSrc.IsKnownNever(C.NegFinite))
    Known.KnownNot(C.NegFinite);
  if (KnownSrc.IsKnownNever(C.PosSubnormal | C.PosNormal))
    Known.KnownNot(C.PosSubnormal | C.PosNormal);
  return Known;
}

public static LlvmKnownFPClass ACos(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Negative | C.Infinity | C.Subnormal);
  if (KnownSrc.IsKnownNever(C.PosNormal))
    Known.KnownNot(C.Zero);
  Known.PropagateNonSNaN(KnownSrc);
  return Known;
}

public static LlvmKnownFPClass ATan(LlvmKnownFPClass KnownSrc) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Infinity);
  if (KnownSrc.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity))
    Known.KnownNot(C.NegSubnormal | C.NegNormal);
  if (KnownSrc.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity))
    Known.KnownNot(C.PosSubnormal | C.PosNormal);
  if (KnownSrc.IsKnownNever(C.NegNormal | C.NegSubnormal | C.NegZero))
    Known.KnownNot(C.NegZero);
  Known.PropagateNonNaN(KnownSrc);
  return Known;
}

public static LlvmKnownFPClass ATan2(LlvmKnownFPClass KnownY_,
                                 LlvmKnownFPClass KnownX_,
                                 LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass KnownY = ApplyInputDenormalMode(KnownY_, Mode);
  LlvmKnownFPClass KnownX = ApplyInputDenormalMode(KnownX_, Mode);
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Infinity);
  Known.PropagateNonNaN(KnownY, KnownX);
  if (KnownX.IsKnownNever(C.Positive))
    Known.KnownNot(C.Zero | C.Subnormal);
  if (KnownY.IsKnownNever(C.Negative))
    Known.KnownNot(C.Negative);
  if (KnownY.IsKnownNever(C.Positive))
    Known.KnownNot(C.Positive);
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass RoundToIntegral(LlvmKnownFPClass KnownSrc,
                                           bool MayBeMultiUnitFPType,
                                           LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = new();
  Known.KnownNot(C.Subnormal);
  Known.PropagateNonNaN(KnownSrc);
  if (!MayBeMultiUnitFPType) {
    if (KnownSrc.IsKnownNever(C.PosInfinity))
      Known.KnownNot(C.PosInfinity);
    if (KnownSrc.IsKnownNever(C.NegInfinity))
      Known.KnownNot(C.NegInfinity);
  }
  if (KnownSrc.IsKnownNever(C.PosNormal | C.PosSubnormal))
    Known.KnownNot(C.PosNormal);
  if (KnownSrc.IsKnownNever(C.NegNormal | C.NegSubnormal))
    Known.KnownNot(C.NegNormal);
  if (KnownSrc.IsKnownNever(C.NegFinite))
    Known.KnownNot(C.NegZero);
  if (KnownSrc.IsKnownNever(C.PosFinite) &&
      (KnownSrc.IsKnownNever(C.NegSubnormal) ||
       !(Mode.Input is LlvmDenormalMode.PositiveZero or LlvmDenormalMode.Dynamic)))
    Known.KnownNot(C.PosZero);
  return Known;
}

public static LlvmKnownFPClass Trunc(LlvmKnownFPClass KnownSrc,
                                 LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = RoundToIntegral(KnownSrc,
                                       true, Mode);
  if (KnownSrc.IsKnownNever(C.PosInfinity))
    Known.KnownNot(C.PosInfinity);
  if (KnownSrc.IsKnownNever(C.NegInfinity))
    Known.KnownNot(C.NegInfinity);
  return Known;
}

public static LlvmKnownFPClass Floor(LlvmKnownFPClass KnownSrc,
                                 bool IsMultiUnitFPType, LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = RoundToIntegral(KnownSrc, IsMultiUnitFPType, Mode);
  if (KnownSrc.IsKnownNever(C.PosInfinity))
    Known.KnownNot(C.PosInfinity);
  return Known;
}

public static LlvmKnownFPClass Ceil(LlvmKnownFPClass KnownSrc,
                                bool IsMultiUnitFPType, LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = RoundToIntegral(KnownSrc, IsMultiUnitFPType, Mode);
  if (KnownSrc.IsKnownNever(C.NegInfinity))
    Known.KnownNot(C.NegInfinity);
  return Known;
}

public static LlvmKnownFPClass Ldexp(LlvmKnownFPClass KnownSrc_,
                                 LlvmAPInt ConstantRangeExpMin,
                                 LlvmAPInt ConstantRangeExpMax,
                                 LlvmAPFloatSemantics Flt, LlvmDenormalModeInfo Mode) {
  LlvmKnownFPClass Known = new();
  LlvmKnownFPClass KnownSrc = ApplyInputDenormalMode(KnownSrc_, Mode);
  if (ConstantRangeExpMin.IsZero && ConstantRangeExpMax.IsZero) {
    Known.SetKnownFPClasses(KnownSrc.GetKnownFPClasses() | C.NaN);
    Known.PropagateNonNaN(KnownSrc);
    return ApplyOutputDenormalMode(Known, Mode);
  }
  Known.PropagateNonNaN(KnownSrc);
  if (KnownSrc.IsKnownNever(C.Negative))
    Known.KnownNot(C.Negative);
  else if (KnownSrc.CannotBeOrderedLessThanZero())
    Known.KnownNot(OrderedLessThanZeroMask);
  if (KnownSrc.IsKnownNever(C.Positive))
    Known.KnownNot(C.Positive);
  else if (KnownSrc.CannotBeOrderedGreaterThanZero())
    Known.KnownNot(OrderedGreaterThanZeroMask);
  {
    uint Precision = LlvmAPFloat.SemanticsPrecision(Flt);
    int MantissaBits = (int)Precision - 1;
    if (ConstantRangeExpMin.SExtOrTrunc(Math.Max(32U, ConstantRangeExpMin.BitWidth)).Sge(new(Math.Max(32U, ConstantRangeExpMin.BitWidth), (ulong)MantissaBits)))
      Known.KnownNot(C.Subnormal);
  }
  if ((ConstantRangeExpMax.IsNegative || ConstantRangeExpMax.IsZero)) {
    if (KnownSrc.IsKnownNever(C.PosInfinity))
      Known.KnownNot(C.PosInfinity);
    if (KnownSrc.IsKnownNever(C.NegInfinity))
      Known.KnownNot(C.NegInfinity);
  } else if (ConstantRangeExpMin.IsNonNegative) {
    if (KnownSrc.IsKnownNever(C.PosSubnormal))
      Known.KnownNot(C.PosSubnormal);
    if (KnownSrc.IsKnownNever(C.NegSubnormal))
      Known.KnownNot(C.NegSubnormal);
    if (KnownSrc.IsKnownNever(C.PosZero))
      Known.KnownNot(C.PosZero);
    if (KnownSrc.IsKnownNever(C.NegZero))
      Known.KnownNot(C.NegZero);
  }
  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Pow(LlvmKnownFPClass KnownLHS,
                               LlvmKnownFPClass KnownRHS) {
  LlvmKnownFPClass Known = new();
  Known.PropagateNonSNaN(KnownLHS, KnownRHS);
  if (KnownLHS.IsKnownNever(C.NaN | C.NegNormal | C.NegSubnormal) &&
      KnownRHS.IsKnownNeverNaN())
    Known.KnownNot(C.NaN);
  if (KnownLHS.IsKnownNever(C.Negative)) {
    Known.KnownNot(C.Negative);
  } else if (KnownLHS.IsKnownNever(C.NegNormal | C.NegSubnormal)) {
    Known.KnownNot(C.NegNormal | C.NegSubnormal);
    if ((KnownLHS.IsKnownNever(C.NegZero) ||
         KnownRHS.IsKnownNever(C.PosNormal)) &&
        (KnownLHS.IsKnownNever(C.NegInfinity) || KnownRHS.IsKnownNever(C.NegNormal)))
      Known.KnownNot(C.NegZero);
    if ((KnownLHS.IsKnownNever(C.NegZero) ||
         KnownRHS.IsKnownNever(C.NegNormal)) &&
        (KnownLHS.IsKnownNever(C.NegInfinity) || KnownRHS.IsKnownNever(C.PosNormal)))
      Known.KnownNot(C.NegInfinity);
  }
  return Known;
}

public static LlvmKnownFPClass PowI(LlvmKnownFPClass KnownSrc,
                                LlvmKnownBits ExponentKnownBits) {
  LlvmKnownFPClass Known = new();
  Known.PropagateNonNaN(KnownSrc);
  if (ExponentKnownBits.IsZero) {
    if (KnownSrc.IsKnownNever(C.SNaN)) {
      Known.KnownNot(~C.PosNormal);
      return Known;
    }
    Known.KnownNot(~(C.PosNormal | C.NaN));
    return Known;
  }
  if (KnownSrc.IsKnownNever(C.Negative) || (ExponentKnownBits.Zero.GetWord(0) & 1) != 0) {
    Known.KnownNot(C.Negative);
  } else if (KnownSrc.IsKnownNever(C.NegNormal | C.NegSubnormal)) {
    Known.KnownNot(C.NegNormal | C.NegSubnormal);
    bool IsKnownNeverOddPositive = ExponentKnownBits.IsNegative;
    bool IsKnownNeverOddNegative = ExponentKnownBits.IsNonNegative;
    if ((KnownSrc.IsKnownNever(C.NegZero) || IsKnownNeverOddPositive) &&
        (KnownSrc.IsKnownNever(C.NegInfinity) || IsKnownNeverOddNegative))
      Known.KnownNot(C.NegZero);
    if ((KnownSrc.IsKnownNever(C.NegZero) || IsKnownNeverOddNegative) &&
        (KnownSrc.IsKnownNever(C.NegInfinity) || IsKnownNeverOddPositive))
      Known.KnownNot(C.NegInfinity);
  }
  {
    LlvmAPInt MinExp = ExponentKnownBits.GetSignedMinValue();
    LlvmAPInt MaxExp = ExponentKnownBits.GetSignedMaxValue();
    bool MayInfSrc =
        !KnownSrc.IsKnownNever(C.Infinity) && MaxExp.IsStrictlyPositive;
    bool MayDivByZero = !KnownSrc.IsKnownNever(C.Zero) && MinExp.IsNegative;
    bool MayFinite = !KnownSrc.IsKnownNever(C.Normal | C.Subnormal);
    bool MayAbsExpGT1 = MinExp.Slt(LlvmAPInt.GetAllOnes(MinExp.BitWidth)) || MaxExp.SExtOrTrunc(Math.Max(32U, MaxExp.BitWidth)).Sgt(new(Math.Max(32U, MaxExp.BitWidth), 1));
    bool MayFiniteOverflow = MayFinite && MayAbsExpGT1;
    bool MayBeNegOne = ExponentKnownBits.Zero.IsZero;
    bool MaySubnormInv = !KnownSrc.IsKnownNever(C.Subnormal) && MayBeNegOne;
    if (!MayInfSrc && !MayDivByZero && !MayFiniteOverflow && !MaySubnormInv)
      Known.KnownNot(C.Infinity);
  }
  return Known;
}
    public static LlvmKnownFPClass FAbs(LlvmKnownFPClass source)
    {
        source.SetSignBit(null);
        source.SetSignBit(false);
        return source;
    }
    public static LlvmKnownFPClass CopySign(LlvmKnownFPClass magnitude, LlvmKnownFPClass sign)
    {
        magnitude.SetSignBit(null);
        magnitude.SetSignBit(sign.SignBit);
        return magnitude;
    }
    public static LlvmKnownFPClass ApplyInputDenormalMode(LlvmKnownFPClass source, LlvmDenormalModeInfo mode) =>
        ApplyInputDenormalMode(source, mode.Input);
    public static LlvmKnownFPClass ApplyOutputDenormalMode(LlvmKnownFPClass source, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(source, mode.Output);
    public bool IsKnownNeverLogicalNegZero(LlvmDenormalModeInfo mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.NegZero);
    public static LlvmKnownFPClass Multiply(LlvmKnownFPClass left, LlvmKnownFPClass right, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Multiply(ApplyInputDenormalMode(left, mode), ApplyInputDenormalMode(right, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass Add(LlvmKnownFPClass left, LlvmKnownFPClass right, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Add(ApplyInputDenormalMode(left, mode), ApplyInputDenormalMode(right, mode), LlvmDenormalMode.IEEE), mode);
    public static LlvmKnownFPClass Square(LlvmKnownFPClass source, LlvmDenormalModeInfo mode) =>
        ApplyOutputDenormalMode(Square(ApplyInputDenormalMode(source, mode), LlvmDenormalMode.IEEE), mode);

    private static C OrderedStrictlyLess(C classes, bool orderedZeroSign)
    {
        uint mask = (uint)(classes & ~C.NaN);
        int zeros = BitOperations.TrailingZeroCount(mask);
        C result = (C)(zeros == 32 ? uint.MaxValue : (1U << zeros) - 1) & (C.All & ~C.NaN);
        if (!orderedZeroSign && (result & C.Zero) != C.Zero) result &= ~C.Zero;
        return result;
    }
    private static C OrderedStrictlyGreater(C classes, bool orderedZeroSign)
    {
        int zeros = BitOperations.LeadingZeroCount((uint)classes);
        C result = (C)(zeros == 32 ? uint.MaxValue : ~(uint.MaxValue >> zeros)) & (C.All & ~C.NaN);
        if (!orderedZeroSign && (result & C.Zero) != C.Zero) result &= ~C.Zero;
        return result;
    }

}
