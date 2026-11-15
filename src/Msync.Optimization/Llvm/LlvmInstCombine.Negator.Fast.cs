// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineNegator.cpp
// visitImpl non-recursive represented scalar cases.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private sealed partial class Negator
        {
            private LlvmValue? Visit(LlvmValue value, bool isNSW, uint depth)
            {
                if (value is LlvmUndefValue) return value;
                if (value.Type.IsIntOrIntVectorTy(1)) return value;
                if (MatchNegation(value, out LlvmValue unnegated)) return unnegated;
                if (value is LlvmConstantInt integer) return NegateConstant(integer);
                if (value is not LlvmInstruction instruction) return null;
                if (!value.HasOneUse && !trulyNegating) return null;
                if (value.Type.IsVectorTy()) throw new NotSupportedException("Negator vector instruction recipes are not represented.");
                var saved = builder.SaveInsertionPoint();
                var savedSource = builder.CurrentSource;
                builder.SetInsertPoint(instruction);
                try { return VisitInstruction(instruction, isNSW, depth); }
                finally { builder.RestoreInsertionPoint(saved); builder.CurrentSource = savedSource; }
            }
            private LlvmValue? VisitInstruction(LlvmInstruction instruction, bool isNSW, uint depth)
            {
                uint width = instruction.Type.GetScalarSizeInBits();
                string name = instruction.Name + ".neg";
                switch (instruction)
                {
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add:
                        var sorted = SortedOperands(add);
                        if (sorted.Right is LlvmConstantInt { IsOne: true }) return builder.CreateNot(sorted.Left, name);
                        break;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.Xor }:
                        if (MatchNot(instruction, out LlvmValue complemented)) return builder.CreateAdd(complemented, One(complemented.Type), name);
                        break;
                    case LlvmBinaryOperator { Opcode: LlvmOpcode.AShr or LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift:
                        if (amount.Value == new LlvmAPInt(width, width - 1))
                        {
                            LlvmValue result = Shift(shift.Opcode == LlvmOpcode.AShr ? LlvmOpcode.LShr : LlvmOpcode.AShr, shift.Left, shift.Right);
                            if (result is LlvmInstruction replacement) { replacement.CopyIRFlags(instruction); replacement.SetName(name); }
                            return result;
                        }
                        break;
                    case LlvmCastInstruction { Opcode: LlvmOpcode.SExt or LlvmOpcode.ZExt } cast:
                        if (cast.Operand.Type.IsIntOrIntVectorTy(1))
                            return builder.CreateCast(cast.Opcode == LlvmOpcode.SExt ? LlvmOpcode.ZExt : LlvmOpcode.SExt, cast.Operand, cast.Type, name);
                        break;
                    case LlvmSelectInstruction select:
                        if (select.TrueValue is LlvmConstant trueConstant && IsAddSubImmediateConstant(trueConstant) &&
                            select.FalseValue is LlvmConstant falseConstant && IsAddSubImmediateConstant(falseConstant))
                            return builder.CreateSelect(select.Condition, NegateConstant(trueConstant), NegateConstant(falseConstant), name);
                        break;
                    case LlvmCallInstruction call when call.HasOneUse:
                        var kind = call.CalledFunction?.IntrinsicDescriptor?.Kind;
                        if (kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp)
                            return builder.CreateIntrinsic(kind.Value, [call.GetArgument(1), call.GetArgument(0)], (LlvmIntegerType)call.Type);
                        break;
                }
                if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } subtract &&
                    (subtract.HasOneUse || IsAddSubImmediateConstant(subtract.Left)))
                    return builder.CreateSub(subtract.Right, subtract.Left, name, nsw: isNSW && subtract.HasNoSignedWrap);
                if (!instruction.HasOneUse) return null;
                if (instruction is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extended)
                {
                    LlvmValue source = extended.Operand;
                    uint sourceWidth = source.Type.GetScalarSizeInBits();
                    if (trulyNegating && source is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt bits } logical &&
                        bits.Value == new LlvmAPInt(sourceWidth, sourceWidth - 1))
                        return builder.CreateSExt(Shift(LlvmOpcode.AShr, logical.Left, logical.Right), instruction.Type);
                }
                if (trulyNegating && instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt { IsOne: true } } and &&
                    and.Left.HasOneUse)
                {
                    LlvmValue source = and.Left is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncate ? truncate.Operand : and.Left;
                    if (source is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstant shiftAmount } logical)
                    {
                        uint sourceWidth = logical.Left.Type.GetScalarSizeInBits();
                        var last = LlvmConstantInt.Get(logical.Left.Context, new LlvmAPInt(sourceWidth, sourceWidth - 1));
                        LlvmValue result = Shift(LlvmOpcode.Shl, logical.Left, builder.CreateSub(last, shiftAmount));
                        result = Shift(LlvmOpcode.AShr, result, last);
                        return TruncateOrBitcast(result, instruction.Type);
                    }
                }
                if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv, Right: LlvmConstantInt denominator } division &&
                    !denominator.Value.IsMinSignedValue && !denominator.IsOne)
                {
                    LlvmValue result = builder.CreateBinary(LlvmOpcode.SDiv, division.Left, NegateConstant(denominator), name);
                    if (result is LlvmBinaryOperator replacement) replacement.IsExact = division.IsExact;
                    return result;
                }
                if (depth > maxDepth) { owner.Count("negator.depth_limit"); return null; }
                return VisitRecursive(instruction, isNSW, depth);
            }
        }
    }
}
