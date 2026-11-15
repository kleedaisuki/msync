// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp visitGetElementPtrInst.
using System.Numerics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitGetElementPtrInst(LlvmGetElementPtrInstruction gep)
        {
            Count("gep.visits");
            LlvmValue pointer = gep.PointerOperand;
            LlvmValue[] indices = gep.Indices.ToArray();
            LlvmType resultType = gep.Type, elementType = gep.SourceElementType;
            if (LlvmInstructionSimplify.SimplifyGepInst(elementType, pointer, indices, gep.NoWrapFlags, Query(gep)) is { } simplified)
            {
                Count("gep.simplified");
                return ReplaceInstUsesWith(gep, simplified);
            }
            if (resultType is LlvmFixedVectorType vector)
            {
                uint width = vector.ElementCount.KnownMinValue;
                LlvmAPInt poison = LlvmAPInt.GetZero(width);
                if (SimplifyDemandedVectorElts(gep, LlvmAPInt.GetAllOnes(width), ref poison) is { } demanded)
                    return ReferenceEquals(demanded, gep) ? gep : ReplaceInstUsesWith(gep, demanded);
            }
            if (NormalizeGEPIndices(gep)) return gep;
            if (!elementType.IsIntegerTy(8) && gep.HasAllConstantIndices)
            {
                var offset = LlvmAPInt.GetZero(DL.GetIndexTypeSizeInBits(resultType));
                if (LlvmValueTracking.AccumulateConstantOffset(gep, DL, ref offset, Metrics))
                {
                    Count("gep.constant_offset_canonicalized");
                    return ReplaceInstUsesWith(gep, Builder.CreatePtrAdd(pointer, LlvmConstantInt.Get(gep.Context, offset), noWrapFlags: gep.NoWrapFlags));
                }
            }
            if (ShouldCanonicalizeGEPToPtrAdd(gep))
            {
                LlvmValue offset = EmitGEPOffset(gep);
                Count("gep.offset_arithmetic_canonicalized");
                return ReplaceInstUsesWith(gep, Builder.CreatePtrAdd(pointer, offset, noWrapFlags: gep.NoWrapFlags));
            }
            LlvmType canonicalElement = elementType is LlvmArrayType array ? array.ElementType : elementType;
            if (!canonicalElement.IsIntegerTy(8) || indices.Length != 1)
            {
                Count("gep.type_canonicalized");
                return ReplaceInstUsesWith(gep, Builder.CreateGep(elementType, pointer, indices, noWrapFlags: gep.NoWrapFlags));
            }
            if (resultType.IsVectorTy() && gep.Operands.Any(operand => operand!.Type.IsVectorTy() &&
                LlvmValueTracking.GetSplatValue(operand!, Metrics) is not null))
                return ScalarizeGEPSplats(gep);
            if (pointer is LlvmPhiInstruction phi && FoldGEPOfPhi(gep, phi) is { } foldedPhi)
                return ReplaceOperand(gep, 0, foldedPhi);
            if (LlvmGepOperator.TryGet(pointer, out var source) && VisitGEPOfGEP(gep, source) is { } combined) return combined;
            if (gep.IndexCount == 1 && TryFoldGEPSingleIndex(gep, out var singleIndex)) return singleIndex;
            if (resultType.IsVectorTy()) return null;
            if (!gep.NoWrapFlags.IsInBounds && DeriveGEPInBounds(gep, pointer, indices) is { } inBounds) return inBounds;
            if (gep.NoWrapFlags.HasNoUnsignedSignedWrap && !gep.NoWrapFlags.HasNoUnsignedWrap &&
                gep.Indices.All(index => IsKnownNonNegative(index, gep)))
            {
                gep.NoWrapFlags |= LlvmGepNoWrapFlags.NoUnsignedWrap;
                Count("gep.derived_nuw");
                return gep;
            }
            if (gep.IndexCount == 1 && SplitGEPAdd(gep) is { } split) return split;
            if (FoldSelectGEP(gep) is { } threadedSelect) return threadedSelect;
            if (indices.Length == 1 && gep.NoWrapFlags.IsInBounds && gep.NoWrapFlags.HasNoUnsignedWrap &&
                indices[0] is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem, HasOneUse: true } remainder &&
                IsKnownToBeAPowerOfTwo(remainder.Right, true, gep))
            {
                LlvmValue unsigned = Builder.CreateBinary(LlvmOpcode.URem, remainder.Left, remainder.Right, remainder.Name);
                Count("gep.srem_to_urem");
                return new LlvmGetElementPtrInstruction(elementType, pointer, [unsigned], gep.NoWrapFlags);
            }
            return null;
        }

        private bool NormalizeGEPIndices(LlvmGetElementPtrInstruction gep)
        {
            bool changed = false;
            var scalarIndex = DL.GetIndexType(gep.PointerOperand.Type.GetScalarType());
            LlvmType current = gep.SourceElementType;
            for (int index = 0; index < gep.IndexCount; ++index)
            {
                LlvmValue operand = gep.GetIndex(index);
                bool structure = index != 0 && current is LlvmStructType;
                LlvmType element = index == 0 ? current : LlvmGetElementPtrInstruction.GetTypeAtIndex(current, operand)!;
                current = element;
                if (structure) continue;
                LlvmType oldType = operand.Type;
                LlvmType newType = oldType is LlvmVectorType vector ? LlvmVectorType.Get(scalarIndex, vector.ElementCount) : scalarIndex;
                if (element.IsSized() && DL.GetTypeAllocSize(element).IsZero && operand is not LlvmConstant { IsNullValue: true })
                {
                    operand = LlvmConstant.GetNullValue(newType);
                    gep.SetOperand(index + 1, operand);
                    changed = true;
                    Count("gep.index.zero_sized");
                }
                if (ReferenceEquals(oldType, newType)) continue;
                LlvmValue cast;
                if (oldType.GetScalarSizeInBits() < newType.GetScalarSizeInBits())
                {
                    bool nonnegative = gep.NoWrapFlags.HasNoUnsignedWrap && gep.NoWrapFlags.HasNoUnsignedSignedWrap;
                    cast = nonnegative ? Builder.CreateZExt(operand, newType) : Builder.CreateSExt(operand, newType);
                    if (nonnegative && cast is LlvmCastInstruction extension) extension.IsNonNegative = true;
                    Count(nonnegative ? "gep.index.zext" : "gep.index.sext");
                }
                else
                {
                    cast = Builder.CreateTrunc(operand, newType);
                    if (cast is LlvmCastInstruction truncation)
                    {
                        truncation.HasNoUnsignedWrap = gep.NoWrapFlags.HasNoUnsignedWrap;
                        truncation.HasNoSignedWrap = gep.NoWrapFlags.HasNoUnsignedSignedWrap;
                    }
                    Count("gep.index.trunc");
                }
                gep.SetOperand(index + 1, cast);
                changed = true;
            }
            return changed;
        }

        private LlvmInstruction? ScalarizeGEPSplats(LlvmGetElementPtrInstruction gep)
        {
            LlvmValue[] operands = gep.Operands.Select(operand => operand!).ToArray();
            for (int index = 0; index < operands.Length; ++index)
                if (operands[index].Type.IsVectorTy() && LlvmValueTracking.GetSplatValue(operands[index], Metrics) is { } scalar)
                    operands[index] = scalar;
            LlvmValue result = Builder.CreateGep(gep.SourceElementType, operands[0], operands.AsSpan(1).ToArray(), gep.Name, gep.NoWrapFlags);
            if (!result.Type.IsVectorTy()) result = Builder.CreateVectorSplat(((LlvmVectorType)gep.Type).ElementCount, result);
            Count("gep.scalarized_splats");
            return ReplaceInstUsesWith(gep, result);
        }

        private bool TryFoldGEPSingleIndex(LlvmGetElementPtrInstruction gep, out LlvmInstruction? result)
        {
            result = null;
            uint addressSpace = ((LlvmPointerType)gep.PointerOperand.Type.GetScalarType()).AddressSpace;
            LlvmValue index = gep.GetIndex(0);
            if (index.Type.GetScalarSizeInBits() != DL.GetIndexSizeInBits(addressSpace)) return false;
            ulong size = DL.GetTypeAllocSize(gep.SourceElementType).GetFixedValue();
            if (size == 1)
            {
                if (!GepMatchPointerDifference(index, gep.PointerOperand, out var target) || !ReferenceEquals(gep.Type, target.Type)) return false;
                bool nonAddressBits = DL.GetAddressSizeInBits(addressSpace) != DL.GetPointerSizeInBits(addressSpace);
                bool changed = false;
                foreach (LlvmUse use in gep.Uses.ToArray())
                {
                    if (use.User is not LlvmInstruction user || user.Opcode != LlvmOpcode.PtrToAddr && user.Opcode != LlvmOpcode.ICmp &&
                        (nonAddressBits || user.Opcode != LlvmOpcode.PtrToInt)) continue;
                    if (use.LogicalType < 0) use.LogicalType = gep.LogicalType;
                    ReplaceUse(use, target);
                    changed = true;
                }
                if (changed) Count("gep.address_only_uses_replaced");
                result = changed ? gep : null;
                return true;
            }
            if (index is not LlvmBinaryOperator { IsExact: true } exact ||
                exact.Opcode is not (LlvmOpcode.LShr or LlvmOpcode.AShr or LlvmOpcode.UDiv or LlvmOpcode.SDiv)) return false;
            bool powerOfTwo = BitOperations.IsPow2(size);
            int scaleShift = BitOperations.TrailingZeroCount(size);
            bool shift = exact.Opcode is LlvmOpcode.LShr or LlvmOpcode.AShr;
            LlvmConstantInt? constant = GepAPInt(exact.Right);
            if (constant is not null && ((powerOfTwo && shift && constant.Value.TryZExtValue() == (ulong)scaleShift) ||
                (!shift && constant.Value.TryZExtValue() == size)))
            {
                Count("gep.exact_scale_cancelled");
                result = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(gep.Context), gep.PointerOperand, [exact.Left], gep.NoWrapFlags);
                return true;
            }
            if (!exact.HasOneUse || constant is null) return false;
            LlvmAPInt? divisor = null;
            if (powerOfTwo && shift && constant.Value.GetLimitedValue() >= (ulong)scaleShift)
                divisor = constant.Value - new LlvmAPInt(constant.BitWidth, (ulong)scaleShift, implicitTrunc: true);
            else if (exact.Opcode == LlvmOpcode.UDiv)
            {
                uint width = Math.Max(64U, constant.BitWidth);
                LlvmAPInt.UDivRem(constant.Value.ZExtOrTrunc(width), new(width, size), out var quotient, out var remainder);
                if (remainder.IsZero) divisor = quotient.Trunc(constant.BitWidth);
            }
            else if (exact.Opcode == LlvmOpcode.SDiv)
            {
                uint width = Math.Max(64U, constant.BitWidth);
                LlvmAPInt.SDivRem(constant.Value.SExtOrTrunc(width), new(width, size, isSigned: true), out var quotient, out var remainder);
                quotient = quotient.Trunc(constant.BitWidth);
                if (!quotient.IsAllOnes && remainder.IsZero) divisor = quotient;
            }
            if (divisor is not { } folded) return false;
            LlvmValue newIndex = Builder.CreateBinary(exact.Opcode, exact.Left, GepConstantInt(exact.Left.Type, folded), exact: true);
            Count("gep.exact_scale_folded");
            result = new LlvmGetElementPtrInstruction(LlvmType.GetInt8Ty(gep.Context), gep.PointerOperand, [newIndex], gep.NoWrapFlags);
            return true;
        }

        private LlvmInstruction? DeriveGEPInBounds(LlvmGetElementPtrInstruction gep, LlvmValue pointer, IReadOnlyList<LlvmValue> indices)
        {
            var offset = LlvmAPInt.GetZero(DL.GetIndexTypeSizeInBits(pointer.Type));
            LlvmValue underlying = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, DL, ref offset,
                allowNonInbounds: false, metrics: Metrics);
            ulong bytes = LlvmValueTracking.GetPointerDereferenceableBytes(underlying, DL, out bool canBeNull, out _, Metrics);
            if (canBeNull || bytes == 0 || !LlvmValueTracking.AccumulateConstantOffset(gep, DL, ref offset, Metrics) || offset.IsNegative) return null;
            if (!offset.Ule(new LlvmAPInt(offset.BitWidth, bytes, implicitTrunc: true))) return null;
            Count("gep.derived_inbounds");
            return new LlvmGetElementPtrInstruction(gep.SourceElementType, pointer, indices, LlvmGepNoWrapFlags.InBounds);
        }

        private LlvmInstruction? SplitGEPAdd(LlvmGetElementPtrInstruction gep)
        {
            LlvmValue index = gep.GetIndex(0);
            if (index.HasOneUse && GepMatchAddLike(index, out var first, out var second, out bool nuw, out _))
            {
                LlvmGepNoWrapFlags flags = gep.NoWrapFlags.HasNoUnsignedWrap && nuw ? gep.NoWrapFlags : LlvmGepNoWrapFlags.None;
                LlvmValue pointer = Builder.CreateGep(gep.SourceElementType, gep.PointerOperand, [first], noWrapFlags: flags);
                Count("gep.add_split");
                return ReplaceInstUsesWith(gep, Builder.CreateGep(gep.SourceElementType, pointer, [second], noWrapFlags: flags));
            }
            if (index is not LlvmCastInstruction { HasOneUse: true } extension ||
                extension.Opcode != LlvmOpcode.SExt && !(extension.Opcode == LlvmOpcode.ZExt && extension.IsNonNegative) ||
                !extension.Operand.HasOneUse || !GepMatchAddLike(extension.Operand, out first, out second, out nuw, out bool nsw) ||
                !nsw || second is not LlvmConstantInt constant) return null;
            bool unsigned = extension.Opcode == LlvmOpcode.ZExt && extension.IsNonNegative && nuw;
            LlvmGepNoWrapFlags preserved = gep.NoWrapFlags.HasNoUnsignedWrap && unsigned ? gep.NoWrapFlags : LlvmGepNoWrapFlags.None;
            LlvmValue basePointer = Builder.CreateGep(gep.SourceElementType, gep.PointerOperand,
                [Builder.CreateSExt(first, index.Type)], noWrapFlags: preserved);
            Count("gep.extended_add_split");
            return ReplaceInstUsesWith(gep, Builder.CreateGep(gep.SourceElementType, basePointer,
                [Builder.CreateSExt(constant, index.Type)], noWrapFlags: preserved));
        }

        private static bool GepMatchPointerDifference(LlvmValue value, LlvmValue pointer, out LlvmValue target)
        {
            LlvmValue? left = null, right = null;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } binary) { left = binary.Left; right = binary.Right; }
            else if (value is LlvmConstantExpr { Opcode: LlvmOpcode.Sub } expression)
            { left = expression.GetOperand(0); right = expression.GetOperand(1); }
            target = left switch
            {
                LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast => cast.Operand,
                LlvmConstantExpr { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast => cast.GetConstantOperand(0),
                _ => null!
            };
            LlvmValue? basePointer = right switch
            {
                LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast => cast.Operand,
                LlvmConstantExpr { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast => cast.GetConstantOperand(0),
                _ => null
            };
            return target is not null && ReferenceEquals(basePointer, pointer);
        }
    }
}
