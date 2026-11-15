using Msync.Core.Llvm;
using Msync.Optimization.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

internal static class KnownFPClassProperties
{
    internal static int Run()
    {
        ulong[] encodings = [0, 0x8000000000000000, 1, 0x8000000000000001,
            0x0010000000000000, 0x8010000000000000, 0x3ff0000000000000, 0xbff0000000000000,
            0x7fefffffffffffff, 0xffefffffffffffff, 0x7ff0000000000000, 0xfff0000000000000,
            0x7ff0000000000001, 0xfff0000000000001, 0x7ff8000000000000, 0xfff8000000000000];
        int cases = 0;
        foreach (ulong encoding in encodings)
        {
            var value = LlvmAPFloat.FromBits(encoding);
            var known = new LlvmKnownFPClass(value);
            var bitcast = LlvmKnownFPClass.Bitcast(LlvmKnownBits.MakeConstant(value.BitcastToAPInt()));
            Program.Require(known.GetKnownFPClasses() == bitcast.GetKnownFPClasses() && known.SignBit == bitcast.SignBit,
                "APFloat classification and exact integer bitcast must retain the same signed IEEEdouble class.");
            var bits = known.ToKnownBits();
            Program.Require((value.BitcastToAPInt() & bits.Zero).IsZero && (value.BitcastToAPInt() & bits.One) == bits.One,
                "Known FP classes must not invent a bit conflicting with their concrete IEEEdouble member.");
            var negated = LlvmKnownFPClass.Negate(known);
            Program.Require(negated.SignBit == !known.SignBit && LlvmKnownFPClass.Negate(negated).GetKnownFPClasses() == known.GetKnownFPClasses(),
                "Fneg must flip the sign of every class, including signaling and quiet NaNs.");
            foreach (ulong other in encodings)
            {
                var rhs = LlvmAPFloat.FromBits(other);
                var right = new LlvmKnownFPClass(rhs);
                Member(LlvmKnownFPClass.Add(known, right, LlvmDenormalMode.IEEE), value.Add(rhs).Value);
                Member(LlvmKnownFPClass.Subtract(known, right, LlvmDenormalMode.IEEE), value.Subtract(rhs).Value);
                Member(LlvmKnownFPClass.Multiply(known, right, LlvmDenormalMode.IEEE), value.Multiply(rhs).Value);
                Member(LlvmKnownFPClass.Divide(known, right, LlvmDenormalMode.IEEE), value.Divide(rhs).Value);
                Member(LlvmKnownFPClass.Remainder(known, right, LlvmDenormalMode.IEEE), value.Mod(rhs).Value);
                cases += 5;
            }
        }
        Query();
        return cases;
    }

    private static void Member(LlvmKnownFPClass known, LlvmAPFloat result) =>
        Program.Require(!known.IsKnownNever(new LlvmKnownFPClass(result).GetKnownFPClasses()),
            "Source KnownFPClass arithmetic excluded the source APFloat operation's concrete result.");

    private static void Query()
    {
        LlvmContext context = new();
        LlvmModule module = new("known-fp", context);
        LlvmType type = LlvmType.GetDoubleTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "positive-select", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        var x = new LlvmFreezeInstruction(function.Arguments[0]);
        var zero = LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0));
        var compare = new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOgt, x, zero);
        var (classValue, classMask) = LlvmKnownFPClasses.FCmpToClassTest(compare.Predicate, x, zero);
        Program.Require(ReferenceEquals(classValue, x) && classMask == LlvmKnownFPClass.OrderedGreaterThanZeroMask,
            "An ordered positive comparison against zero must be an exact source fpclass test.");
        var one = LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x3ff0000000000000UL));
        Program.Require(LlvmKnownFPClasses.FCmpToClassTest(compare.Predicate, x, one).Value is null,
            "A comparison against an ordinary finite value must not pretend its implied classes are an exact test.");
        var select = new LlvmSelectInstruction(compare, x, zero);
        entry.Append(x); entry.Append(compare); entry.Append(select); entry.Append(new LlvmReturnInstruction(context, select));
        Dictionary<string, ulong> metrics = [];
        var known = LlvmKnownFPClasses.Compute(select, metrics: metrics);
        Program.Require(known.IsKnownNever(C.NaN | C.Negative) && known.SignBit == false &&
            metrics.GetValueOrDefault("known_fp.select.condition_refined") == 1,
            "A defined select arm must inherit its source fcmp condition's positive class restriction.");
        var square = new LlvmBinaryOperator(LlvmOpcode.FMul, x, x);
        Program.Require(LlvmKnownFPClasses.Compute(square).CannotBeOrderedLessThanZero(),
            "The source square case must exclude ordered-negative results without excluding NaNs.");
        var divide = new LlvmBinaryOperator(LlvmOpcode.FDiv, x, x);
        Program.Require(LlvmKnownFPClasses.Compute(divide).IsKnownNever(C.All & ~(C.NaN | C.PosNormal)),
            "A defined self-division must retain precisely the source 1.0-or-NaN class restriction.");
        Program.Require(LlvmValueTracking.IsGuaranteedNotToBeUndef(LlvmPoisonValue.Get(type)) &&
            !LlvmValueTracking.IsGuaranteedNotToBeUndef(LlvmUndefValue.Get(type)),
            "UndefOnly must distinguish poison from undef rather than aliasing the PoisonOnly query.");
        Program.Require(!LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(LlvmPoisonValue.Get(type)) &&
            !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(LlvmUndefValue.Get(type)) &&
            LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(x),
            "The combined source query must reject either undefined constant while accepting freeze results.");
        var definedArithmetic = new LlvmBinaryOperator(LlvmOpcode.FAdd, x, zero);
        Program.Require(LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(definedArithmetic) &&
            !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(definedArithmetic, depth: 6),
            "The combined query must recurse through represented arithmetic and preserve the source depth limit.");
    }
}
