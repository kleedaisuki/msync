// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp canCreateUndefOrPoison and public poison/combined queries.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    private enum SourceUndefPoisonKind { UndefOnly, PoisonOnly, UndefOrPoison }

    public static bool CanCreatePoison(LlvmInstruction instruction, bool considerFlagsAndMetadata = true) =>
        CanCreateUndefined(instruction, SourceUndefPoisonKind.PoisonOnly, considerFlagsAndMetadata);
    public static bool CanCreateUndefOrPoison(LlvmInstruction instruction, bool considerFlagsAndMetadata = true) =>
        CanCreateUndefined(instruction, SourceUndefPoisonKind.UndefOrPoison, considerFlagsAndMetadata);
    internal static bool CanCreateUndef(LlvmInstruction instruction) =>
        CanCreateUndefined(instruction, SourceUndefPoisonKind.UndefOnly, true);

    private static bool CanCreateUndefined(LlvmInstruction instruction, SourceUndefPoisonKind kind, bool considerFlagsAndMetadata)
    {
        bool includesPoison = kind != SourceUndefPoisonKind.UndefOnly;
        bool noUndef = instruction is LlvmCallBase call && call.HasRetAttr(LlvmAttributeKind.NoUndef);
        if (considerFlagsAndMetadata && includesPoison && instruction.HasPoisonGeneratingAnnotations() && !noUndef) return true;
        switch (instruction.Opcode)
        {
            case LlvmOpcode.Shl: case LlvmOpcode.AShr: case LlvmOpcode.LShr:
                return includesPoison && !ShiftAmountKnownInRange(instruction.GetOperand(1)!);
            case LlvmOpcode.FPToSI: case LlvmOpcode.FPToUI:
                return true;
            case LlvmOpcode.Call:
                if (instruction is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } intrinsic)
                {
                    if (descriptor.Kind.Name is "llvm.ctlz" or "llvm.cttz" or "llvm.abs") return false;
                    if ((descriptor.Kind.Name is "llvm.sshl.sat" or "llvm.ushl.sat") &&
                        (!includesPoison || ShiftAmountKnownInRange(intrinsic.GetArgument(1)))) return false;
                }
                goto case LlvmOpcode.CallBr;
            case LlvmOpcode.CallBr: case LlvmOpcode.Invoke:
                return !noUndef && instruction is not LlvmCallBase { HasNoCreateUndefOrPoison: true };
            case LlvmOpcode.InsertElement: case LlvmOpcode.ExtractElement:
                var vector = (LlvmVectorType)instruction.GetOperand(0)!.Type;
                int indexOperand = instruction.Opcode == LlvmOpcode.InsertElement ? 2 : 1;
                return includesPoison && (instruction.GetOperand(indexOperand) is not LlvmConstantInt index ||
                    index.Value.GetLimitedValue() >= vector.ElementCount.KnownMinValue);
            case LlvmOpcode.ShuffleVector:
                return includesPoison && ((LlvmShuffleVectorInstruction)instruction).ShuffleMask.Contains(LlvmShuffleVectorInstruction.PoisonMaskElem);
            case LlvmOpcode.FNeg: case LlvmOpcode.PHI: case LlvmOpcode.Select: case LlvmOpcode.ExtractValue:
            case LlvmOpcode.InsertValue: case LlvmOpcode.Freeze: case LlvmOpcode.ICmp: case LlvmOpcode.FCmp: case LlvmOpcode.GetElementPtr:
                return false;
            case LlvmOpcode.AddrSpaceCast:
                return true;
            case LlvmOpcode.HeapAlloc:
                return false;
            default:
                return instruction is not (LlvmCastInstruction or LlvmBinaryOperator);
        }
    }
}
