// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/TargetLibraryInfo.cpp isCallingConvCCompatible, IR/CallingConv.h.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmTargetLibraryInfo
{
    public bool IsCallingConvCCompatible(LlvmCallBase call) =>
        IsCallingConvCCompatible(call.CallingConvention, call.Parent!.Parent!.Parent!.TargetTriple, call.FunctionType);
    public bool IsCallingConvCCompatible(LlvmFunction function) =>
        IsCallingConvCCompatible(function.CallingConvention, function.Parent!.TargetTriple, function.FunctionType);

    private static bool IsCallingConvCCompatible(uint convention, LlvmTriple triple, LlvmFunctionType type)
    {
        if (convention == 0) return true;
        if (convention is not (66 or 67 or 68) || triple.IsiOS()) return false;
        LlvmType result = type.ReturnType;
        if (!result.IsPointerTy() && !result.IsIntegerTy() && !result.IsVoidTy()) return false;
        foreach (var parameter in type.Parameters)
            if (!parameter.IsPointerTy() && !parameter.IsIntegerTy()) return false;
        return true;
    }
}
