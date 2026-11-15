using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class OverflowProperties
{
    internal static int Run()
    {
        int checks = CheckRangeClassifications();
        LlvmContext context = new(); LlvmModule module = new("overflow", context);
        var type = LlvmType.GetInt8Ty(context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type]), LlvmLinkage.InternalLinkage, "overflow", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var yes = new LlvmBasicBlock(context, "uge", function); var no = new LlvmBasicBlock(context, "ult", function);
        var x = function.Arguments[0]; var y = function.Arguments[1];
        var mask = LlvmConstantInt.Get(type, 7); var zero = LlvmConstantInt.Get(type, 0); var one = LlvmConstantInt.Get(type, 1);
        var lowX = new LlvmBinaryOperator(LlvmOpcode.And, x, mask); var lowY = new LlvmBinaryOperator(LlvmOpcode.And, y, mask);
        Require(LlvmValueTracking.ComputeOverflowForUnsignedAdd(lowX, lowY, module.DataLayout) == LlvmOverflowResult.NeverOverflows,
            "Unsigned overflow query must use nonconstant KnownBits/range facts.");
        Require(LlvmValueTracking.ComputeOverflowForSignedAdd(lowX, lowY, module.DataLayout) == LlvmOverflowResult.NeverOverflows &&
            LlvmValueTracking.ComputeOverflowForSignedSub(lowX, lowY, module.DataLayout) == LlvmOverflowResult.NeverOverflows,
            "Two-sign-bit operands use the source signed add/sub shortcut.");
        Require(LlvmValueTracking.ComputeOverflowForUnsignedSub(zero, one, module.DataLayout) == LlvmOverflowResult.AlwaysOverflowsLow,
            "Unsigned subtraction classifies guaranteed underflow separately.");
        Require(LlvmValueTracking.ComputeOverflowForSignedAdd(LlvmConstantInt.Get(type, 127), one, module.DataLayout) == LlvmOverflowResult.AlwaysOverflowsHigh,
            "Signed addition classifies guaranteed high overflow separately.");
        var flagged = new LlvmBinaryOperator(LlvmOpcode.Add, x, y) { HasNoSignedWrap = true };
        Require(LlvmValueTracking.ComputeOverflowForSignedAdd(flagged, module.DataLayout, useInstructionInfo: false) == LlvmOverflowResult.NeverOverflows,
            "The source AddOperator overload reads its raw NSW flag rather than IIQ.");
        var condition = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUge, x, y);
        entry.Append(condition); entry.Append(new LlvmCondBrInstruction(condition, yes, no));
        var yesResult = new LlvmBinaryOperator(LlvmOpcode.Sub, x, y); var noResult = new LlvmBinaryOperator(LlvmOpcode.Sub, x, y);
        yes.Append(yesResult); yes.Append(new LlvmReturnInstruction(context, yesResult));
        no.Append(noResult); no.Append(new LlvmReturnInstruction(context, noResult));
        Require(LlvmValueTracking.ComputeOverflowForUnsignedSub(x, y, module.DataLayout, yesResult) == LlvmOverflowResult.NeverOverflows &&
            LlvmValueTracking.ComputeOverflowForUnsignedSub(x, y, module.DataLayout, noResult) == LlvmOverflowResult.AlwaysOverflowsLow,
            "Source dominating unsigned comparison determines both overflow directions.");
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); checks++; }
    }

    private static int CheckRangeClassifications()
    {
        int checks = 0;
        for (uint width = 1; width <= 3; width++)
        {
            int limit = 1 << (int)width;
            var ranges = new List<LlvmConstantRange> { LlvmConstantRange.Empty(width), LlvmConstantRange.Full(width) };
            for (int lower = 0; lower < limit; lower++)
            for (int upper = 0; upper < limit; upper++)
                if (lower != upper) ranges.Add(new(new LlvmAPInt(width, (ulong)lower), new LlvmAPInt(width, (ulong)upper)));
            foreach (var left in ranges)
            foreach (var right in ranges)
            {
                Check(left.UnsignedAddMayOverflow(right), signed: false, subtract: false);
                Check(left.UnsignedSubMayOverflow(right), signed: false, subtract: true);
                Check(left.SignedAddMayOverflow(right), signed: true, subtract: false);
                Check(left.SignedSubMayOverflow(right), signed: true, subtract: true);
                void Check(LlvmOverflowResult actual, bool signed, bool subtract)
                {
                    bool any = false, allLow = true, allHigh = true, anyOverflow = false;
                    for (int a = 0; a < limit; a++)
                    for (int b = 0; b < limit; b++)
                    {
                        if (!left.Contains(new LlvmAPInt(width, (ulong)a)) || !right.Contains(new LlvmAPInt(width, (ulong)b))) continue;
                        int av = signed && a >= limit / 2 ? a - limit : a;
                        int bv = signed && b >= limit / 2 ? b - limit : b;
                        int result = subtract ? av - bv : av + bv;
                        int minimum = signed ? -limit / 2 : 0, maximum = signed ? limit / 2 - 1 : limit - 1;
                        bool low = result < minimum, high = result > maximum;
                        any = true; allLow &= low; allHigh &= high; anyOverflow |= low || high;
                    }
                    var expected = !any ? LlvmOverflowResult.MayOverflow : allLow ? LlvmOverflowResult.AlwaysOverflowsLow :
                        allHigh ? LlvmOverflowResult.AlwaysOverflowsHigh : anyOverflow ? LlvmOverflowResult.MayOverflow : LlvmOverflowResult.NeverOverflows;
                    Program.Require(actual == expected, $"i{width} range overflow classification: signed={signed}, sub={subtract}, expected={expected}, actual={actual}.");
                    checks++;
                }
            }
        }
        return checks;
    }
}
