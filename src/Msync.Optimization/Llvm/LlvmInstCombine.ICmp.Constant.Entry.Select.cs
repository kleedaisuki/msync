// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 4485-4654.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpInstWithConstantNotInt(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.noninteger.queries");
            if (compare.Right is not LlvmConstant rhs || compare.Left is not LlvmInstruction lhs) return null;
            if (lhs is LlvmSelectInstruction select)
            {
                if (!ICmpIsEquality(compare.Predicate) || rhs.Type.GetScalarType() is not LlvmPointerType) return null;
                var bits = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.PtrToAddr, rhs, DL.GetIndexType(rhs.Type), DL, Metrics);
                if (bits is null || !bits.IsNullValue || !select.HasOneUse || !select.Condition.HasOneUse || select.Condition is not LlvmICmpInstruction condition) return null;
                var a = select.TrueValue; var b = select.FalseValue;
                var pred = condition.Predicate;
                if (condition.Left == b && condition.Right == a) pred = LlvmCmpInstruction.GetSwappedPredicate(pred);
                else if (condition.Left != a || condition.Right != b) return null;
                if (pred is not (LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge)) return null;
                var acmp = Builder.CreateICmp(compare.Predicate, a, rhs);
                var bcmp = Builder.CreateICmp(compare.Predicate, b, rhs);
                Count("icmp.constant.noninteger.pointer-max.rewrite");
                return new LlvmBinaryOperator(compare.Predicate == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.And : LlvmOpcode.Or, acmp, bcmp);
            }
            if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.IntToPtr } cast && rhs is LlvmConstantPointerNull)
            {
                var type = DL.GetIntPtrType(rhs.Type);
                if (type == cast.Operand.Type)
                    return IccCompare(compare.Predicate, cast.Operand, LlvmConstantInt.Get(type, DL.GetNullPtrValue(((LlvmPointerType)rhs.Type.GetScalarType()).AddressSpace)));
            }
            if (lhs is LlvmLoadInstruction load && load.PointerOperand is LlvmGetElementPtrInstruction gep)
                return FoldCmpLoadFromIndexedGlobal(load, gep, compare.Predicate, rhs, compare);
            return null;
        }

        private LlvmInstruction? FoldSelectICmp(LlvmCmpPredicate pred, LlvmSelectInstruction select, LlvmValue rhs, LlvmICmpInstruction compare)
        {
            Count("icmp.select.compare.queries");
            LlvmValue? SimplifyArm(LlvmValue arm, bool truth)
            {
                if (LlvmInstructionSimplify.SimplifyICmp(pred, arm, rhs, SQ) is { } result) return result;
                if (LlvmInstructionSimplify.IsImpliedConditionForInstCombine(select.Condition, pred, arm, rhs, new(DL, null, Metrics, null), truth) is { } implied)
                    return LlvmConstantInt.Get(compare.Type, implied ? 1UL : 0UL);
                return null;
            }
            var a = SimplifyArm(select.TrueValue, true); var b = SimplifyArm(select.FalseValue, false);
            LlvmConstantInt? constant = a as LlvmConstantInt;
            if (b is LlvmConstantInt bc) constant = bc;
            bool Simplifies(LlvmValue? folded, LlvmValue arm) => folded is not null ||
                arm is LlvmCallInstruction { HasOneUse: true } call &&
                (call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.SCmp || call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.UCmp) && ICmpAPInt(rhs) is not null;
            bool transform = a is not null && b is not null;
            if (!transform && (Simplifies(a, select.TrueValue) || Simplifies(b, select.FalseValue)))
            {
                if (select.HasOneUse) transform = true;
                else if (constant is { IsZero: false }) transform = ReplacedSelectWithOperand(select, compare, a is not null ? 2 : 1);
            }
            if (transform)
            {
                a ??= Builder.CreateICmp(pred, select.TrueValue, rhs, compare.Name);
                b ??= Builder.CreateICmp(pred, select.FalseValue, rhs, compare.Name);
                Count("icmp.select.compare.arms.rewrite");
                return new LlvmSelectInstruction(select.Condition, a, b);
            }
            if (ICmpIsEquality(pred) && select.HasOneUse && select.Condition is LlvmICmpInstruction condition && condition.Left == rhs &&
                ICmpAPInt(condition.Right) is { } threshold && ICmpAPInt(select.TrueValue) is { } c1 && ICmpAPInt(select.FalseValue) is { } c2)
            {
                var region = LlvmConstantRange.MakeExactICmpRegion(condition.Predicate, threshold.Value);
                if (region.Contains(c1.Value) && !region.Contains(c2.Value))
                {
                    var first = Builder.CreateICmp(pred, rhs, select.TrueValue);
                    var second = Builder.CreateICmp(pred, rhs, select.FalseValue);
                    Count("icmp.select.compare.membership.rewrite");
                    return new LlvmBinaryOperator(pred == LlvmCmpPredicate.ICmpEq ? LlvmOpcode.Or : LlvmOpcode.And, first, second);
                }
            }
            return null;
        }
    }
}
