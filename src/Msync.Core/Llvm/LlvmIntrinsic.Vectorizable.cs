// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/VectorUtils.cpp isTriviallyVectorizable.
namespace Msync.Core.Llvm;

public static partial class LlvmIntrinsic
{
    // Kind.Name is the source intrinsic identity, never an ordinary Function name.
    public static bool IsTriviallyVectorizable(LlvmIntrinsicKind kind) => kind.Name is
        "llvm.abs" or "llvm.bswap" or "llvm.bitreverse" or "llvm.ctpop" or "llvm.ctlz" or "llvm.cttz" or
        "llvm.fshl" or "llvm.fshr" or "llvm.smax" or "llvm.smin" or "llvm.umax" or "llvm.umin" or
        "llvm.sadd.sat" or "llvm.ssub.sat" or "llvm.uadd.sat" or "llvm.usub.sat" or
        "llvm.smul.fix" or "llvm.smul.fix.sat" or "llvm.umul.fix" or "llvm.umul.fix.sat" or
        "llvm.uadd.with.overflow" or "llvm.sadd.with.overflow" or "llvm.usub.with.overflow" or "llvm.ssub.with.overflow" or
        "llvm.umul.with.overflow" or "llvm.smul.with.overflow" or
        "llvm.sqrt" or "llvm.asin" or "llvm.acos" or "llvm.atan" or "llvm.atan2" or "llvm.sin" or "llvm.cos" or
        "llvm.sincos" or "llvm.sincospi" or "llvm.tan" or "llvm.sinh" or "llvm.cosh" or "llvm.tanh" or
        "llvm.exp" or "llvm.exp10" or "llvm.exp2" or "llvm.frexp" or "llvm.ldexp" or "llvm.log" or "llvm.log10" or "llvm.log2" or
        "llvm.fabs" or "llvm.minnum" or "llvm.maxnum" or "llvm.minimum" or "llvm.maximum" or "llvm.minimumnum" or "llvm.maximumnum" or
        "llvm.modf" or "llvm.copysign" or "llvm.floor" or "llvm.ceil" or "llvm.trunc" or "llvm.rint" or "llvm.nearbyint" or
        "llvm.round" or "llvm.roundeven" or "llvm.pow" or "llvm.fma" or "llvm.fmuladd" or "llvm.is.fpclass" or "llvm.powi" or
        "llvm.canonicalize" or "llvm.fptosi.sat" or "llvm.fptoui.sat" or "llvm.lround" or "llvm.llround" or "llvm.lrint" or "llvm.llrint" or
        "llvm.ucmp" or "llvm.scmp" or "llvm.clmul" or "llvm.pdep" or "llvm.pext" or "llvm.smulh" or "llvm.umulh";
}
