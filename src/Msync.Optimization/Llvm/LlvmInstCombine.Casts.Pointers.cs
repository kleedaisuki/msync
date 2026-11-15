// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCasts.cpp 2740–2875, 3638.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitIntToPtr(LlvmCastInstruction instruction)
        {
            Count("casts.inttoptr.queries");
            var source = instruction.Operand;
            uint space = instruction.Type.GetPointerAddressSpace();
            if (source.Type.GetScalarSizeInBits() != DL.GetPointerSizeInBits(space))
            {
                var integer = DL.GetIntPtrType(instruction.Context, space);
                Count("casts.inttoptr.normalize_width");
                return new LlvmCastInstruction(LlvmOpcode.IntToPtr, Builder.CreateZExtOrTrunc(source, integer), instruction.Type);
            }
            if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true } add)
            {
                LlvmCastInstruction? cast = add.Left as LlvmCastInstruction;
                LlvmValue offset = add.Right;
                if (cast?.Opcode != LlvmOpcode.PtrToInt) { cast = add.Right as LlvmCastInstruction; offset = add.Left; }
                if (cast?.Opcode == LlvmOpcode.PtrToInt && DL.GetTypeSizeInBits(cast.Type) == DL.GetTypeSizeInBits(cast.Operand.Type) &&
                    cast.Operand.Type.GetPointerAddressSpace() == space && instruction.Users.All(UsesPointerAsInt))
                {
                    Count("casts.inttoptr.add_to_gep");
                    return new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(instruction.Context), cast.Operand, [offset]);
                }
            }
            return CommonCastTransforms(instruction);
        }
        private static bool UsesPointerAsInt(LlvmUser user)
        {
            if (user is LlvmICmpInstruction or LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt }) return true;
            return user is LlvmPhiInstruction { HasOneUse: true } phi &&
                phi.Users.First() is LlvmICmpInstruction or LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt };
        }
        private LlvmValue? FoldPtrToIntOrAddrOfGEP(LlvmType integerType, LlvmValue pointer)
        {
            Count("casts.pointer_gep.queries");
            var pointerType = pointer.Type;
            List<LlvmGetElementPtrInstruction> geps = [];
            while (pointer is LlvmGetElementPtrInstruction { HasOneUse: true } gep)
            { geps.Add(gep); pointer = gep.PointerOperand; }
            if (geps.Count == 0 || pointer.Type != pointerType) return null;
            var indexType = LlvmIntegerType.Get(pointer.Context, DL.GetIndexSizeInBits(pointerType.GetPointerAddressSpace()));
            LlvmValue result;
            if (pointer is LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr, HasOneUse: true } cast &&
                cast.Operand.Type == integerType && integerType == indexType) result = cast.Operand;
            else if (pointer is LlvmConstantPointerNull) result = LlvmConstant.GetNullValue(indexType);
            else return null;
            for (int index = geps.Count - 1; index >= 0; index--)
                result = Builder.CreateAdd(result, EmitGEPOffset(geps[index]), nuw: geps[index].NoWrapFlags.HasNoUnsignedWrap);
            Count("casts.pointer_gep.rewrites");
            return Builder.CreateZExtOrTrunc(result, integerType);
        }
        private LlvmInstruction? VisitPtrToInt(LlvmCastInstruction instruction)
        {
            Count("casts.ptrtoint.queries");
            var source = instruction.Operand;
            uint space = source.Type.GetPointerAddressSpace();
            if (instruction.Type.GetScalarSizeInBits() != DL.GetPointerSizeInBits(space))
            {
                var integer = DL.GetIntPtrType(instruction.Context, space);
                var pointer = Builder.CreatePtrToInt(source, integer);
                var opcode = integer.GetScalarSizeInBits() > instruction.Type.GetScalarSizeInBits() ? LlvmOpcode.Trunc : LlvmOpcode.ZExt;
                Count("casts.ptrtoint.normalize_width");
                return new LlvmCastInstruction(opcode, pointer, instruction.Type);
            }
            if (source is LlvmCallInstruction { HasOneUse: true } mask && mask.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.PtrMask &&
                mask.GetArgument(1).Type == instruction.Type)
            {
                Count("casts.ptrtoint.mask");
                return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreatePtrToInt(mask.GetArgument(0), instruction.Type), mask.GetArgument(1));
            }
            if (FoldPtrToIntOrAddrOfGEP(instruction.Type, source) is { } folded) return ReplaceInstUsesWith(instruction, folded);
            return CommonCastTransforms(instruction);
        }
        private LlvmInstruction? VisitPtrToAddr(LlvmCastInstruction instruction)
        {
            Count("casts.ptrtoaddr.queries");
            if (instruction.Operand is LlvmCallInstruction { HasOneUse: true } mask && mask.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.PtrMask &&
                mask.GetArgument(1).Type == instruction.Type)
            {
                Count("casts.ptrtoaddr.mask");
                var addressType = DL.GetIndexType(instruction.Context, mask.GetArgument(0).Type.GetPointerAddressSpace());
                var address = Builder.CreateCast(LlvmOpcode.PtrToAddr, mask.GetArgument(0), addressType);
                return new LlvmBinaryOperator(LlvmOpcode.And, address, mask.GetArgument(1));
            }
            if (FoldPtrToIntOrAddrOfGEP(instruction.Type, instruction.Operand) is { } folded) return ReplaceInstUsesWith(instruction, folded);
            return CommonCastTransforms(instruction);
        }
        private LlvmInstruction? VisitAddrSpaceCast(LlvmCastInstruction instruction) => CommonCastTransforms(instruction);
    }
}
