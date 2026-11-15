// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp
// ConstantExpr::getBinOpIdentity / getBinOpAbsorber.
namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantExpr
{
    public static LlvmConstant? GetBinOpIdentity(LlvmOpcode opcode, LlvmType type,
        bool allowRhsConstant = false, bool noSignedZeros = false) => opcode switch
    {
        LlvmOpcode.Add or LlvmOpcode.Or or LlvmOpcode.Xor => GetNullValue(type),
        LlvmOpcode.Mul => GetIntegerOne(type),
        LlvmOpcode.And => GetAllOnes(type),
        LlvmOpcode.FAdd => GetFloatingIdentity(type, noSignedZeros ? 0.0 : BitConverter.UInt64BitsToDouble(1UL << 63)),
        LlvmOpcode.FMul => GetFloatingIdentity(type, 1.0),
        LlvmOpcode.Sub or LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr or LlvmOpcode.FSub
            when allowRhsConstant => GetNullValue(type),
        LlvmOpcode.SDiv or LlvmOpcode.UDiv when allowRhsConstant => GetIntegerOne(type),
        LlvmOpcode.FDiv when allowRhsConstant => GetFloatingIdentity(type, 1.0),
        _ => null
    };

    public static LlvmConstant? GetBinOpAbsorber(LlvmOpcode opcode, LlvmType type,
        bool allowLhsConstant = false) => opcode switch
    {
        LlvmOpcode.Or => GetAllOnes(type),
        LlvmOpcode.And or LlvmOpcode.Mul => GetNullValue(type),
        LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr or LlvmOpcode.SDiv or LlvmOpcode.UDiv or
            LlvmOpcode.URem or LlvmOpcode.SRem when allowLhsConstant => GetNullValue(type),
        _ => null
    };

    private static LlvmConstant GetIntegerOne(LlvmType type) => type is LlvmVectorType vector
        ? LlvmConstantVector.GetSplat(vector.ElementCount, GetIntegerOne(vector.ElementType))
        : LlvmConstantInt.Get((LlvmIntegerType)type, 1);

    private static LlvmConstant GetFloatingIdentity(LlvmType type, double value) => type is LlvmVectorType vector
        ? LlvmConstantVector.GetSplat(vector.ElementCount, GetFloatingIdentity(vector.ElementType, value))
        : LlvmConstantFP.Get(type, new LlvmAPFloat(value).Convert(type.GetFltSemantics()).Value);
}
