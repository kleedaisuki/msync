// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CaptureTracking.cpp DetermineUseCaptureKind.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmCaptureTracking
{
    public static LlvmUseCaptureInfo DetermineUseCaptureKind(LlvmUse use, LlvmValue @base,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "capture.classify_use");
        if (use.User is not LlvmInstruction instruction) return new(LlvmCaptureComponents.All);
        switch (instruction.Opcode)
        {
            case LlvmOpcode.Call:
            case LlvmOpcode.Invoke:
                var call = (LlvmCallBase)instruction;
                if (use.OperandNo == call.OperandCount - 1) return new(LlvmCaptureComponents.None);
                if (call.IsBundleOperand(use.OperandNo) && IsDeoptBundleUse(call, use))
                { Count(metrics, "capture.deopt_bundle"); return new(LlvmCaptureComponents.None); }
                // The represented CallBase has no capture/readonly attributes or
                // intrinsic identity: getCaptureInfo follows CaptureInfo::all().
                Count(metrics, "capture.unannotated_call_argument");
                return new(LlvmCaptureComponents.All, LlvmCaptureComponents.All);
            case LlvmOpcode.Load:
                return new(((LlvmLoadInstruction)instruction).IsVolatile ? LlvmCaptureComponents.Address : LlvmCaptureComponents.None);
            case LlvmOpcode.VAArg:
                return new(LlvmCaptureComponents.None);
            case LlvmOpcode.Store:
                if (use.OperandNo == 0) return new(LlvmCaptureComponents.All);
                return new(((LlvmStoreInstruction)instruction).IsVolatile ? LlvmCaptureComponents.Address : LlvmCaptureComponents.None);
            case LlvmOpcode.GetElementPtr:
                return instruction.Type.IsVectorTy() ? new(LlvmCaptureComponents.All) : LlvmUseCaptureInfo.Passthrough;
            case LlvmOpcode.BitCast:
            case LlvmOpcode.PHI:
            case LlvmOpcode.Select:
            case LlvmOpcode.AddrSpaceCast:
                return LlvmUseCaptureInfo.Passthrough;
            case LlvmOpcode.PtrToAddr:
                return new(LlvmCaptureComponents.Address);
            case LlvmOpcode.ICmp:
                var compare = (LlvmICmpInstruction)instruction;
                bool equality = compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
                return new(instruction.GetOperand(1 - use.OperandNo) is LlvmConstantPointerNull && equality && use.Value == @base
                    ? LlvmCaptureComponents.AddressIsNull : LlvmCaptureComponents.Address);
            default:
                Count(metrics, "capture.default_use");
                return new(LlvmCaptureComponents.All);
        }
    }

    private static bool IsDeoptBundleUse(LlvmCallBase call, LlvmUse use)
    {
        if (call.GetOperandBundle("deopt") is not { } bundle) return false;
        for (int i = 0; i < bundle.InputCount; ++i)
            if (bundle.GetInputUse(i) == use) return true;
        return false;
    }
}
