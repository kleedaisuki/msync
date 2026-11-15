// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp frexp(IEEEFloat).
namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    public static bool SemanticsHasInf(LlvmAPFloatSemantics semantics) => true;
    public static bool HasSignBitInMSB(LlvmAPFloatSemantics semantics) => true;

    public LlvmAPFloat Frexp(out int exponent, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        exponent = ILogB();
        if (exponent == IEK_NaN) return MakeQuiet();
        if (exponent == IEK_Inf) return this;
        exponent = exponent == IEK_Zero ? 0 : exponent + 1;
        return Scalbn(-exponent, mode).Value;
    }
}
