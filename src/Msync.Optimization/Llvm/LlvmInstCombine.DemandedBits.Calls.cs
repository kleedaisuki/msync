// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedUseBits Call source cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyDemandedCall(LlvmCallInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            if (instruction.CalledFunction?.IntrinsicDescriptor is not { } descriptor)
            { known = ComputeDemandedKnownBits(instruction, query, depth); return null; }
            uint width = demanded.BitWidth;
            var kind = descriptor.Kind;
            if (kind == LlvmIntrinsicKind.Abs && demanded.IsOne) return instruction.GetArgument(0);
            if (kind == LlvmIntrinsicKind.CTPop && demanded.IsOne && width % 2 == 0 &&
                instruction.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt { Value.IsAllOnes: true } } complement)
                return InsertNewInstWith(LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [complement.Left], F.Parent!, Metrics), instruction);
            if (kind == LlvmIntrinsicKind.BSwap)
            {
                uint leading = demanded.CountLeadingZeros() / 8 * 8, trailing = demanded.CountTrailingZeros() / 8 * 8;
                if (width - leading - trailing == 8)
                {
                    var shifted = new LlvmBinaryOperator(leading > trailing ? LlvmOpcode.LShr : LlvmOpcode.Shl,
                        instruction.GetArgument(0), LlvmConstantInt.Get((LlvmIntegerType)instruction.Type,
                            leading > trailing ? leading - trailing : trailing - leading));
                    shifted.TakeName(instruction); return InsertNewInstWith(shifted, instruction);
                }
            }
            if (kind == LlvmIntrinsicKind.PtrMask)
                return SimplifyDemandedPointerMask(instruction, demanded, ref known, query, depth);
            if ((kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr) && instruction.GetArgument(2) is LlvmConstantInt constant)
            {
                uint amount = (uint)constant.Value.URem(new LlvmAPInt(width, width)).GetZExtValue();
                if (kind == LlvmIntrinsicKind.FShr) amount = width - amount;
                var fromLeft = demanded.LShr(amount);
                var fromRight = demanded.Shl(width - amount);
                LlvmKnownBits left = new(width), right = new(width);
                if (instruction.GetArgument(0) != instruction.GetArgument(1))
                {
                    if (SimplifyDemandedBits(instruction, 0, fromLeft, ref left, query, depth + 1) ||
                        SimplifyDemandedBits(instruction, 1, fromRight, ref right, query, depth + 1))
                    { instruction.DropPoisonGeneratingAnnotations(); return instruction; }
                }
                else
                {
                    left = ComputeDemandedKnownBits(instruction.GetArgument(0), query with { Context = instruction }, depth + 1);
                    if (DemandedSubset(fromLeft, left.Zero | left.One) &&
                        !(instruction.GetArgument(0) is LlvmConstantInt leftConstant && leftConstant.Value == left.One))
                    {
                        ReplaceOperand(instruction, 0, DemandedConstant(instruction.Type, left.One));
                        instruction.DropPoisonGeneratingAnnotations(); return instruction;
                    }
                    right = ComputeDemandedKnownBits(instruction.GetArgument(1), query with { Context = instruction }, depth + 1);
                    if (DemandedSubset(fromRight, right.Zero | right.One) &&
                        !(instruction.GetArgument(1) is LlvmConstantInt rightConstant && rightConstant.Value == right.One))
                    {
                        ReplaceOperand(instruction, 1, DemandedConstant(instruction.Type, right.One));
                        instruction.DropPoisonGeneratingAnnotations(); return instruction;
                    }
                }
                known = new(left.Zero.Shl(amount) | right.Zero.LShr(width - amount), left.One.Shl(amount) | right.One.LShr(width - amount));
                return null;
            }
            if ((kind == LlvmIntrinsicKind.UMax || kind == LlvmIntrinsicKind.UMin) && instruction.GetArgument(1) is LlvmConstantInt bound)
            {
                uint trailing = demanded.CountTrailingZeros();
                if (kind == LlvmIntrinsicKind.UMax && trailing >= bound.Value.GetActiveBits()) return instruction.GetArgument(0);
                if (kind == LlvmIntrinsicKind.UMin && trailing >= bound.BitWidth - bound.Value.CountLeadingOnes()) return instruction.GetArgument(0);
            }
            known = ComputeDemandedKnownBits(instruction, query, depth);
            return null;
        }

        private LlvmValue? SimplifyDemandedPointerMask(LlvmCallInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth, maskWidth = instruction.GetArgument(1).Type.GetIntegerBitWidth();
            LlvmKnownBits left = new(width), right = new(maskWidth);
            if (SimplifyDemandedBits(instruction, 0, demanded, ref left, query, depth + 1) ||
                SimplifyDemandedBits(instruction, 1, (demanded & ~left.Zero).ZExtOrTrunc(maskWidth), ref right, query, depth + 1)) return instruction;
            right = right.AnyExtOrTrunc(width);
            known = left & right;
            if (DemandedSubset(demanded, known.Zero) && instruction.GetArgument(1) is not LlvmConstantInt { IsZero: true })
                return ReplaceOperand(instruction, 1, LlvmConstant.GetNullValue(instruction.GetArgument(1).Type));
            if (DemandedSubset(demanded, right.One | left.Zero)) return instruction.GetArgument(0);
            if (ShrinkDemandedConstant(instruction, 1, (demanded & ~left.Zero).ZExtOrTrunc(maskWidth))) return instruction;
            if (instruction.GetArgument(0) is not LlvmGetElementPtrInstruction gep || !gep.SourceElementType.IsIntegerTy(8) ||
                gep.IndexCount != 1 || gep.GetIndex(0) is not LlvmConstantInt index ||
                index.Value.TryZExtValue() is not { } offset || instruction.GetArgument(1) is not LlvmConstantInt mask ||
                mask.Value.TryZExtValue() is not { } maskValue) return null;
            left = ComputeDemandedKnownBits(gep.PointerOperand, query with { Context = instruction }, depth + 1);
            if (left.IsZero) return null;
            uint trailing = left.CountMinTrailingZeros();
            ulong alignmentBits = (1UL << (int)trailing) - 1;
            ulong newOffset = (offset & ~alignmentBits) | (offset & alignmentBits & maskValue);
            if (newOffset == offset) return null;
            Builder.SetInsertPoint(instruction);
            var indexType = LlvmIntegerType.Get(instruction.Context, DL.GetIndexTypeSizeInBits(gep.PointerOperand.Type));
            var maskedPointer = Builder.CreateGep(gep.SourceElementType, gep.PointerOperand,
                [LlvmConstantInt.Get(indexType, newOffset)], gep.Name, gep.NoWrapFlags.IsInBounds ? LlvmGepNoWrapFlags.InBounds : LlvmGepNoWrapFlags.None);
            ReplaceOperand(instruction, 0, maskedPointer);
            Count("demanded_bits.ptrmask.gep_offset");
            return instruction;
        }
    }
}
