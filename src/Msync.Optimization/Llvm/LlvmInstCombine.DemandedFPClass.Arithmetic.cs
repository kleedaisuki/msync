// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: demanded FPClass FAdd/FSub/FMul/FDiv.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool DemandedFPNoUndef(LlvmValue value, LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmValueTracking.IsGuaranteedNotToBeUndef(value, query.Tree, query.Context, query.Metrics, depth,
                assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC);
        private static bool FPInputMayBePositiveZero(LlvmDenormalModeInfo mode) => mode.Input is LlvmDenormalMode.PositiveZero or LlvmDenormalMode.Dynamic;
        private static bool FPOutputMayBePositiveZero(LlvmDenormalModeInfo mode) => mode.Output is LlvmDenormalMode.PositiveZero or LlvmDenormalMode.Dynamic;
        private LlvmValue? DemandedFPArithmetic(LlvmBinaryOperator instruction, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            var mode = F.GetDenormalMode(instruction.Type.GetScalarType().GetFltSemantics());
            var x = instruction.Left; var y = instruction.Right;
            bool self = x == y && DemandedFPNoUndef(x, query, depth + 1);
            bool noNaNResult = (demanded & C.NaN) == 0, noInfResult = (demanded & C.Infinity) == 0;
            bool noSignedZeros = (flags & LlvmFastMathFlags.NoSignedZeros) != 0;
            LlvmKnownFPClass left, right;
            if (instruction.Opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub)
            {
                if (self && instruction.Opcode == LlvmOpcode.FAdd)
                {
                    C sourceDemand = demanded;
                    if ((demanded & C.NaN) != 0) sourceDemand |= C.NaN;
                    if ((demanded & C.PosNormal) != 0) sourceDemand |= C.PosSubnormal;
                    if ((demanded & C.NegNormal) != 0) sourceDemand |= C.NegSubnormal;
                    if (mode != LlvmDenormalModeInfo.IEEE)
                    {
                        if ((demanded & C.PosZero) != 0)
                        {
                            sourceDemand |= C.PosSubnormal;
                            if (FPInputMayBePositiveZero(mode) || FPOutputMayBePositiveZero(mode)) sourceDemand |= C.NegSubnormal;
                        }
                        if ((demanded & C.NegZero) != 0) sourceDemand |= C.NegSubnormal;
                    }
                    if ((demanded & C.PosInfinity) != 0) sourceDemand |= C.PosNormal;
                    if ((demanded & C.NegInfinity) != 0) sourceDemand |= C.NegNormal;
                    if (SimplifyDemandedFPClass(instruction, 0, sourceDemand, out left, query, depth + 1)) return instruction;
                    known = LlvmKnownFPClass.AddSelf(left, mode); right = left;
                }
                else
                {
                    C sourceDemand = C.Finite;
                    if (!noNaNResult) sourceDemand |= C.NaN | C.Infinity;
                    if (!noInfResult) sourceDemand |= C.Infinity;
                    if (SimplifyDemandedFPClass(instruction, 1, sourceDemand, out right, query, depth + 1) ||
                        SimplifyDemandedFPClass(instruction, 0, sourceDemand, out left, query, depth + 1)) return instruction;
                    known = instruction.Opcode == LlvmOpcode.FAdd ? LlvmKnownFPClass.Add(left, right, mode) : LlvmKnownFPClass.Subtract(left, right, mode);
                }
                known.KnownNot(~demanded);
                if (FPClassConstant(instruction.Type, known.GetKnownFPClasses(), true) is { } constant) return constant;
                if (noNaNResult && instruction.Opcode == LlvmOpcode.FAdd && right.IsKnownAlways(C.Infinity | C.NaN) && left.IsKnownNeverNaN()) return y;
                if (noNaNResult && left.IsKnownAlways(C.Infinity | C.NaN) && right.IsKnownNeverNaN()) return x;
                return InferDemandedArithmeticFlags(instruction, flags, known, left, right);
            }
            if (instruction.Opcode == LlvmOpcode.FDiv)
                return DemandedFPDivide(instruction, demanded, ref known, flags, query, depth, mode, self);
            C demand = demanded & (C.NaN | C.Zero | C.Subnormal | C.Normal);
            if (!noInfResult) demand |= C.Subnormal | C.Normal | C.Infinity;
            if (!noNaNResult) demand |= C.Zero | C.Infinity | C.NaN | C.Subnormal;
            if ((demanded & (C.Normal | C.Subnormal | C.Zero)) != 0) demand |= C.Normal | C.Subnormal;
            if (self)
            {
                if (SimplifyDemandedFPClass(instruction, 0, demand, out left, query, depth + 1)) return instruction;
                known = LlvmKnownFPClass.Square(left, mode); known.KnownNot(~demanded);
                if (FPClassConstant(instruction.Type, known.GetKnownFPClasses(), true) is { } constant) return constant;
                if (!known.IsKnownAlways(C.PosZero | C.PosInfinity | C.NaN) || !left.IsKnownNever(C.Subnormal | C.Normal)) return null;
                if (left.IsKnownAlways(C.Positive)) return x;
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(instruction);
                return Builder.CreateFAbs(x, flags);
            }
            if (SimplifyDemandedFPClass(instruction, 1, demand, out right, query, depth + 1) ||
                SimplifyDemandedFPClass(instruction, 0, demand, out left, query, depth + 1)) return instruction;
            if ((flags & LlvmFastMathFlags.NoInfs) != 0) { left.KnownNot(C.Infinity); right.KnownNot(C.Infinity); }
            using var multiplyGuard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            Builder.SetInsertPoint(instruction);
            if ((noNaNResult || left.IsKnownNeverInfOrNaN()) && right.IsKnownAlways(C.PosZero | C.NaN)) return Builder.CreateCopySign(y, x, flags: flags);
            if (left.IsKnownAlways(C.PosZero | C.NaN) && (noNaNResult || right.IsKnownNeverInfOrNaN())) return Builder.CreateCopySign(x, y, flags: flags);
            if ((noNaNResult || left.IsKnownNeverInfOrNaN()) && right.IsKnownAlways(C.NegZero | C.NaN)) return Builder.CreateCopySign(y, Builder.CreateFNeg(x, fastMathFlags: flags), flags: flags);
            if (left.IsKnownAlways(C.NegZero | C.NaN) && (noNaNResult || right.IsKnownNeverInfOrNaN())) return Builder.CreateCopySign(x, Builder.CreateFNeg(y, fastMathFlags: flags), flags: flags);
            var logicalLeft = LlvmKnownFPClass.ApplyInputDenormalMode(left, mode);
            var logicalRight = LlvmKnownFPClass.ApplyInputDenormalMode(right, mode);
            if (left.IsKnownAlways(C.Infinity | C.NaN) && right.IsKnownNeverNaN() && logicalRight.IsKnownNever(C.Positive | C.Zero)) return Builder.CreateFNeg(x, fastMathFlags: flags);
            if (right.IsKnownAlways(C.Infinity | C.NaN) && left.IsKnownNeverNaN() && logicalLeft.IsKnownNever(C.Positive | C.Zero)) return Builder.CreateFNeg(y, fastMathFlags: flags);
            known = LlvmKnownFPClass.Multiply(left, right, mode); known.KnownNot(~demanded);
            if (FPClassConstant(instruction.Type, known.GetKnownFPClasses(), true) is { } folded) return folded;
            return InferDemandedArithmeticFlags(instruction, flags, known, left, right);
        }
        private LlvmValue? InferDemandedArithmeticFlags(LlvmInstruction instruction, LlvmFastMathFlags flags,
            LlvmKnownFPClass known, LlvmKnownFPClass left, LlvmKnownFPClass right)
        {
            var inferred = InferFPValueFlags(flags, known.GetKnownFPClasses(), left, right);
            if (inferred == flags) return null;
            ((ILlvmFastMathInstruction)instruction).FastMathFlags = inferred;
            Count("demanded_fp.arithmetic_flags_inferred");
            return instruction;
        }
        private LlvmValue? DemandedFPDivide(LlvmBinaryOperator instruction, C demanded, ref LlvmKnownFPClass known,
            LlvmFastMathFlags flags, LlvmInstructionSimplify.BinaryQuery query, uint depth, LlvmDenormalModeInfo mode, bool self)
        {
            using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            Builder.SetInsertPoint(instruction);
            var type = instruction.Type; var x = instruction.Left; var y = instruction.Right;
            var semantics = type.GetScalarType().GetFltSemantics();
            var zero = LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(semantics));
            var infinity = LlvmConstantFP.Get(type, LlvmAPFloat.GetInf(semantics));
            if (self)
            {
                var isZero = Builder.CreateFCmp(LlvmCmpPredicate.FCmpUeq, x, zero, fastMathFlags: flags);
                var isInfinity = Builder.CreateFCmp(LlvmCmpPredicate.FCmpUeq, Builder.CreateFAbs(x, flags), infinity, fastMathFlags: flags);
                var special = Builder.CreateBinary(LlvmOpcode.Or, isInfinity, isZero);
                var nan = LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(semantics).Value);
                return Builder.CreateSelect(special, nan, LlvmConstantFP.Get(type, LlvmAPFloat.GetOne(semantics)), fastMathFlags: flags);
            }
            C sourceDemand = (demanded & C.NaN) | C.Subnormal;
            if ((demanded & (C.Subnormal | C.Normal)) != 0) sourceDemand |= C.Normal;
            if ((demanded & C.NaN) != 0) sourceDemand |= C.Zero | C.Infinity | C.NaN;
            if ((demanded & C.Zero) != 0) sourceDemand |= C.Normal | C.Subnormal;
            C leftDemand = sourceDemand, rightDemand = sourceDemand;
            if ((demanded & C.Zero) != 0) { leftDemand |= C.Zero; rightDemand |= C.Infinity; }
            if ((demanded & C.Infinity) != 0) { leftDemand |= C.Infinity | C.Normal | C.Subnormal; rightDemand |= C.Zero | C.Subnormal | C.Normal; }
            if (SimplifyDemandedFPClass(instruction, 0, leftDemand, out var left, query, depth + 1) ||
                SimplifyDemandedFPClass(instruction, 1, rightDemand, out var right, query, depth + 1)) return instruction;
            bool noNaN = (demanded & C.NaN) == 0, noInf = (demanded & C.Infinity) == 0;
            bool noSignedZeros = (flags & LlvmFastMathFlags.NoSignedZeros) != 0;
            bool ignoreZeroByZero = noNaN || right.IsKnownNeverLogicalZero(mode);
            if (noSignedZeros && left.IsKnownAlways(C.Zero) && right.IsKnownNeverNaN() && ignoreZeroByZero) return zero;
            if (left.IsKnownAlways(C.PosZero) && right.IsKnownNeverNaN() && ignoreZeroByZero) return Builder.CreateCopySign(x, y, flags: flags);
            if (!noInf && (noNaN || left.IsKnownNeverNaN() && left.IsKnownNeverLogicalZero(mode)) &&
                (right.IsKnownAlways(C.PosZero) || noSignedZeros && right.IsKnownAlways(C.Zero))) return Builder.CreateCopySign(infinity, x, flags: flags);
            if (noNaN && noInf && right.IsKnownAlways(C.Zero)) return LlvmPoisonValue.Get(type);
            known = LlvmKnownFPClass.Divide(left, right, mode); known.KnownNot(~demanded);
            if (FPClassConstant(type, known.GetKnownFPClasses(), true) is { } constant) return constant;
            return InferDemandedArithmeticFlags(instruction, flags, known, left, right);
        }
    }
}
