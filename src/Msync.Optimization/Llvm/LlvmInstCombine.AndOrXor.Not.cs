// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// foldNotXor, sinkNotIntoLogicalOp, foldNot (5054–5093, 5147–5200, 5257–5453).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue? NotOperand(LlvmValue value) => LogicNot(value, out var operand) ? operand : null;
        private static bool NotCommonOperand(LlvmBinaryOperator a, LlvmBinaryOperator b) =>
            ReferenceEquals(a.Left, b.Left) || ReferenceEquals(a.Left, b.Right) || ReferenceEquals(a.Right, b.Left) || ReferenceEquals(a.Right, b.Right);
        private LlvmInstruction? FoldNotXor(LlvmBinaryOperator instruction)
        {
            if (NotOperand(instruction) is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true } xor) return null;
            if (xor.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && xor.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or && NotCommonOperand(and, or))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, and, Builder.CreateNot(or)), "not.and_xor_or");
            if (xor.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.And } rightAnd && xor.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } leftOr && NotCommonOperand(rightAnd, leftOr))
                return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, rightAnd, Builder.CreateNot(leftOr)), "not.or_xor_and");
            return null;
        }
        private bool SinkNotIntoLogicalOp(LlvmInstruction instruction)
        {
            bool isAnd = SharedLogicalMatch(instruction, true, out var left, out var right);
            if (!isAnd && !SharedLogicalMatch(instruction, false, out left, out right)) return false;
            if (ReferenceEquals(left, right) || ReferenceEquals(NotOperand(left), right) || ReferenceEquals(NotOperand(right), left)) return false;
            if (!CanFreelyInvertAllUsersOf(instruction, null) || !CanFreelyInvert(left, instruction) || !CanFreelyInvert(right, instruction)) return false;
            left = FreelyInvert(left, instruction); right = FreelyInvert(right, instruction);
            Builder.SetInsertPoint(instruction.GetInsertionPointAfterDef()!);
            LlvmValue result = instruction is LlvmBinaryOperator
                ? Builder.CreateBinary(isAnd ? LlvmOpcode.Or : LlvmOpcode.And, left, right, instruction.Name + ".not")
                : isAnd ? Builder.CreateLogicalOr(left, right, instruction.Name + ".not") : Builder.CreateLogicalAnd(left, right, instruction.Name + ".not");
            ReplaceInstUsesWith(instruction, result);
            FreelyInvertAllUsersOf(result, null);
            Count("instcombine.logic.not.sink_into_logic");
            return true;
        }
        private LlvmInstruction? FoldNot(LlvmBinaryOperator instruction)
        {
            if (NotOperand(instruction) is not { } operand) return null;
            LlvmType type = instruction.Type;
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and)
            {
                if (NotOperand(and.Left) is { } x) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, x, Builder.CreateNot(and.Right, and.Right.Name + ".not")), "not.nand");
                if (NotOperand(and.Right) is { } y) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Or, y, Builder.CreateNot(and.Left, and.Left.Name + ".not")), "not.nand_commuted");
            }
            if (operand.HasOneUse && SharedLogicalMatch(operand, true, out var andLeft, out var andRight) && NotOperand(andLeft) is { } unnotAnd)
                return LogicSelect(unnotAnd, LlvmConstantInt.GetTrue(type), Builder.CreateNot(andRight, andRight.Name + ".not"), "not.logical_nand");
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or)
            {
                if (NotOperand(or.Left) is { } x) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, x, Builder.CreateNot(or.Right, or.Right.Name + ".not")), "not.nor");
                if (NotOperand(or.Right) is { } y) return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, y, Builder.CreateNot(or.Left, or.Left.Name + ".not")), "not.nor_commuted");
            }
            if (operand.HasOneUse && SharedLogicalMatch(operand, false, out var orLeft, out var orRight) && NotOperand(orLeft) is { } unnotOr)
                return LogicSelect(unnotOr, Builder.CreateNot(orRight, orRight.Name + ".not"), LlvmConstantInt.GetFalse(type), "not.logical_nor");
            if (operand is LlvmBinaryOperator binary)
            {
                if (binary.Opcode == LlvmOpcode.Or && binary.HasOneUse)
                {
                    LlvmValue? negated = null, other = null;
                    if (binary.Left.HasOneUse && LogicNeg(binary.Left, out var l)) { negated = l; other = binary.Right; }
                    else if (binary.Right.HasOneUse && LogicNeg(binary.Right, out var r)) { negated = r; other = binary.Left; }
                    if (negated is not null)
                        return LogicResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateAdd(negated, LlvmConstant.GetAllOnesValue(type)), Builder.CreateNot(other!)), "not.neg_or");
                }
                if (binary.Opcode == LlvmOpcode.AShr && NotOperand(binary.Left) is { } ashrInput)
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.AShr, ashrInput, binary.Right), "not.ashr_not");
                if (binary.Opcode == LlvmOpcode.LShr && NotOperand(binary.Left) is { } lshrInput && IsKnownNegative(lshrInput, binary))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.AShr, lshrInput, binary.Right), "not.lshr_negative_not");
                if (binary is { Opcode: LlvmOpcode.AShr, HasOneUse: true } && FunnelInt(binary.Right, type.GetScalarSizeInBits() - 1))
                {
                    var nonnegative = Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, binary.Left, LlvmConstant.GetAllOnesValue(type), "isnotneg");
                    return LogicResult(new LlvmCastInstruction(LlvmOpcode.SExt, nonnegative, type), "not.sign_test");
                }
                if (binary.Left is LlvmConstant constant && binary.Opcode == LlvmOpcode.AShr && NotConstantSign(constant, negative: true))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.LShr, LlvmConstantExpr.GetNot(constant), binary.Right), "not.constant_ashr");
                if (binary.Left is LlvmConstant nonnegativeConstant && binary.Opcode == LlvmOpcode.LShr && NotConstantSign(nonnegativeConstant, negative: false))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.AShr, LlvmConstantExpr.GetNot(nonnegativeConstant), binary.Right), "not.constant_lshr");
                if (binary.Opcode == LlvmOpcode.Add && binary.Right is LlvmConstant addend && (addend is not LlvmConstantExpr || addend.Type.IsVectorTy() && addend.GetSplatValue(true) is { } immediateSplat && immediateSplat is not LlvmConstantExpr))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantExpr.GetNot(addend), binary.Left), "not.add_constant");
                if (binary.Opcode == LlvmOpcode.Sub && (binary.Left is LlvmConstant || binary.HasOneUse))
                    return LogicResult(new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateNot(binary.Left), binary.Right), "not.sub");
                if (binary.Opcode == LlvmOpcode.Add)
                {
                    LlvmValue? a = NotOperand(binary.Left), b = binary.Right;
                    if (a is null) { a = NotOperand(binary.Right); b = binary.Left; }
                    if (a is not null)
                    {
                        var subtract = new LlvmBinaryOperator(LlvmOpcode.Sub, a, b); subtract.CopyIRFlags(binary);
                        return LogicResult(subtract, "not.add_not");
                    }
                }
            }
            if (operand is LlvmCmpInstruction compare && (operand.HasOneUse || CanFreelyInvertAllUsersOf(compare, null)))
            {
                compare.Predicate = LlvmCmpInstruction.GetInversePredicate(compare.Predicate);
                FreelyInvertAllUsersOf(compare, null); Count("instcombine.logic.not.compare"); return instruction;
            }
            if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true, Operand: LlvmCmpInstruction { HasOneUse: true } castCompare })
            {
                castCompare.Predicate = LlvmCmpInstruction.GetInversePredicate(castCompare.Predicate);
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.BitCast, castCompare, type), "not.bitcast_compare");
            }
            if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true, Operand: LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } extension } && extension.Operand.Type.IsIntOrIntVectorTy(1))
            {
                var inverted = Builder.CreateNot(extension.Operand);
                return LogicResult(new LlvmCastInstruction(LlvmOpcode.BitCast, Builder.CreateCast(LlvmOpcode.SExt, inverted, extension.Type), type), "not.bitcast_sext_bool");
            }
            if (operand is LlvmInstruction logical && SinkNotIntoLogicalOp(logical)) return instruction;
            if (operand is LlvmCallInstruction { HasOneUse: true, CalledFunction.IntrinsicDescriptor: { } descriptor } call)
            {
                if (descriptor.Kind == LlvmIntrinsicKind.SMin || descriptor.Kind == LlvmIntrinsicKind.SMax || descriptor.Kind == LlvmIntrinsicKind.UMin || descriptor.Kind == LlvmIntrinsicKind.UMax)
                {
                    LlvmValue? a = NotOperand(call.GetArgument(0)), b = call.GetArgument(1);
                    if (a is null) { a = NotOperand(call.GetArgument(1)); b = call.GetArgument(0); }
                    if (a is not null)
                    {
                        var inverse = descriptor.Kind == LlvmIntrinsicKind.SMin ? LlvmIntrinsicKind.SMax :
                            descriptor.Kind == LlvmIntrinsicKind.SMax ? LlvmIntrinsicKind.SMin :
                            descriptor.Kind == LlvmIntrinsicKind.UMin ? LlvmIntrinsicKind.UMax : LlvmIntrinsicKind.UMin;
                        return LogicReplace(instruction, Builder.CreateIntrinsic(inverse, [a, Builder.CreateNot(b)]), "not.minmax");
                    }
                }
                if (descriptor.Kind == LlvmIntrinsicKind.IsFPClass)
                {
                    var mask = (LlvmConstantInt)call.GetArgument(1);
                    call.SetArgument(1, LlvmConstantInt.Get(mask.Type, ~mask.GetZExtValue() & (uint)LlvmFPClassTest.All));
                    return LogicReplace(instruction, call, "not.fpclass");
                }
            }
            if (operand is LlvmSelectInstruction { HasOneUse: true } select)
            {
                var t = select.TrueValue; var f = select.FalseValue;
                if ((t is LlvmCmpInstruction { HasOneUse: true } || t is LlvmConstant) && (f is LlvmCmpInstruction { HasOneUse: true } || f is LlvmConstant))
                {
                    if (t is LlvmCmpInstruction tc) tc.Predicate = LlvmCmpInstruction.GetInversePredicate(tc.Predicate);
                    else select.TrueValue = LlvmConstantExpr.GetNot((LlvmConstant)t);
                    if (f is LlvmCmpInstruction fc) fc.Predicate = LlvmCmpInstruction.GetInversePredicate(fc.Predicate);
                    else select.FalseValue = LlvmConstantExpr.GetNot((LlvmConstant)f);
                    return LogicReplace(instruction, select, "not.select_compares");
                }
            }
            if (FoldNotXor(instruction) is { } xor) return xor;
            return GetFreelyInverted(operand, operand.HasOneUse, Builder) is { } freelyInverted
                ? LogicReplace(instruction, freelyInverted, "not.freely_inverted") : null;
        }
        private static bool NotConstantSign(LlvmConstant constant, bool negative)
        {
            if (constant is LlvmConstantInt integer) return integer.Value.IsNegative == negative;
            if (constant.Type.IsVectorTy() && constant.GetSplatValue() is LlvmConstantInt splat) return splat.Value.IsNegative == negative;
            if (constant.Type is not LlvmFixedVectorType type) return false;
            bool any = false;
            for (uint lane = 0; lane < type.NumElements; ++lane)
            {
                var element = constant.GetAggregateElement(lane);
                if (element is LlvmPoisonValue) continue;
                if (element is not LlvmConstantInt value || value.Value.IsNegative != negative) return false;
                any = true;
            }
            return any;
        }
    }
}
