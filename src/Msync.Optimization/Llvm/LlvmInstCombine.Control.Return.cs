// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitReturnInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitReturnInst(LlvmReturnInstruction instruction)
        {
            if (instruction.ReturnValue is not { } value) return null;
            LlvmType type = value.Type;
            if (type is LlvmPointerType pointer)
            {
                bool useProvenance = F.ReturnAttributes.DereferenceableBytes is > 0 &&
                    !LlvmFunction.NullPointerIsDefined(F, pointer.AddressSpace);
                if (F.ReturnAttributes.Has(LlvmAttributeKind.NonNull) || useProvenance)
                    if (SimplifyNonNullOperand(value, useProvenance) is { } nonNull)
                    {
                        Count("control.return.nonnull-operand");
                        return ReplaceOperand(instruction, 0, nonNull);
                    }
            }
            if (!LlvmAttributeFunctions.IsNoFPClassCompatibleType(type)) return null;
            LlvmFPClassTest excluded = F.ReturnAttributes.NoFPClass;
            if (excluded == LlvmFPClassTest.None) return null;
            if (SimplifyDemandedFPClass(instruction, 0, ~excluded, out _, Query(instruction))) return instruction;
            return null;
        }
    }
}
