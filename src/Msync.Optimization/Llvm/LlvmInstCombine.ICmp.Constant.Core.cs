// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 950-1483.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmICmpInstruction IccCompare(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right,
            [CallerMemberName] string owner = "")
        {
            if (Metrics is not null) Count("icmp.constant.rewrite." + owner);
            return new LlvmICmpInstruction(predicate, left, right);
        }

        private static LlvmAPInt IccInt(LlvmAPInt value, ulong integer) => new(value.BitWidth, integer, implicitTrunc: true);
        private static LlvmConstantInt IccConstant(LlvmValue value, LlvmAPInt integer) => LlvmConstantInt.Get(value.Context, integer);
        private static LlvmConstantInt IccConstant(LlvmValue value, ulong integer) => LlvmConstantInt.Get((LlvmIntegerType)value.Type, integer);
        private static bool IccIsShift(LlvmOpcode opcode) => opcode is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr;
        private static uint IccLog2(LlvmAPInt value) => value.GetActiveBits() - 1;
        private static LlvmCmpPredicate IccFlipSignedness(LlvmCmpPredicate predicate) => ICmpIsSigned(predicate)
            ? ICmpUnsignedPredicate(predicate) : ICmpSignedPredicate(predicate);

        private LlvmInstruction? FoldICmpAddOpConst(LlvmValue value, LlvmAPInt constant, LlvmCmpPredicate predicate)
        {
            Debug.Assert(!constant.IsZero);
            Count("icmp.constant.add-op");
            if (predicate is LlvmCmpPredicate.ICmpUlt or LlvmCmpPredicate.ICmpUle)
                return IccCompare(LlvmCmpPredicate.ICmpUgt, value, IccConstant(value, LlvmAPInt.GetMaxValue(constant.BitWidth) - constant));
            if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge)
                return IccCompare(LlvmCmpPredicate.ICmpUlt, value, IccConstant(value, -constant));
            LlvmAPInt maximum = LlvmAPInt.GetSignedMaxValue(constant.BitWidth);
            return predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle
                ? IccCompare(LlvmCmpPredicate.ICmpSgt, value, IccConstant(value, maximum - constant))
                : IccCompare(LlvmCmpPredicate.ICmpSlt, value, IccConstant(value, maximum - (constant - IccInt(constant, 1))));
        }

        private LlvmInstruction? FoldICmpShrConstConst(LlvmICmpInstruction compare, LlvmValue amount, LlvmAPInt expected, LlvmAPInt input)
        {
            Count("icmp.constant.FoldICmpShrConstConst.queries");
            Debug.Assert(ICmpIsEquality(compare.Predicate));
            if (input.IsZero) return null;
            bool arithmetic = compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr };
            if (arithmetic && (input.IsAllOnes || input.IsNegative != expected.IsNegative || input.Sgt(expected))) return null;
            if (expected.IsZero) return Make(LlvmCmpPredicate.ICmpUgt, IccLog2(input));
            if (expected == input) return Make(LlvmCmpPredicate.ICmpEq, 0);
            int shift = (int)(arithmetic && expected.IsNegative ? expected.CountLeadingOnes() : expected.CountLeadingZeros())
                - (int)(arithmetic && expected.IsNegative ? input.CountLeadingOnes() : input.CountLeadingZeros());
            if (shift > 0)
            {
                if (arithmetic && expected == input.AShr((uint)shift))
                    return Make(expected.IsAllOnes && !input.IsPowerOf2 ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpEq, (ulong)shift);
                if (expected == input.LShr((uint)shift)) return Make(LlvmCmpPredicate.ICmpEq, (ulong)shift);
            }
            return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context, compare.Predicate == LlvmCmpPredicate.ICmpNe));
            LlvmInstruction Make(LlvmCmpPredicate predicate, ulong value) => IccCompare(
                compare.Predicate == LlvmCmpPredicate.ICmpNe ? LlvmCmpInstruction.GetInversePredicate(predicate) : predicate,
                amount, IccConstant(amount, value));
        }

        private LlvmInstruction? FoldICmpShlConstConst(LlvmICmpInstruction compare, LlvmValue amount, LlvmAPInt expected, LlvmAPInt input)
        {
            Count("icmp.constant.FoldICmpShlConstConst.queries");
            Debug.Assert(ICmpIsEquality(compare.Predicate));
            if (input.IsZero) return null;
            uint trailing = input.CountTrailingZeros();
            if (expected.IsZero && trailing != 0) return Make(LlvmCmpPredicate.ICmpUge, input.BitWidth - trailing);
            if (expected == input) return Make(LlvmCmpPredicate.ICmpEq, 0);
            int shift = (int)expected.CountTrailingZeros() - (int)trailing;
            if (shift > 0 && input.Shl((uint)shift) == expected) return Make(LlvmCmpPredicate.ICmpEq, (ulong)shift);
            return ReplaceInstUsesWith(compare, LlvmConstantInt.GetBool(compare.Context, compare.Predicate == LlvmCmpPredicate.ICmpNe));
            LlvmInstruction Make(LlvmCmpPredicate predicate, ulong value) => IccCompare(
                compare.Predicate == LlvmCmpPredicate.ICmpNe ? LlvmCmpInstruction.GetInversePredicate(predicate) : predicate,
                amount, IccConstant(amount, value));
        }

        private LlvmInstruction? ProcessUgtAddCstAdd(LlvmICmpInstruction compare, LlvmValue left, LlvmValue right,
            LlvmConstantInt bias, LlvmConstantInt bound)
        {
            Count("icmp.constant.ProcessUgtAddCstAdd.queries");
            var outer = (LlvmBinaryOperator)compare.Left;
            if (!outer.HasOneUse || !bias.Value.IsPowerOf2) return null;
            uint width = bias.Value.CountTrailingZeros();
            if (width is not (7 or 15 or 31)) return null;
            ++width;
            if (bound.Value.BitWidth == width || bound.Value != LlvmAPInt.GetLowBitsSet(bound.Value.BitWidth, width)) return null;
            if (ComputeMaxSignificantBits(left, compare) > width || ComputeMaxSignificantBits(right, compare) > width) return null;
            var original = (LlvmBinaryOperator)outer.Left;
            foreach (LlvmUser user in original.Users)
                if (user != outer && (user is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc || trunc.Type.GetScalarSizeInBits() > width)) return null;
            Count("icmp.constant.sadd-overflow.rewrite");
            var type = LlvmIntegerType.Get(compare.Context, width);
            Builder.SetInsertPoint(original);
            LlvmValue lhs = Builder.CreateTrunc(left, type, left.Name + ".trunc");
            LlvmValue rhs = Builder.CreateTrunc(right, type, right.Name + ".trunc");
            LlvmValue call = Builder.CreateIntrinsic(LlvmIntrinsicKind.SAddWithOverflow, [lhs, rhs], "sadd");
            LlvmValue result = Builder.CreateExtractValue(call, 0, "sadd.result");
            LlvmValue extended = Builder.CreateZExt(result, original.Type);
            ReplaceInstUsesWith(original, extended);
            EraseInstFromFunction(original);
            return new LlvmExtractValueInstruction(call, [1]);
        }

        private LlvmInstruction? FoldIRemByPowerOfTwoToBitTest(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldIRemByPowerOfTwoToBitTest.queries");
            if (!ICmpIsEquality(compare.Predicate) || compare.Right is not LlvmConstantInt { IsZero: true }
                || compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.URem or LlvmOpcode.SRem, HasOneUse: true } remainder
                || !IsKnownToBeAPowerOfTwo(remainder.Right, true, compare)) return null;
            var mask = Builder.CreateAdd(remainder.Right, ICmpOnes(remainder.Right));
            return IccCompare(compare.Predicate, Builder.CreateAnd(remainder.Left, mask), compare.Right);
        }

        private LlvmInstruction? FoldSignBitTest(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldSignBitTest.queries");
            if (!ICmpIsEquality(compare.Predicate) || compare.Right is not LlvmConstantInt { IsZero: true }
                || compare.Left is not LlvmInstruction value) return null;
            LlvmValue? input = null;
            LlvmValue shifted = value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc ? trunc.Operand : value;
            if (shifted is LlvmBinaryOperator shift && IccIsShift(shift.Opcode) && shift.Opcode != LlvmOpcode.Shl && shift.Right is LlvmConstantInt amount)
            {
                uint width = shift.Left.Type.GetScalarSizeInBits();
                if (amount.Value == new LlvmAPInt(width, width - 1)) input = shift.Left;
            }
            else if (value is LlvmBinaryOperator binary)
                input = ReassociateShiftAmtsOfTwoSameDirectionShifts(binary, Query(value), analyzeForSignBitExtraction: true);
            return input is null ? null : IccCompare(compare.Predicate == LlvmCmpPredicate.ICmpEq
                ? LlvmCmpPredicate.ICmpSge : LlvmCmpPredicate.ICmpSlt, input, ICmpZero(input));
        }

        private LlvmInstruction? FoldICmpWithZero(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldICmpWithZero.queries");
            if (compare.Right is not LlvmConstantInt { IsZero: true }) return null;
            var predicate = compare.Predicate;
            if (predicate == LlvmCmpPredicate.ICmpSgt && compare.Left is LlvmCallBase { CalledFunction.IntrinsicDescriptor.Kind: var kind } call
                && kind == LlvmIntrinsicKind.SMin)
            {
                if (IsKnownPositive(call.GetArgument(0), compare)) return IccCompare(predicate, call.GetArgument(1), compare.Right);
                if (IsKnownPositive(call.GetArgument(1), compare)) return IccCompare(predicate, call.GetArgument(0), compare.Right);
            }
            if (FoldIRemByPowerOfTwoToBitTest(compare) is { } folded) return folded;
            if (!ICmpIsEquality(predicate)) return null;
            if (compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.URem } remainder)
            {
                var left = ComputeKnownBits(remainder.Left, compare); var right = ComputeKnownBits(remainder.Right, compare);
                if ((~left.Zero).PopCount() == 1 && right.One.PopCount() >= 2) return IccCompare(predicate, remainder.Left, compare.Right);
            }
            if (compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } product)
            {
                var left = ComputeKnownBits(product.Left, compare);
                if (left.CountMaxTrailingZeros() == 0) return IccCompare(predicate, product.Right, compare.Right);
                var right = ComputeKnownBits(product.Right, compare);
                if (right.CountMaxTrailingZeros() == 0) return IccCompare(predicate, product.Left, compare.Right);
                if (product.HasNoUnsignedWrap || product.HasNoSignedWrap)
                {
                    if (!left.One.IsZero || IsKnownNonZero(product.Left, compare)) return IccCompare(predicate, product.Right, compare.Right);
                    if (!right.One.IsZero || IsKnownNonZero(product.Right, compare)) return IccCompare(predicate, product.Left, compare.Right);
                }
            }
            return LlvmValueTracking.StripNullTest(compare.Left, Metrics) is { } stripped ? IccCompare(predicate, stripped, ICmpZero(stripped)) : null;
        }

        private LlvmInstruction? FoldIsMultipleOfAPowerOfTwo(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldIsMultipleOfAPowerOfTwo.queries");
            if (!ICmpIsEquality(compare.Predicate)) return null;
            for (int side = 0; side < 2; ++side)
            {
                LlvmValue number = side != 0 ? compare.Right : compare.Left;
                LlvmValue candidate = side != 0 ? compare.Left : compare.Right;
                if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } mask) continue;
                for (int maskSide = 0; maskSide < 2; ++maskSide)
                {
                    LlvmValue summed = maskSide != 0 ? mask.Right : mask.Left;
                    LlvmValue negated = maskSide != 0 ? mask.Left : mask.Right;
                    if (negated is not LlvmConstantInt negative || summed is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true } add) continue;
                    LlvmConstantInt? low = ReferenceEquals(add.Left, number) ? add.Right as LlvmConstantInt : ReferenceEquals(add.Right, number) ? add.Left as LlvmConstantInt : null;
                    if (low is null || !(low.Value & (low.Value + IccInt(low.Value, 1))).IsZero || low.Value.IsZero || negative.Value != ~low.Value) continue;
                    return IccCompare(compare.Predicate, Builder.CreateAnd(number, low), ICmpZero(number));
                }
            }
            return null;
        }

        private LlvmInstruction? FoldICmpWithConstant(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldICmpWithConstant.queries");
            if (compare.Predicate == LlvmCmpPredicate.ICmpUgt && compare.Right is LlvmConstantInt bound
                && compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add, Right: LlvmConstantInt bias }
                && ProcessUgtAddCstAdd(compare, add.Left, add.Right, bias, bound) is { } overflow) return overflow;
            if (compare.Right is not LlvmConstant constant) return null;
            if (compare.Left is LlvmPhiInstruction phi && phi.Operands.All(value => value is LlvmConstant))
            {
                List<LlvmConstant> values = [];
                foreach (LlvmValue? value in phi.Operands)
                {
                    if (LlvmConstantFolding.ConstantFoldCompareInstOperands(compare.Predicate, (LlvmConstant)value!, constant, DL, metrics: Metrics) is not { } result) return null;
                    values.Add(result);
                }
                Count("icmp.constant.constant-phi.rewrite");
                Count("icmp.constant.constant-phi.incoming", (ulong)values.Count);
                Builder.SetInsertPoint(phi);
                var replacement = Builder.CreatePhi(compare.Type, phi.IncomingCount);
                for (int index = 0; index < values.Count; ++index) replacement.AddIncoming(values[index], phi.GetIncomingBlock(index));
                return ReplaceInstUsesWith(compare, replacement);
            }
            return TryFoldInstWithCtpopWithNot(compare);
        }

        private LlvmInstruction? FoldICmpWithDominatingICmp(LlvmICmpInstruction compare)
        {
            Count("icmp.constant.FoldICmpWithDominatingICmp.queries");
            if (compare.Right is not LlvmConstantInt constant) return null;
            LlvmValue input = compare.Left;
            var region = LlvmConstantRange.MakeExactICmpRegion(compare.Predicate, constant.Value);
            foreach (var branch in DC.ConditionsFor(input))
            {
                if (branch.Condition is not LlvmICmpInstruction { Right: LlvmConstantInt boundary } premise || premise.Left != input) continue;
                LlvmCmpPredicate predicate;
                Count("icmp.constant.dominating.conditions-visited");
                if (DT.DominatesEdge(branch.Parent!, branch.GetSuccessor(0), compare.Parent!)) predicate = premise.Predicate;
                else if (DT.DominatesEdge(branch.Parent!, branch.GetSuccessor(1), compare.Parent!)) predicate = LlvmCmpInstruction.GetInversePredicate(premise.Predicate);
                else continue;
                var dominating = LlvmConstantRange.MakeExactICmpRegion(predicate, boundary.Value);
                Count("icmp.constant.dominating.range-query");
                var intersection = dominating.IntersectWith(region); var difference = dominating.Difference(region);
                if (intersection.IsEmptySet) return ReplaceInstUsesWith(compare, LlvmConstantInt.GetFalse(compare.Context));
                if (difference.IsEmptySet) return ReplaceInstUsesWith(compare, LlvmConstantInt.GetTrue(compare.Context));
                if (ICmpIsEquality(compare.Predicate) || LlvmValueTracking.IsSignBitCheck(compare.Predicate, constant.Value, out _) && ICmpHasBranchUse(compare)) continue;
                if (compare.HasOneUse && compare.Users.First() is LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } && LlvmIntrinsic.IsMinMax(descriptor.Kind)) continue;
                if (intersection.GetSingleElement() is { } equal) return IccCompare(LlvmCmpPredicate.ICmpEq, input, IccConstant(input, equal));
                if (difference.GetSingleElement() is { } unequal) return IccCompare(LlvmCmpPredicate.ICmpNe, input, IccConstant(input, unequal));
            }
            return null;
        }
    }
}
