// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp shouldMergeGEPs, foldSelectGEP,
// canonicalizeGEPOfConstGEPI8, combineConstantOffsets, foldGEPOfPhi.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool GepShouldMerge(LlvmGepOperator gep, LlvmGepOperator source) =>
            !(gep.HasAllZeroIndices && !source.HasAllZeroIndices && !source.HasOneUse);

        private LlvmInstruction? FoldSelectGEP(LlvmGetElementPtrInstruction gep)
        {
            if (!gep.HasAllConstantIndices || gep.PointerOperand is not LlvmSelectInstruction { HasOneUse: true } select ||
                select.TrueValue is not LlvmConstant whenTrue || select.FalseValue is not LlvmConstant whenFalse) return null;
            LlvmValue[] indices = gep.Indices.ToArray();
            LlvmValue newTrue = Builder.CreateGep(gep.SourceElementType, whenTrue, indices, noWrapFlags: gep.NoWrapFlags);
            LlvmValue newFalse = Builder.CreateGep(gep.SourceElementType, whenFalse, indices, noWrapFlags: gep.NoWrapFlags);
            Count("gep.select_threaded");
            return new LlvmSelectInstruction(select.Condition, newTrue, newFalse) { Source = select.Source, LogicalType = gep.LogicalType };
        }

        private LlvmInstruction? CanonicalizeGEPOfConstGEPI8(LlvmGetElementPtrInstruction gep, LlvmGepOperator source)
        {
            if (gep.IndexCount != 1 || !source.SourceElementType.IsIntegerTy(8) ||
                !source.GetIndex(0).Type.IsIntOrIntVectorTy(DL.GetIndexTypeSizeInBits(source.Type)) ||
                GepAPInt(source.GetIndex(0)) is not { } first) return null;
            if (!GepMatchAddLike(gep.GetIndex(0), out var variable, out var secondOperand, out bool nuw, out _) ||
                GepAPInt(secondOperand) is not { } second) return null;
            uint width = DL.GetIndexTypeSizeInBits(source.Type.GetScalarType());
            if (second.BitWidth != width || gep.SourceElementType is LlvmScalableVectorType) return null;
            LlvmAPInt size = new(width, DL.GetTypeAllocSize(gep.SourceElementType).GetFixedValue(), implicitTrunc: true);
            LlvmAPInt offset = size * second.Value + first.Value;
            if (!offset.IsZero && !(source.HasOneUse && gep.GetIndex(0).HasOneUse)) return null;
            LlvmGepNoWrapFlags flags = LlvmGepNoWrapFlags.None;
            if (gep.NoWrapFlags.HasNoUnsignedWrap && source.HasNoUnsignedWrap && nuw)
            {
                flags |= LlvmGepNoWrapFlags.NoUnsignedWrap;
                if (gep.NoWrapFlags.IsInBounds && source.IsInBounds) flags |= LlvmGepNoWrapFlags.InBounds;
            }
            LlvmValue newBase = Builder.CreatePtrAdd(source.PointerOperand, LlvmConstantInt.Get(gep.Context, offset), noWrapFlags: flags);
            Count("gep.constant_base_canonicalized");
            return new LlvmGetElementPtrInstruction(gep.SourceElementType, newBase, [variable], flags);
        }

        private LlvmInstruction? CombineGEPConstantOffsets(LlvmGetElementPtrInstruction gep)
        {
            if (!gep.HasAllConstantIndices) return null;
            LlvmGepNoWrapFlags flags = LlvmGepNoWrapFlags.All;
            List<LlvmGetElementPtrInstruction> skipped = [];
            var inner = gep.PointerOperand as LlvmGetElementPtrInstruction;
            while (true)
            {
                if (inner is null) return null;
                flags = flags.IntersectForReassociate(inner.NoWrapFlags);
                if (inner.HasAllConstantIndices) break;
                if (!inner.HasOneUse) return null;
                skipped.Add(inner);
                inner = inner.PointerOperand as LlvmGetElementPtrInstruction;
            }
            if (skipped.Count == 0 || !inner.HasOneUse || !ReferenceEquals(inner.Type, gep.Type)) return null;
            var offset = LlvmAPInt.GetZero(DL.GetIndexTypeSizeInBits(gep.Type));
            if (!LlvmValueTracking.AccumulateConstantOffset(gep, DL, ref offset, Metrics) ||
                !LlvmValueTracking.AccumulateConstantOffset(inner, DL, ref offset, Metrics)) return null;
            ReplaceOperand(skipped[^1], 0, inner.PointerOperand);
            foreach (LlvmGetElementPtrInstruction entry in skipped) entry.NoWrapFlags = flags;
            Count("gep.separated_constants_combined");
            return ReplaceInstUsesWith(gep, Builder.CreatePtrAdd(skipped[0], LlvmConstantInt.Get(gep.Context, offset),
                noWrapFlags: flags.IntersectForOffsetAdd(gep.NoWrapFlags)));
        }

        private static bool ShouldCanonicalizeGEPToPtrAdd(LlvmGetElementPtrInstruction gep)
        {
            if (gep.SourceElementType.IsIntegerTy(8)) return false;
            if (gep.SourceElementType.IsScalableTy()) return true;
            if (gep.IndexCount == 1 && gep.GetIndex(0) is LlvmBinaryOperator { HasOneUse: true } binaryIndex &&
                binaryIndex.Opcode is LlvmOpcode.Mul or LlvmOpcode.Shl && binaryIndex.Right is LlvmConstantInt) return true;
            return LlvmGepOperator.TryGet(gep.PointerOperand, out var pointer) && pointer.HasAllConstantIndices &&
                gep.Indices.Any(index => GepAPInt(index) is { IsZero: false });
        }

        private LlvmInstruction? FoldGEPOfPhi(LlvmGetElementPtrInstruction gep, LlvmPhiInstruction phi)
        {
            if (phi.GetIncomingValue(0) is not LlvmGetElementPtrInstruction first || ReferenceEquals(first, gep)) return null;
            LlvmGepNoWrapFlags flags = first.NoWrapFlags;
            int difference = -1;
            for (int index = 1; index < phi.IncomingCount; ++index)
            {
                if (phi.GetIncomingValue(index) is not LlvmGetElementPtrInstruction next || ReferenceEquals(next, gep) ||
                    first.OperandCount != next.OperandCount || !ReferenceEquals(first.SourceElementType, next.SourceElementType)) return null;
                LlvmType? current = null;
                for (int operand = 0; operand < first.OperandCount; ++operand)
                {
                    LlvmValue left = first.GetOperand(operand)!, right = next.GetOperand(operand)!;
                    if (!ReferenceEquals(left.Type, right.Type)) return null;
                    if (!ReferenceEquals(left, right))
                    {
                        if (difference != -1) return null;
                        if (operand > 1)
                        {
                            Debug.Assert(current is not null);
                            if (current is LlvmStructType) return null;
                        }
                        difference = operand;
                    }
                    if (operand > 0)
                        current = operand == 1 ? first.SourceElementType : LlvmGetElementPtrInstruction.GetTypeAtIndex(current!, left);
                }
                flags &= next.NoWrapFlags;
            }
            if (difference != -1 && !phi.HasOneUse) return null;
            var clone = (LlvmGetElementPtrInstruction)first.Clone();
            clone.NoWrapFlags = flags;
            if (difference != -1)
            {
                LlvmPhiInstruction replacement;
                using (new LlvmTransformBuilder.InsertPointGuard(Builder))
                {
                    Builder.SetInsertPoint(phi);
                    replacement = Builder.CreatePhi(first.GetOperand(difference)!.Type, phi.IncomingCount);
                    replacement.LogicalType = first.GetOperandUse(difference).LogicalType >= 0
                        ? first.GetOperandUse(difference).LogicalType : first.GetOperand(difference)!.LogicalType;
                }
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    var incoming = (LlvmGetElementPtrInstruction)phi.GetIncomingValue(index);
                    replacement.AddIncoming(incoming.GetOperand(difference)!, phi.GetIncomingBlock(index));
                    replacement.GetOperandUse(index).LogicalType = incoming.GetOperandUse(difference).LogicalType;
                }
                clone.SetOperand(difference, replacement);
            }
            LlvmInstruction insertion = gep.Parent!.FirstInstruction!;
            while (insertion is LlvmPhiInstruction) insertion = insertion.Next!;
            if (insertion.IsEHPad) insertion = insertion.Next!;
            gep.Parent.InsertBefore(clone, insertion);
            Count("gep.phi_folded");
            return clone;
        }

        private static LlvmConstantInt? GepAPInt(LlvmValue value) => value is LlvmConstantInt integer ? integer
            : value is LlvmConstant constant && value.Type.IsVectorTy() ? constant.GetSplatValue() as LlvmConstantInt : null;

        private static bool GepMatchAddLike(LlvmValue value, out LlvmValue left, out LlvmValue right, out bool nuw, out bool nsw)
        {
            if (value is LlvmBinaryOperator operation && (operation.Opcode == LlvmOpcode.Add ||
                operation is { Opcode: LlvmOpcode.Or, IsDisjoint: true }))
            {
                left = operation.Left; right = operation.Right;
                nuw = operation.Opcode == LlvmOpcode.Or || operation.HasNoUnsignedWrap;
                nsw = operation.Opcode == LlvmOpcode.Or || operation.HasNoSignedWrap;
                return true;
            }
            if (value is LlvmConstantExpr { Opcode: LlvmOpcode.Add } expression)
            {
                left = expression.GetOperand(0)!; right = expression.GetOperand(1)!;
                nuw = (expression.Flags & 1U) != 0; nsw = (expression.Flags & 2U) != 0;
                return true;
            }
            left = right = null!; nuw = nsw = false;
            return false;
        }
    }
}
