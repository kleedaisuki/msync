// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp fpclassTestIsFCmp0.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
using P = Msync.Core.Llvm.LlvmCmpPredicate;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static P FPClassTestIsFCmp0(C mask, LlvmFunction function, LlvmType type)
        {
            var mode = function.GetDenormalMode(type.GetScalarType().GetFltSemantics());
            bool ieee = mode.Input == LlvmDenormalMode.IEEE, daz = mode.InputsAreZero;
            return mask switch
            {
                C.Zero when ieee => P.FCmpOeq,
                C.Zero | C.Subnormal when daz => P.FCmpOeq,
                C.Positive | C.NegZero when ieee => P.FCmpOge,
                C.Positive | C.NegZero | C.NegSubnormal when daz => P.FCmpOge,
                C.PosSubnormal | C.PosNormal | C.PosInfinity when ieee => P.FCmpOgt,
                C.Negative | C.PosZero when ieee => P.FCmpOle,
                C.Negative | C.PosZero | C.PosSubnormal when daz => P.FCmpOle,
                C.NegSubnormal | C.NegNormal | C.NegInfinity when ieee => P.FCmpOlt,
                C.PosNormal | C.PosInfinity when daz => P.FCmpOgt,
                C.NegNormal | C.NegInfinity when daz => P.FCmpOlt,
                C.All & ~C.Zero & ~C.NaN when ieee => P.FCmpOne,
                C.All & ~(C.Zero | C.Subnormal) & ~C.NaN when daz => P.FCmpOne,
                _ => P.BadFCmpPredicate
            };
        }
    }
}
