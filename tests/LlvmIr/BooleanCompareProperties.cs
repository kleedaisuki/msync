using Msync.Core.Llvm;
using Msync.Optimization.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

internal static class BooleanCompareProperties
{
    private static readonly ulong[] FloatingBits =
    [
        0, 0x8000000000000000UL, 1, 0x8000000000000001UL,
        0x000fffffffffffffUL, 0x800fffffffffffffUL, 0x0010000000000000UL, 0x8010000000000000UL,
        0x3ff0000000000000UL, 0xbff0000000000000UL, 0x4000000000000000UL, 0xc000000000000000UL,
        0x7fefffffffffffffUL, 0xffefffffffffffffUL, 0x7ff0000000000000UL, 0xfff0000000000000UL,
        0x7ff8000000000000UL, 0xfff8000000000000UL, 0x7ff0000000000001UL, 0xfff0000000000001UL
    ];

    internal static (int ComparisonCodes, int FloatingPredicates) Run()
        => (Codes(), FloatingPredicates());

    private static int Codes()
    {
        int count = 0;
        var context = new LlvmContext();
        var type = LlvmIntegerType.Get(context, 4);
        foreach (bool signed in new[] { false, true })
            for (uint code = 0; code < 8; ++code)
            {
                LlvmConstant? constant = LlvmCmpInstAnalysis.GetPredForICmpCode(code, signed, type, out var predicate);
                if (constant is null && LlvmCmpInstAnalysis.GetICmpCode(predicate) != code)
                    throw new InvalidOperationException("ICmp code roundtrip disagrees.");
                for (int a = 0; a < 16; ++a)
                    for (int b = 0; b < 16; ++b)
                    {
                        int x = signed && a >= 8 ? a - 16 : a, y = signed && b >= 8 ? b - 16 : b;
                        uint relation = x > y ? 1U : x == y ? 2U : 4U;
                        bool expected = (code & relation) != 0;
                        bool actual = constant is LlvmConstantInt boolean ? !boolean.IsZero :
                            IntCompare(predicate, a, b, 16);
                        if (actual != expected) throw new InvalidOperationException("ICmp code truth table disagrees.");
                        ++count;
                    }
            }
        for (int p = 32; p <= 41; ++p)
            for (ulong rhs = 0; rhs < 16; ++rhs)
            {
                if (!LlvmValueTracking.IsSignBitCheck((LlvmCmpPredicate)p, new(4, rhs), out bool negative)) continue;
                for (int value = 0; value < 16; ++value)
                {
                    bool expected = negative ? value >= 8 : value < 8;
                    if (IntCompare((LlvmCmpPredicate)p, value, (int)rhs, 16) != expected)
                        throw new InvalidOperationException("Sign-bit classification disagrees.");
                    ++count;
                }
            }
        return count;
    }

    private static int FloatingPredicates()
    {
        int count = 0;
        var context = new LlvmContext();
        var module = new LlvmModule("floating-predicate-properties", context);
        var type = LlvmType.GetDoubleTy(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]), LlvmLinkage.InternalLinkage, "classification", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        LlvmValue input = function.Arguments[0];
        block.Append(new LlvmReturnInstruction(context, input));
        LlvmValue abs = new LlvmTransformBuilder(block.GetTerminator()).CreateFAbs(input);
        foreach (bool absolute in new[] { false, true })
            foreach (ulong rhsBits in FloatingBits)
                for (int p = 0; p < 16; ++p)
                {
                    LlvmValue left = absolute ? abs : input;
                    var right = LlvmConstantFP.Get(type, new LlvmAPFloat(LlvmAPFloatSemantics.IEEEdouble, new(64, rhsBits)));
                    var exact = LlvmKnownFPClasses.FCmpToClassTest((LlvmCmpPredicate)p, function, left, right);
                    var implied = LlvmKnownFPClasses.FCmpImpliesClass((LlvmCmpPredicate)p, function, left, right);
                    foreach (ulong inputBits in FloatingBits)
                    {
                        ulong testedBits = absolute ? inputBits & 0x7fffffffffffffffUL : inputBits;
                        double lhsNumber = BitConverter.UInt64BitsToDouble(testedBits), rhsNumber = BitConverter.UInt64BitsToDouble(rhsBits);
                        uint relation = double.IsNaN(lhsNumber) || double.IsNaN(rhsNumber) ? 8U : lhsNumber < rhsNumber ? 4U : lhsNumber > rhsNumber ? 2U : 1U;
                        bool answer = ((uint)p & relation) != 0;
                        if (exact.Value is not null)
                        {
                            ulong selected = exact.Value == input ? inputBits : testedBits;
                            if (((Classify(selected) & exact.Classes) != 0) != answer)
                                throw new InvalidOperationException($"FP exact predicate {p}, abs={absolute}, rhs={rhsBits:x16}, value={inputBits:x16} disagrees.");
                            ++count;
                        }
                        if (implied.Value is not null)
                        {
                            ulong selected = implied.Value == input ? inputBits : testedBits;
                            C mask = answer ? implied.True : implied.False;
                            if ((Classify(selected) & mask) == 0)
                                throw new InvalidOperationException($"FP implied predicate {p}, abs={absolute}, rhs={rhsBits:x16}, value={inputBits:x16} lost class.");
                            ++count;
                        }
                    }
                }
        function.DenormalFPEnvironment = new(LlvmDenormalModeInfo.PreserveSign, LlvmDenormalModeInfo.PreserveSign);
        foreach (var predicate in new[] { LlvmCmpPredicate.FCmpOeq, LlvmCmpPredicate.FCmpOgt, LlvmCmpPredicate.FCmpUne })
        {
            var result = LlvmKnownFPClasses.FCmpToClassTest(predicate, function, input, LlvmConstantFP.Get(type, 0.0));
            if (result.Value is not null) throw new InvalidOperationException("Non-IEEE input mode must not use the zero-class identity.");
            ++count;
        }
        return count;
    }

    private static C Classify(ulong bits)
    {
        bool negative = (bits >> 63) != 0;
        ulong exponent = bits >> 52 & 0x7ffUL, fraction = bits & 0xfffffffffffffUL;
        if (exponent == 0x7ffUL)
            return fraction == 0 ? negative ? C.NegInfinity : C.PosInfinity : (fraction & 0x8000000000000UL) != 0 ? C.QNaN : C.SNaN;
        if (exponent == 0) return fraction == 0 ? negative ? C.NegZero : C.PosZero : negative ? C.NegSubnormal : C.PosSubnormal;
        return negative ? C.NegNormal : C.PosNormal;
    }
    private static bool IntCompare(LlvmCmpPredicate predicate, int left, int right, int modulus)
    {
        int signedLeft = left >= modulus / 2 ? left - modulus : left, signedRight = right >= modulus / 2 ? right - modulus : right;
        return predicate switch
        {
            LlvmCmpPredicate.ICmpEq => left == right, LlvmCmpPredicate.ICmpNe => left != right,
            LlvmCmpPredicate.ICmpUgt => left > right, LlvmCmpPredicate.ICmpUge => left >= right,
            LlvmCmpPredicate.ICmpUlt => left < right, LlvmCmpPredicate.ICmpUle => left <= right,
            LlvmCmpPredicate.ICmpSgt => signedLeft > signedRight, LlvmCmpPredicate.ICmpSge => signedLeft >= signedRight,
            LlvmCmpPredicate.ICmpSlt => signedLeft < signedRight, LlvmCmpPredicate.ICmpSle => signedLeft <= signedRight,
            _ => throw new InvalidOperationException()
        };
    }
}
