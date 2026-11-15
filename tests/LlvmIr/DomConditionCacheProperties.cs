using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class DomConditionCacheProperties
{
    internal static int Run()
    {
        int checks = 0;
        IntegerFacts();
        PowerFacts();
        FloatingFacts();
        DuplicateEdge();
        return checks;

        void IntegerFacts()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("dom-condition-integer", context);
            var integer = LlvmIntegerType.Get(context, 8);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer]), LlvmLinkage.InternalLinkage, "guarded", module);
            var value = function.Arguments[0];
            var entry = new LlvmBasicBlock(context, "entry", function);
            var yes = new LlvmBasicBlock(context, "yes", function);
            var yesChild = new LlvmBasicBlock(context, "yes.child", function);
            var no = new LlvmBasicBlock(context, "no", function);
            var comparison = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, value, LlvmConstantInt.Get(integer, 7));
            entry.Append(comparison);
            var branch = new LlvmCondBrInstruction(comparison, yes, no);
            entry.Append(branch);
            yes.Append(new LlvmUncondBrInstruction(yesChild));
            var add = new LlvmBinaryOperator(LlvmOpcode.Add, value, LlvmConstantInt.Get(integer, 1));
            yesChild.Append(add);
            var compareAgain = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSge, value, LlvmConstantInt.Get(integer, 0));
            yesChild.Append(compareAgain);
            yesChild.Append(new LlvmReturnInstruction(context, add));
            no.Append(new LlvmReturnInstruction(context, value));
            var tree = new LlvmDominatorTree(function);
            var metrics = new Dictionary<string, ulong>();
            var cache = new LlvmDomConditionCache(metrics);
            Program.Require(LlvmKnownBitsAnalysis.Compute(value, context: add, tree: tree).IsUnknown,
                "Unregistered argument acquired a branch condition.");
            cache.RegisterBranch(branch);
            cache.RegisterBranch(branch);
            Program.Require(cache.ConditionsFor(value).Count == 1, "Repeated branch registration duplicated a source cache entry.");
            var known = LlvmKnownBitsAnalysis.Compute(value, context: add, tree: tree, conditions: cache);
            Program.Require(known.IsConstant && known.One == new LlvmAPInt(8, 7), "Dominating true edge did not refine known bits.");
            known = LlvmKnownBitsAnalysis.Compute(add, context: add, tree: tree, conditions: cache);
            Program.Require(known.IsConstant && known.One == new LlvmAPInt(8, 8), "Recursive known bits dropped the condition cache.");
            Program.Require(LlvmInstructionSimplify.SimplifyInstruction(compareAgain, tree, conditions: cache) is LlvmConstantInt { IsOne: true },
                "InstructionSimplify did not consume its real cached query.");
            Program.Require(LlvmKnownBitsAnalysis.IsKnownNonEqual(value, LlvmConstantInt.Get(integer, 7), context: no.GetTerminator(),
                tree: tree, conditions: cache), "Dominating false edge did not imply non-equality.");
            cache.RemoveValue(value);
            Program.Require(cache.ConditionsFor(value).Count == 0 && LlvmKnownBitsAnalysis.Compute(value,
                context: add, tree: tree, conditions: cache).IsUnknown, "Explicit cache removal retained facts.");
            cache.RegisterBranch(branch);
            Program.Require(LlvmKnownBitsAnalysis.Compute(value, context: entry.GetTerminator(), tree: tree, conditions: cache).IsUnknown,
                "Condition was applied before its branch edge.");
            Program.Require(metrics.GetValueOrDefault("dom_condition_cache.queries.hit") != 0
                && metrics.GetValueOrDefault("dom_condition_cache.queries.miss") != 0
                && metrics.GetValueOrDefault("dom_condition_cache.values.removed") == 1, "Cache work observations were not recorded.");
            checks += 9;
        }

        void PowerFacts()
        {
            foreach (bool inverse in new[] { false, true })
            {
                var context = new LlvmContext();
                var module = new LlvmModule("dom-condition-power", context);
                var integer = LlvmIntegerType.Get(context, 8);
                var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer]), LlvmLinkage.InternalLinkage, "guarded-power", module);
                var entry = new LlvmBasicBlock(context, "entry", function);
                var yes = new LlvmBasicBlock(context, "yes", function);
                var no = new LlvmBasicBlock(context, "no", function);
                var value = function.Arguments[0];
                var pop = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [value], module);
                entry.Append(pop);
                var comparison = new LlvmICmpInstruction(inverse ? LlvmCmpPredicate.ICmpNe : LlvmCmpPredicate.ICmpEq,
                    pop, LlvmConstantInt.Get(integer, 1));
                entry.Append(comparison);
                var branch = new LlvmCondBrInstruction(comparison, yes, no);
                entry.Append(branch);
                yes.Append(new LlvmReturnInstruction(context, value));
                no.Append(new LlvmReturnInstruction(context, value));
                var cache = new LlvmDomConditionCache();
                cache.RegisterBranch(branch);
                var tree = new LlvmDominatorTree(function);
                var powerBlock = inverse ? no : yes;
                var otherBlock = inverse ? yes : no;
                Program.Require(LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, context: powerBlock.GetTerminator(),
                    tree: tree, conditions: cache), "Cached ctpop edge did not imply power-of-two.");
                Program.Require(!LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, context: otherBlock.GetTerminator(),
                    tree: tree, conditions: cache), "Opposite ctpop edge invented a power-of-two fact.");
                checks += 2;
            }
        }

        void FloatingFacts()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("dom-condition-float", context);
            var floating = LlvmType.GetDoubleTy(context);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(floating, [floating]), LlvmLinkage.InternalLinkage, "guarded-float", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var yes = new LlvmBasicBlock(context, "yes", function);
            var no = new LlvmBasicBlock(context, "no", function);
            var value = function.Arguments[0];
            var comparison = new LlvmFCmpInstruction(LlvmCmpPredicate.FCmpOgt, value, LlvmConstantFP.Get(floating, 0));
            entry.Append(comparison);
            var branch = new LlvmCondBrInstruction(comparison, yes, no);
            entry.Append(branch);
            yes.Append(new LlvmReturnInstruction(context, value));
            no.Append(new LlvmReturnInstruction(context, value));
            var cache = new LlvmDomConditionCache();
            cache.RegisterBranch(branch);
            var tree = new LlvmDominatorTree(function);
            var known = LlvmKnownFPClasses.Compute(value, contextInstruction: yes.GetTerminator(), tree: tree, conditions: cache);
            Program.Require(known.IsKnownNeverNaN() && known.IsKnownNever(LlvmFPClassTest.Zero | LlvmFPClassTest.Negative),
                "Ordered positive branch did not restrict floating classes.");
            known = LlvmKnownFPClasses.Compute(value, contextInstruction: no.GetTerminator(), tree: tree, conditions: cache);
            Program.Require(!known.IsKnownNeverNaN(), "False ordered branch incorrectly removed NaNs.");
            checks += 2;
        }

        void DuplicateEdge()
        {
            var context = new LlvmContext();
            var module = new LlvmModule("dom-condition-parallel", context);
            var integer = LlvmIntegerType.Get(context, 8);
            var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [integer]), LlvmLinkage.InternalLinkage, "parallel", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var exit = new LlvmBasicBlock(context, "exit", function);
            var value = function.Arguments[0];
            var comparison = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpEq, value, LlvmConstantInt.Get(integer, 7));
            entry.Append(comparison);
            var branch = new LlvmCondBrInstruction(comparison, exit, exit);
            entry.Append(branch);
            exit.Append(new LlvmReturnInstruction(context, value));
            var cache = new LlvmDomConditionCache();
            cache.RegisterBranch(branch);
            Program.Require(LlvmKnownBitsAnalysis.Compute(value, context: exit.GetTerminator(),
                tree: new(function), conditions: cache).IsUnknown, "Parallel edges were mistaken for a single dominating outcome.");
            ++checks;
        }
    }
}
