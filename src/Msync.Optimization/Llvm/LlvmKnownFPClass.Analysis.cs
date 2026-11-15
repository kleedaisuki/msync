// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// computeKnownFPClass, computeKnownFPClassForFRem, breakSelfRecursivePHI.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownFPClasses
{
    private const int MaxDepth = 6;
    public static bool CannotBeNegativeZero(LlvmValue value, LlvmInstruction? contextInstruction = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) =>
        Compute(value, C.NegZero, contextInstruction, tree, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext).IsKnownNever(C.NegZero);

    public static LlvmKnownFPClass Compute(LlvmValue value, LlvmFastMathFlags flags, C interested,
        LlvmInstruction? contextInstruction = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, int depth = 0, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        C excluded = Excluded(flags);
        if (excluded != C.None) Count(metrics, "known_fp.fmf_query");
        var known = Compute(value, interested & ~excluded, contextInstruction, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext);
        // The source FMF wrapper sets classes, discarding NaN sign information.
        if ((flags & LlvmFastMathFlags.NoNaNs) != 0) known.SetKnownFPClasses(known.GetKnownFPClasses() & ~C.NaN);
        if ((flags & LlvmFastMathFlags.NoInfs) != 0) known.SetKnownFPClasses(known.GetKnownFPClasses() & ~C.Infinity);
        return known;
    }

    public static LlvmKnownFPClass Compute(LlvmValue value, C interested = C.All,
        LlvmInstruction? contextInstruction = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, int depth = 0, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Count(metrics, "known_fp.visited");
        if (value is LlvmConstantFP fp) { Count(metrics, "known_fp.constant"); return new(fp.Value); }
        if (value is LlvmConstantAggregateZero) return new(C.PosZero);
        if (value is LlvmPoisonValue) return LlvmKnownFPClass.Empty;
        if (value.Type is LlvmVectorType)
            throw new NotSupportedException("KnownFPClass vector demanded-elements analysis has not been ported.");
        if (value is LlvmConstantAggregate aggregate)
        {
            var result = LlvmKnownFPClass.Empty;
            foreach (var use in aggregate.OperandUses)
            {
                if (use.Value is not LlvmConstantFP element) return new();
                result |= new LlvmKnownFPClass(element.Value);
            }
            return result;
        }
        C flagsExcluded = value is ILlvmFastMathInstruction floating ? Excluded(floating.FastMathFlags) : C.None;
        var assumed = FromContext(value, contextInstruction, tree, metrics, assumptions, allowEphemerals, conditions, condContext);
        C excluded = flagsExcluded | (C.All & ~assumed.GetKnownFPClasses());
        var known = new LlvmKnownFPClass();
        if (value is LlvmInstruction instruction && depth < MaxDepth)
            known = ComputeInstruction(instruction, interested & ~excluded, contextInstruction, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext);
        else if (depth >= MaxDepth) Count(metrics, "known_fp.depth_limit");
        known.KnownNot(excluded);
        if (known.SignBit is null && assumed.SignBit is bool sign) known.SetSignBit(sign);
        if (flagsExcluded != C.None) Count(metrics, "known_fp.instruction_flags");
        if (!known.IsUnknown) Count(metrics, "known_fp.refined");
        return known;
    }

    private static LlvmKnownFPClass ComputeInstruction(LlvmInstruction instruction, C interested,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (metrics is not null) Count(metrics, "known_fp.opcode." + instruction.Opcode);
        LlvmKnownFPClass Recurse(LlvmValue value, C classes = C.All) => Compute(value, classes, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext);
        LlvmDenormalMode mode = instruction.Parent?.Parent is null ? LlvmDenormalMode.Dynamic : LlvmDenormalMode.IEEE;
        switch (instruction)
        {
            case LlvmUnaryOperator negate:
                return LlvmKnownFPClass.Negate(Recurse(negate.Operand, interested));
            case LlvmSelectInstruction select:
                return SelectArm(select.TrueValue, select.Condition, false, interested, context, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext)
                    .IntersectWith(SelectArm(select.FalseValue, select.Condition, true, interested, context, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext));
            case LlvmPhiInstruction phi:
                return Phi(phi, interested, context, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext);
            case LlvmCastInstruction cast:
                return Cast(cast, interested, context, tree, metrics, depth, assumptions, allowEphemerals, conditions, condContext);
            case LlvmExtractValueInstruction extract:
                return Recurse(extract.AggregateOperand, interested);
            case LlvmBinaryOperator binary:
                return Binary(binary, interested, context, tree, metrics, depth, mode, assumptions, allowEphemerals, conditions, condContext);
            default:
                // No nofpclass attributes, load metadata or additional FP intrinsic identity
                // are attached to this instruction. Context conditions are queried above.
                return new();
        }
    }

    private static LlvmKnownFPClass Binary(LlvmBinaryOperator binary, C interested,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmDenormalMode mode, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        LlvmKnownFPClass Recurse(LlvmValue value, C classes = C.All) => Compute(value, classes, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext);
        bool self = ReferenceEquals(binary.Left, binary.Right);
        switch (binary.Opcode)
        {
            case LlvmOpcode.FAdd:
            case LlvmOpcode.FSub:
            {
                bool wantNegative = binary.Opcode == LlvmOpcode.FAdd && (interested & LlvmKnownFPClass.OrderedLessThanZeroMask) != 0;
                bool wantNaN = (interested & C.NaN) != 0;
                bool wantNegZero = (interested & C.NegZero) != 0;
                if (!wantNegative && !wantNaN && !wantNegZero)
                { Count(metrics, "known_fp.interest_pruned"); return new(); }
                C srcInterest = interested | (wantNegative ? LlvmKnownFPClass.OrderedLessThanZeroMask : 0) | (wantNaN ? C.Infinity : 0);
                var right = Recurse(binary.Right, srcInterest);
                self = self && IsGuaranteedNotToBeUndef(binary.Left, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions);
                if (!((wantNaN && right.IsKnownNeverNaN()) || (wantNegative && right.CannotBeOrderedLessThanZero()) || wantNegZero || binary.Opcode == LlvmOpcode.FSub)) return new();
                if (self && binary.Opcode == LlvmOpcode.FAdd)
                { Count(metrics, "known_fp.add_self"); return LlvmKnownFPClass.AddSelf(right, mode); }
                var left = self ? right : Recurse(binary.Left, srcInterest);
                return binary.Opcode == LlvmOpcode.FAdd ? LlvmKnownFPClass.Add(left, right, mode) : LlvmKnownFPClass.Subtract(left, right, mode);
            }
            case LlvmOpcode.FMul:
                if (self)
                { Count(metrics, "known_fp.square"); return LlvmKnownFPClass.Square(Recurse(binary.Left), mode); }
                if (binary.Right is LlvmConstantFP constant) return LlvmKnownFPClass.Multiply(Recurse(binary.Left), constant.Value, mode);
                var multiplyRight = Recurse(binary.Right);
                return LlvmKnownFPClass.Multiply(Recurse(binary.Left), multiplyRight, mode);
            case LlvmOpcode.FDiv:
                if (self && IsGuaranteedNotToBeUndef(binary.Left, context, tree, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions))
                    return (interested & C.NaN) == 0 ? new(C.NaN | C.PosNormal) : LlvmKnownFPClass.DivideSelf(Recurse(binary.Left, C.NaN | C.Infinity | C.Zero | C.Subnormal), mode);
                if ((interested & (C.NaN | C.Negative | C.Positive)) == 0) return new();
                var divisor = Recurse(binary.Right);
                bool useful = divisor.IsKnownNever(C.SNaN) || divisor.IsKnownNever(C.NegNormal | C.NegSubnormal) || divisor.IsKnownNever(C.PosNormal | C.PosSubnormal);
                return LlvmKnownFPClass.Divide(useful ? Recurse(binary.Left) : new LlvmKnownFPClass(), divisor, mode);
            case LlvmOpcode.FRem:
                return Remainder(binary, interested, context, tree, metrics, depth, mode, assumptions, allowEphemerals, conditions);
            default: return new();
        }
    }

    private static LlvmKnownFPClass Remainder(LlvmBinaryOperator binary, C interested,
        LlvmInstruction? context, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmDenormalMode mode, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        C leftInterest = C.None, rightInterest = C.None;
        if ((interested & C.NaN) != 0) { leftInterest |= C.NaN | C.Infinity; rightInterest |= C.NaN | C.Zero | C.Subnormal; }
        if ((interested & (C.PosNormal | C.PosSubnormal)) != 0) leftInterest |= C.PosNormal | C.PosSubnormal;
        if ((interested & (C.NegNormal | C.NegSubnormal)) != 0) leftInterest |= C.NegNormal | C.NegSubnormal;
        if ((interested & C.NegZero) != 0) leftInterest |= C.NegFinite;
        if ((interested & C.PosZero) != 0) leftInterest |= C.PosFinite | C.NegNormal | C.NegSubnormal;
        if (ReferenceEquals(binary.Left, binary.Right) && IsGuaranteedNotToBeUndef(binary.Left, context, tree, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions))
            return LlvmKnownFPClass.RemainderSelf((leftInterest | rightInterest) != C.None
                ? Compute(binary.Left, leftInterest | rightInterest, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext) : new LlvmKnownFPClass(), mode);
        var left = leftInterest == C.None ? new LlvmKnownFPClass() : Compute(binary.Left, leftInterest, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext);
        var right = rightInterest != C.None && left.IsKnownNever(C.SNaN) ? Compute(binary.Right, rightInterest, context, tree, metrics, depth + 1, assumptions, allowEphemerals, conditions, condContext) : new LlvmKnownFPClass();
        return LlvmKnownFPClass.Remainder(left, right, mode);
    }

    private static LlvmKnownFPClass Phi(LlvmPhiInstruction phi, C interested, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (phi.IncomingCount == 0 || depth >= MaxDepth - 2 || phi.HasConstantValue() is LlvmUndefValue) return new();
        var known = new LlvmKnownFPClass();
        bool first = true;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            LlvmValue incoming = phi.GetIncomingValue(index);
            if (ReferenceEquals(incoming, phi)) continue;
            LlvmInstruction? incomingContext = phi.GetIncomingBlock(index).GetTerminator();
            if (incoming is LlvmSelectInstruction select)
            {
                if (ReferenceEquals(select.TrueValue, phi)) incoming = select.FalseValue;
                else if (ReferenceEquals(select.FalseValue, phi)) incoming = select.TrueValue;
            }
            if (incoming is LlvmPhiInstruction { IncomingCount: 2 } nested)
                for (int n = 0; n < 2; ++n)
                    if (ReferenceEquals(nested.GetIncomingValue(n), phi))
                    { incoming = nested.GetIncomingValue(1 - n); incomingContext = nested.GetIncomingBlock(1 - n).GetTerminator(); break; }
            if (ReferenceEquals(incoming, phi)) continue;
            Count(metrics, "known_fp.phi.incoming");
            var next = Compute(incoming, interested, incomingContext, tree, metrics, MaxDepth - 2, assumptions, allowEphemerals, conditions, null);
            known = first ? next : known | next;
            first = false;
            if (known.GetKnownFPClasses() == C.All) break;
        }
        return known;
    }

    private static LlvmKnownFPClass Cast(LlvmCastInstruction cast, C interested, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, int depth, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (cast.Opcode == LlvmOpcode.BitCast && cast.Operand.Type.IsIntegerTy() && cast.Type.IsDoubleTy())
            return LlvmKnownFPClass.Bitcast(LlvmKnownBitsAnalysis.Compute(cast.Operand, (uint)depth + 1, context, tree, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext));
        if (cast.Opcode is LlvmOpcode.FPExt or LlvmOpcode.FPTrunc)
            throw new NotSupportedException("KnownFPClass fpext/fptrunc require APFloat formats beyond represented binary64.");
        if (cast.Opcode is not (LlvmOpcode.SIToFP or LlvmOpcode.UIToFP)) return new();
        var known = new LlvmKnownFPClass();
        known.KnownNot(C.NaN | C.Subnormal | C.NegZero);
        if (cast.Opcode == LlvmOpcode.UIToFP) known.SetSignBit(false);
        if ((interested & (C.PosZero | C.Normal | C.Infinity)) == 0) return known;
        var bits = LlvmKnownBitsAnalysis.Compute(cast.Operand, (uint)depth + 1, context, tree, metrics, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
        if (bits.IsNonZero) known.KnownNot(C.PosZero);
        if (cast.Opcode == LlvmOpcode.SIToFP)
        {
            if (bits.IsNonNegative) known.SetSignBit(false);
            else if (bits.IsNegative) known.SetSignBit(true);
        }
        if ((interested & C.Infinity) != 0)
        {
            uint size = bits.BitWidth - (cast.Opcode == LlvmOpcode.UIToFP ? bits.CountMinLeadingZeros() : bits.CountMinSignBits());
            if (!cast.Type.IsDoubleTy()) throw new NotSupportedException("KnownFPClass integer-to-FP exponent query requires represented binary64.");
            if (1023 >= size) known.KnownNot(C.Infinity);
        }
        return known;
    }

    private static C Excluded(LlvmFastMathFlags flags) =>
        ((flags & LlvmFastMathFlags.NoNaNs) != 0 ? C.NaN : 0) | ((flags & LlvmFastMathFlags.NoInfs) != 0 ? C.Infinity : 0);
    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
