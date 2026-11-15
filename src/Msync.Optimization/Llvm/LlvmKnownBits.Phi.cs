// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp PHI
// simple binary recurrence and one-level incoming intersection.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmKnownBits ComputePhi(LlvmPhiInstruction phi, uint depth, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        uint width = phi.Type.IsPointerTy() ? (layout ?? GetQueryLayout(phi, null)).GetPointerTypeSizeInBits(phi.Type) : phi.Type.GetIntegerBitWidth();
        LlvmKnownBits known = new(width);
        if (phi.IncomingCount == 2)
        {
            for (int index = 0; index < 2; ++index)
            {
                if (phi.GetIncomingValue(index) is not LlvmBinaryOperator binary) continue;
                bool leftPhi = ReferenceEquals(binary.Left, phi), rightPhi = ReferenceEquals(binary.Right, phi);
                if (!leftPhi && !rightPhi) continue;
                LlvmValue start = phi.GetIncomingValue(1 - index), step = leftPhi ? binary.Right : binary.Left;
                if (ReferenceEquals(start, phi) || ReferenceEquals(step, phi)) continue;
                LlvmKnownBits initial = Compute(start, depth + 1, phi, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
                Count(metrics, "known_bits.phi.recurrence");
                switch (binary.Opcode)
                {
                    case LlvmOpcode.Shl when leftPhi:
                        known = new(LlvmAPInt.GetLowBitsSet(width, initial.CountMinTrailingZeros()), known.One); break;
                    case LlvmOpcode.LShr or LlvmOpcode.UDiv when leftPhi:
                    case LlvmOpcode.URem:
                        known = new(LlvmAPInt.GetHighBitsSet(width, initial.CountMinLeadingZeros()), known.One); break;
                    case LlvmOpcode.AShr when leftPhi:
                        known = new(LlvmAPInt.GetHighBitsSet(width, initial.CountMinLeadingZeros()), LlvmAPInt.GetHighBitsSet(width, initial.CountMinLeadingOnes())); break;
                    case LlvmOpcode.And:
                    case LlvmOpcode.Or:
                    case LlvmOpcode.Add:
                    case LlvmOpcode.Sub:
                    case LlvmOpcode.Mul:
                        LlvmKnownBits increment = Compute(step, depth + 1, phi.GetIncomingBlock(index).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
                        initial = Compute(start, depth + 1, phi.GetIncomingBlock(1 - index).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
                        if (binary.Opcode == LlvmOpcode.And) known = new(initial.Zero, initial.One & increment.One);
                        else if (binary.Opcode == LlvmOpcode.Or) known = new(initial.Zero & increment.Zero, initial.One);
                        else known = new(LlvmAPInt.GetLowBitsSet(width, Math.Min(initial.CountMinTrailingZeros(), increment.CountMinTrailingZeros())), known.One);
                        if ((!useInstructionInfo || !binary.HasNoSignedWrap)) break;
                        if (binary.Opcode == LlvmOpcode.Add)
                        {
                            if (initial.IsNonNegative && increment.IsNonNegative) known = known.WithNonNegative();
                            else if (initial.IsNegative && increment.IsNegative) known = known.WithNegative();
                        }
                        if (binary.Opcode == LlvmOpcode.Sub && leftPhi)
                        {
                            if (initial.IsNonNegative && increment.IsNegative) known = known.WithNonNegative();
                            else if (initial.IsNegative && increment.IsNonNegative) known = known.WithNegative();
                        }
                        if (binary.Opcode == LlvmOpcode.Mul && initial.IsNonNegative && increment.IsNonNegative) known = known.WithNonNegative();
                        break;
                }
                break;
            }
        }
        if (known.IsUnknown && phi.Type.IsIntegerTy() && phi.IncomingCount == 2)
            known = ComputeMinMaxPhiRecurrence(phi, depth, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
        if (known.IsUnknown && phi.Type.IsPointerTy() && phi.IncomingCount == 2)
        {
            for (int index = 0; index < 2; ++index)
            {
                if (phi.GetIncomingValue(index) is not LlvmGetElementPtrInstruction gep || gep.IndexCount == 0 || !ReferenceEquals(gep.PointerOperand, phi)) continue;
                LlvmKnownBits initial = Compute(phi.GetIncomingValue(1 - index), depth + 1,
                    phi.GetIncomingBlock(1 - index).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
                uint zeros = Math.Min(initial.CountMinTrailingZeros(), GetGepPreservedZeros(gep, layout));
                known = new(LlvmAPInt.GetLowBitsSet(width, zeros), known.One);
                break;
            }
        }
        if (!known.IsUnknown || phi.IncomingCount == 0 || depth >= MaxDepth - 1) return known;
        known = LlvmKnownBits.Conflict(width);
        bool hasIncoming = false;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            LlvmValue incoming = phi.GetIncomingValue(index);
            if (ReferenceEquals(incoming, phi)) continue;
            LlvmPhiInstruction contextPhi = phi;
            LlvmInstruction edge = phi.GetIncomingBlock(index).GetTerminator();
            if (incoming is LlvmSelectInstruction select)
            {
                if (ReferenceEquals(select.TrueValue, phi)) incoming = select.FalseValue;
                else if (ReferenceEquals(select.FalseValue, phi)) incoming = select.TrueValue;
            }
            if (incoming is LlvmPhiInstruction { IncomingCount: 2 } other)
            {
                for (int nested = 0; nested < 2; ++nested)
                {
                    if (!ReferenceEquals(other.GetIncomingValue(nested), phi)) continue;
                    incoming = other.GetIncomingValue(1 - nested);
                    edge = other.GetIncomingBlock(1 - nested).GetTerminator();
                    contextPhi = other;
                    break;
                }
            }
            hasIncoming = true;
            Count(metrics, "known_bits.phi.incoming");
            LlvmKnownBits bits = Compute(incoming, MaxDepth - 1, edge, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
            if (!bits.IsConstant && edge is LlvmCondBrInstruction { Condition: LlvmICmpInstruction cmp } branch)
            {
                bool trueEdge = ReferenceEquals(branch.GetSuccessor(0), contextPhi.Parent), falseEdge = ReferenceEquals(branch.GetSuccessor(1), contextPhi.Parent);
                LlvmCmpPredicate pred = cmp.Predicate;
                LlvmValue? rhs = null;
                if (ReferenceEquals(cmp.Left, incoming)) rhs = cmp.Right;
                else if (ReferenceEquals(cmp.Right, incoming)) { rhs = cmp.Left; pred = LlvmCmpInstruction.GetSwappedPredicate(pred); }
                if (rhs is not null && trueEdge != falseEdge)
                {
                    if (falseEdge) pred = LlvmCmpInstruction.GetInversePredicate(pred);
                    LlvmKnownBits rhsBits = Compute(rhs, MaxDepth - 1, edge, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
                    if (!rhsBits.IsUnknown)
                    {
                        LlvmConstantRange range = LlvmConstantRange.FromKnownBits(rhsBits, pred is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle);
                        bits = bits.UnionWith(LlvmConstantRange.MakeAllowedICmpRegion(pred, range).ToKnownBits());
                        if (bits.HasConflict) return new(width);
                    }
                }
            }
            known = known.IntersectWith(bits);
            if (known.IsUnknown) break;
        }
        return hasIncoming ? known : new(width);
    }

    public static bool MaskedValueIsZero(LlvmValue value, LlvmAPInt mask, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        Count(metrics, "known_bits.masked_zero.queries");
        return (Compute(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout).Zero & mask) == mask;
    }
    public static bool IsKnownNonNegative(LlvmValue value, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null) => Compute(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout).IsNonNegative;
    public static bool IsKnownNegative(LlvmValue value, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null) => Compute(value, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout).IsNegative;

    public static bool IsKnownNegation(LlvmValue left, LlvmValue right, bool needNSW = false, bool allowPoison = true)
    {
        static bool Negates(LlvmValue a, LlvmValue b, bool nsw, bool poison) =>
            a is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } binary && ReferenceEquals(binary.Right, b)
            && (!nsw || binary.HasNoSignedWrap)
            && binary.Left is LlvmConstantInt { IsZero: true };
        if (Negates(left, right, needNSW, allowPoison) || Negates(right, left, needNSW, allowPoison)) return true;
        return left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } lhs && right is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } rhs
            && (!needNSW || (lhs.HasNoSignedWrap && rhs.HasNoSignedWrap))
            && ReferenceEquals(lhs.Left, rhs.Right) && ReferenceEquals(lhs.Right, rhs.Left);
    }
}
