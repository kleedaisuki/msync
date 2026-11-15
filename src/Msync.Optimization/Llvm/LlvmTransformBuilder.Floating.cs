// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IRBuilder.h
// CreateUnaryIntrinsic/CreateBinaryIntrinsic, CreateFAbs/CreateCopySign/CreateIsFPClass.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTransformBuilder
{
    public LlvmValue CreateFAbs(LlvmValue value, LlvmFastMathFlags? flags = null) =>
        CreateFloatingCall(LlvmIntrinsicKind.FAbs, [value], "", flags);

    public LlvmValue CreateCopySign(LlvmValue magnitude, LlvmValue sign, string name = "", LlvmFastMathFlags? flags = null) =>
        CreateFloatingCall(LlvmIntrinsicKind.CopySign, [magnitude, sign], name, flags);

    public LlvmValue CreateIsFPClass(LlvmValue value, LlvmFPClassTest mask) =>
        CreateIntrinsic(LlvmIntrinsicKind.IsFPClass, [value, LlvmConstantInt.Get(LlvmType.GetInt32Ty(value.Context), (uint)mask)]);

    private LlvmValue CreateFloatingCall(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        string name, LlvmFastMathFlags? flags)
    {
        var result = CreateIntrinsic(kind, arguments, name);
        if (result is LlvmCallInstruction call) call.FastMathFlags = flags ?? FastMathFlags;
        return result;
    }
}
