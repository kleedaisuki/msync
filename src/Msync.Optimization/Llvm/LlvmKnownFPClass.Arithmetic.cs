// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownFPClass.cpp
// fadd_impl, fadd/fsub/fmul/fdiv/frem and self variants.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public partial struct LlvmKnownFPClass
{

private static LlvmKnownFPClass AddImpl(LlvmKnownFPClass KnownLHS,
                              LlvmKnownFPClass KnownRHS, bool nearestTiesToEven) {
  LlvmKnownFPClass Known = new();

  Known.PropagateNonSNaN(KnownLHS, KnownRHS);

  if (KnownLHS.IsKnownNeverNaN() && KnownRHS.IsKnownNeverNaN() &&
      (KnownLHS.IsKnownNever(C.PosInfinity) || KnownRHS.IsKnownNever(C.NegInfinity)) &&
      (KnownLHS.IsKnownNever(C.NegInfinity) || KnownRHS.IsKnownNever(C.PosInfinity)))
    Known.KnownNot(C.NaN);

  if (KnownLHS.CannotBeOrderedLessThanZero() &&
      KnownRHS.CannotBeOrderedLessThanZero()) {
    Known.KnownNot(OrderedLessThanZeroMask);

    if (KnownLHS.IsKnownNever(C.Zero | C.PosSubnormal) ||
        KnownRHS.IsKnownNever(C.Zero | C.PosSubnormal))
      Known.KnownNot(C.Zero | C.PosSubnormal);
  }

  if (KnownLHS.CannotBeOrderedGreaterThanZero() &&
      KnownRHS.CannotBeOrderedGreaterThanZero()) {
    Known.KnownNot(OrderedGreaterThanZeroMask);

    if (KnownLHS.IsKnownNever(C.Zero | C.NegSubnormal) ||
        KnownRHS.IsKnownNever(C.Zero | C.NegSubnormal))
      Known.KnownNot(C.Zero | C.NegSubnormal);
  }

  if (nearestTiesToEven) {

    if (KnownLHS.IsKnownNever(C.NegZero) || KnownRHS.IsKnownNever(C.NegZero))
      Known.KnownNot(C.NegZero);
  }

  return Known;
}

public static LlvmKnownFPClass Add(LlvmKnownFPClass KnownLHS_,
                                LlvmKnownFPClass KnownRHS_,
                                LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);

  LlvmKnownFPClass Known =
      AddImpl(KnownLHS, KnownRHS, true);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass AddSelf(LlvmKnownFPClass KnownSrc_,
                                     LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownSrc = ApplyInputDenormalMode(KnownSrc_, Mode);

  LlvmKnownFPClass Known =
      AddImpl(KnownSrc, KnownSrc, true);

  if (KnownSrc.IsKnownNever(C.PosSubnormal))
    Known.KnownNot(C.PosSubnormal);
  if (KnownSrc.IsKnownNever(C.NegSubnormal))
    Known.KnownNot(C.NegSubnormal);

  if (KnownSrc.IsKnownNever(C.PosZero))
    Known.KnownNot(C.PosZero);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Subtract(LlvmKnownFPClass KnownLHS_,
                                LlvmKnownFPClass KnownRHS_,
                                LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);

  KnownRHS.Negate();

  LlvmKnownFPClass Known =
      AddImpl(KnownLHS, KnownRHS, true);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Multiply(LlvmKnownFPClass KnownLHS_,
                                LlvmKnownFPClass KnownRHS_,
                                LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);

  LlvmKnownFPClass Known = new();

  Known.PropagateNonSNaN(KnownLHS, KnownRHS);

  if (KnownLHS.IsKnownNeverNaN() && KnownRHS.IsKnownNeverNaN() &&
      (KnownRHS.IsKnownNeverInfinity() || KnownLHS.IsKnownNever(C.Zero)) &&
      (KnownLHS.IsKnownNeverInfinity() || KnownRHS.IsKnownNever(C.Zero)))
    Known.KnownNot(C.NaN);

  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity)))
    Known.KnownNot(C.PosInfinity);
  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity)))
    Known.KnownNot(C.NegInfinity);

  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal)))
    Known.KnownNot(C.PosSubnormal | C.PosNormal);
  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal)))
    Known.KnownNot(C.NegSubnormal | C.NegNormal);

  if ((KnownLHS.IsKnownNever(C.PosFinite) ||
       KnownRHS.IsKnownNever(C.PosFinite)) &&
      (KnownLHS.IsKnownNever(C.NegFinite) ||
       KnownRHS.IsKnownNever(C.NegFinite)))
    Known.KnownNot(C.PosZero);
  if ((KnownLHS.IsKnownNever(C.PosFinite) ||
       KnownRHS.IsKnownNever(C.NegFinite)) &&
      (KnownLHS.IsKnownNever(C.NegFinite) ||
       KnownRHS.IsKnownNever(C.PosFinite)))
    Known.KnownNot(C.NegZero);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass Multiply(LlvmKnownFPClass KnownLHS,
                                LlvmAPFloat CRHS, LlvmDenormalMode Mode) {

  const int Precision = 53;
  int MantissaBits = Precision - 1;

  int MinKnownExponent = CRHS.ILogB();
  bool CannotBeSubnormal = (MinKnownExponent >= MantissaBits);

  LlvmKnownFPClass Known = Multiply(KnownLHS, new LlvmKnownFPClass(CRHS), Mode);
  if (CannotBeSubnormal)
    Known.KnownNot(C.Subnormal);

  if (KnownLHS.IsKnownNever(C.Infinity)) {
    if (MinKnownExponent < 0)
      Known.KnownNot(C.Infinity);
    else if (MinKnownExponent == 0 && (CRHS.BitcastToAPInt().GetZExtValue() & 0x7fffffffffffffffUL) == 0x3ff0000000000000UL)
      Known.KnownNot(C.Infinity);
  }

  return Known;
}

public static LlvmKnownFPClass Divide(LlvmKnownFPClass KnownLHS_,
                                LlvmKnownFPClass KnownRHS_,
                                LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);

  LlvmKnownFPClass Known = new();

  Known.PropagateNonSNaN(KnownLHS, KnownRHS);

  if (KnownLHS.IsKnownNeverNaN() && KnownRHS.IsKnownNeverNaN() &&
      (KnownLHS.IsKnownNeverInfinity() || KnownRHS.IsKnownNeverInfinity()) &&
      (KnownLHS.IsKnownNever(C.Zero) || KnownRHS.IsKnownNever(C.Zero)))
    Known.KnownNot(C.NaN);

  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity) ||
       KnownRHS.IsKnownNever(C.PosFinite)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity) ||
       KnownRHS.IsKnownNever(C.NegFinite)))
    Known.KnownNot(C.PosInfinity);
  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity) ||
       KnownRHS.IsKnownNever(C.NegFinite)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity) ||
       KnownRHS.IsKnownNever(C.PosFinite)))
    Known.KnownNot(C.NegInfinity);

  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal)))
    Known.KnownNot(C.PosSubnormal | C.PosNormal);
  if ((KnownLHS.IsKnownNever(C.PosSubnormal | C.PosNormal) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal)) &&
      (KnownLHS.IsKnownNever(C.NegSubnormal | C.NegNormal) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal)))
    Known.KnownNot(C.NegSubnormal | C.NegNormal);

  if ((KnownLHS.IsKnownNever(C.PosFinite) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity)) &&
      (KnownLHS.IsKnownNever(C.NegFinite) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity)))
    Known.KnownNot(C.PosZero);
  if ((KnownLHS.IsKnownNever(C.PosFinite) ||
       KnownRHS.IsKnownNever(C.NegSubnormal | C.NegNormal | C.NegInfinity)) &&
      (KnownLHS.IsKnownNever(C.NegFinite) ||
       KnownRHS.IsKnownNever(C.PosSubnormal | C.PosNormal | C.PosInfinity)))
    Known.KnownNot(C.NegZero);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass DivideSelf(LlvmKnownFPClass KnownSrc_,
                                     LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownSrc = ApplyInputDenormalMode(KnownSrc_, Mode);

  LlvmKnownFPClass Known = new(C.NaN | C.PosNormal);

  Known.PropagateNonSNaN(KnownSrc);

  if (KnownSrc.IsKnownNeverInfOrNaN() && KnownSrc.IsKnownNever(C.Zero))
    Known.KnownNot(C.NaN);

  return Known;
}

public static LlvmKnownFPClass Remainder(LlvmKnownFPClass KnownLHS_,
                                LlvmKnownFPClass KnownRHS_,
                                LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownLHS = ApplyInputDenormalMode(KnownLHS_, Mode);
  LlvmKnownFPClass KnownRHS = ApplyInputDenormalMode(KnownRHS_, Mode);
  LlvmKnownFPClass Known = new();

  Known.KnownNot(C.Infinity);

  Known.PropagateNonSNaN(KnownLHS, KnownRHS);

  if (KnownLHS.IsKnownNeverInfOrNaN() && KnownRHS.IsKnownNeverNaN() &&
      KnownRHS.IsKnownNever(C.Zero)) {
    Known.KnownNot(C.NaN);
  }

  if (KnownLHS.IsKnownNever(C.PosNormal | C.PosSubnormal))
    Known.KnownNot(C.PosNormal | C.PosSubnormal);
  if (KnownLHS.IsKnownNever(C.NegNormal | C.NegSubnormal))
    Known.KnownNot(C.NegNormal | C.NegSubnormal);

  if (KnownLHS.IsKnownNever(C.NegFinite))
    Known.KnownNot(C.NegZero);

  if (KnownLHS.IsKnownNever(C.PosFinite))
    Known.KnownNot(C.PosZero);

  return ApplyOutputDenormalMode(Known, Mode);
}

public static LlvmKnownFPClass RemainderSelf(LlvmKnownFPClass KnownSrc_,
                                     LlvmDenormalMode Mode) {
  LlvmKnownFPClass KnownSrc = ApplyInputDenormalMode(KnownSrc_, Mode);

  LlvmKnownFPClass Known = new(C.NaN | C.Zero);

  Known.PropagateNonSNaN(KnownSrc);

  if (KnownSrc.IsKnownNeverInfOrNaN() && KnownSrc.IsKnownNever(C.Zero))
    Known.KnownNot(C.NaN);

  if (KnownSrc.IsKnownNever(C.PosNormal | C.PosSubnormal))
    Known.KnownNot(C.PosZero);
  if (KnownSrc.IsKnownNever(C.NegNormal | C.NegSubnormal))
    Known.KnownNot(C.NegZero);

  return Known;
}

}
