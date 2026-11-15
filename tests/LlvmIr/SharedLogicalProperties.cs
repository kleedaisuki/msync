using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SharedLogicalProperties
{
    internal static (int OverflowMatches, int TruthTables) Run() => (OverflowMatcher(), TruthTables());

    private static int OverflowMatcher()
    {
        var context = new LlvmContext();
        var module = new LlvmModule("overflow-check-properties", context);
        var i8 = LlvmIntegerType.Get(context, 8);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i8, i8]), LlvmLinkage.InternalLinkage, "checks", module);
        var builder = new LlvmTransformBuilder(new LlvmBasicBlock(context, "entry", function));
        var x = function.Arguments[0]; var y = function.Arguments[1];
        var notZero = (LlvmInstruction)builder.CreateICmp(LlvmCmpPredicate.ICmpNe, x, LlvmConstantInt.Get(i8, 0));
        var zero = (LlvmInstruction)builder.CreateICmp(LlvmCmpPredicate.ICmpEq, x, LlvmConstantInt.Get(i8, 0));
        int count = 0;
        foreach (var kind in new[] { LlvmIntrinsicKind.UMulWithOverflow, LlvmIntrinsicKind.SMulWithOverflow })
            foreach (bool swap in new[] { false, true })
            {
                var aggregate = LlvmIntrinsic.CreateIntrinsic(kind, swap ? [y, x] : [x, y], module);
                builder.Insert(aggregate);
                var overflow = builder.Insert(new LlvmExtractValueInstruction(aggregate, [1]));
                var complement = builder.CreateNot(overflow);
                Require(LlvmOverflowInstAnalysis.IsCheckForZeroAndMulWithOverflow(notZero, overflow, true, out var use) &&
                    use == aggregate.GetOperandUse(swap ? 0 : 1), "Source overflow matcher must return the other multiply Use.");
                Require(LlvmOverflowInstAnalysis.IsCheckForZeroAndMulWithOverflow(zero, complement, false, out use) &&
                    use == aggregate.GetOperandUse(swap ? 0 : 1), "The zero OR negated-overflow source form must match.");
                Require(!LlvmOverflowInstAnalysis.IsCheckForZeroAndMulWithOverflow(zero, overflow, true), "Zero equality is not the AND source form.");
                var product = builder.Insert(new LlvmExtractValueInstruction(aggregate, [0]));
                Require(!LlvmOverflowInstAnalysis.IsCheckForZeroAndMulWithOverflow(notZero, product, true), "The product is not an overflow bit.");
                count += 4;
            }
        builder.CreateRet(x);
        return count;
    }

    private static int TruthTables()
    {
        int count = 0;
        foreach (bool logical in new[] { false, true })
        {
            var context = new LlvmContext(); var module = new LlvmModule("logical-select-properties", context);
            var i1 = LlvmIntegerType.Get(context, 1);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i1, [i1, i1, i1]), LlvmLinkage.InternalLinkage, "and_or", module);
            var builder = new LlvmTransformBuilder(new LlvmBasicBlock(context, "entry", function));
            var condition = function.Arguments[0]; var a = function.Arguments[1]; var b = function.Arguments[2];
            var not = builder.CreateNot(condition);
            LlvmValue left = logical ? builder.CreateLogicalAnd(a, condition) : builder.CreateAnd(a, condition);
            LlvmValue right = logical ? builder.CreateLogicalAnd(b, not) : builder.CreateAnd(b, not);
            var combined = logical ? builder.CreateLogicalOr(left, right) : builder.CreateOr(left, right);
            builder.CreateRet(combined);
            Optimize(function);
            for (ulong c = 0; c < 2; ++c)
                for (ulong x = 0; x < 2; ++x)
                    for (ulong y = 0; y < 2; ++y)
                    {
                        Require(Evaluate(function, [c, x, y]) == (c != 0 ? x : y), "Boolean AND/OR to select changed its truth table.");
                        ++count;
                    }
        }
        foreach (var opcode in new[] { LlvmOpcode.And, LlvmOpcode.Or })
        {
            var context = new LlvmContext(); var module = new LlvmModule("sext-select-properties", context);
            var i1 = LlvmIntegerType.Get(context, 1); var i8 = LlvmIntegerType.Get(context, 8);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(i8, [i1]), LlvmLinkage.InternalLinkage, "sext_constant", module);
            var builder = new LlvmTransformBuilder(new LlvmBasicBlock(context, "entry", function));
            var extended = builder.CreateSExt(function.Arguments[0], i8);
            builder.CreateRet(builder.CreateBinOp(opcode, extended, LlvmConstantInt.Get(i8, 42)));
            Optimize(function);
            for (ulong value = 0; value < 2; ++value)
            {
                ulong extendedValue = value == 0 ? 0UL : 255UL;
                ulong expected = opcode == LlvmOpcode.And ? extendedValue & 42UL : extendedValue | 42UL;
                Require(Evaluate(function, [value]) == expected, "Sext boolean constant folding changed its value.");
                ++count;
            }
        }
        return count;
    }
    private static void Optimize(LlvmFunction function)
    {
        var tree = new LlvmDominatorTree(function);
        LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), new LlvmAssumptionCache(function),
            new LlvmTargetLibraryInfo(function.Parent!), new LlvmTargetTransformInfo(function.Parent!.DataLayout), tree);
    }
    private static ulong Evaluate(LlvmFunction function, IReadOnlyList<ulong> arguments)
    {
        Dictionary<LlvmValue, ulong> values = [];
        for (int i = 0; i < arguments.Count; ++i) values[function.Arguments[i]] = arguments[i];
        ulong Value(LlvmValue value) => value is LlvmConstantInt integer ? integer.GetZExtValue() : values[value];
        long Signed(LlvmValue value) => (long)(Value(value) << (64 - (int)value.Type.GetScalarSizeInBits())) >> (64 - (int)value.Type.GetScalarSizeInBits());
        foreach (var instruction in function.BasicBlocks[0].Instructions)
        {
            if (instruction is LlvmReturnInstruction result) return Value(result.ReturnValue!);
            uint width = instruction.Type.GetScalarSizeInBits();
            ulong mask = width == 64 ? ulong.MaxValue : (1UL << (int)width) - 1;
            values[instruction] = instruction switch
            {
                LlvmBinaryOperator binary => (binary.Opcode switch
                { LlvmOpcode.And => Value(binary.Left) & Value(binary.Right), LlvmOpcode.Or => Value(binary.Left) | Value(binary.Right),
                  LlvmOpcode.Xor => Value(binary.Left) ^ Value(binary.Right), LlvmOpcode.Add => Value(binary.Left) + Value(binary.Right),
                  LlvmOpcode.Sub => unchecked(Value(binary.Left) - Value(binary.Right)), LlvmOpcode.Mul => Value(binary.Left) * Value(binary.Right),
                  LlvmOpcode.Shl => Value(binary.Left) << (int)Value(binary.Right), LlvmOpcode.LShr => Value(binary.Left) >> (int)Value(binary.Right),
                  LlvmOpcode.AShr => unchecked((ulong)(Signed(binary.Left) >> (int)Value(binary.Right))),
                  _ => throw new InvalidOperationException("Unexpected property binop.") }) & mask,
                LlvmICmpInstruction compare => (compare.Predicate switch
                { LlvmCmpPredicate.ICmpEq => Value(compare.Left) == Value(compare.Right), LlvmCmpPredicate.ICmpNe => Value(compare.Left) != Value(compare.Right),
                  LlvmCmpPredicate.ICmpUlt => Value(compare.Left) < Value(compare.Right), LlvmCmpPredicate.ICmpUle => Value(compare.Left) <= Value(compare.Right),
                  LlvmCmpPredicate.ICmpUgt => Value(compare.Left) > Value(compare.Right), LlvmCmpPredicate.ICmpUge => Value(compare.Left) >= Value(compare.Right),
                  LlvmCmpPredicate.ICmpSlt => Signed(compare.Left) < Signed(compare.Right), LlvmCmpPredicate.ICmpSle => Signed(compare.Left) <= Signed(compare.Right),
                  LlvmCmpPredicate.ICmpSgt => Signed(compare.Left) > Signed(compare.Right), LlvmCmpPredicate.ICmpSge => Signed(compare.Left) >= Signed(compare.Right),
                  _ => throw new InvalidOperationException("Unexpected property predicate.") }) ? 1UL : 0UL,
                LlvmSelectInstruction select => Value(select.Condition) != 0 ? Value(select.TrueValue) : Value(select.FalseValue),
                LlvmFreezeInstruction freeze => Value(freeze.Operand),
                LlvmCastInstruction cast when cast.Opcode == LlvmOpcode.SExt =>
                    ((Value(cast.Operand) & (1UL << ((int)cast.Operand.Type.GetScalarSizeInBits() - 1))) != 0
                        ? Value(cast.Operand) | ~((1UL << (int)cast.Operand.Type.GetScalarSizeInBits()) - 1) : Value(cast.Operand)) & mask,
                LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.BitCast => Value(cast.Operand) & mask,
                _ => throw new InvalidOperationException($"Unexpected property instruction {instruction.Opcode}.")
            };
        }
        throw new InvalidOperationException("No property return.");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
