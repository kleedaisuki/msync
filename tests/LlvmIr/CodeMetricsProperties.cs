using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CodeMetricsProperties
{
    internal static int Run()
    {
        int checks = 0;
        Costs();
        Attributes();
        Ephemeral(false);
        Ephemeral(true);
        LoopSeeds();
        PhiUseBlock();
        return checks;

        void Require(bool condition, string message)
        {
            Program.Require(condition, message);
            ++checks;
        }

        void Costs()
        {
            Fixture f = new();
            var removed = new LlvmBasicBlock(f.Context, "removed", f.Function);
            removed.RemoveFromParent();
            var exit = new LlvmBasicBlock(f.Context, "exit", f.Function);
            var constantSize = LlvmConstantInt.Get(f.Integer, 1);
            var fixedAllocation = new LlvmAllocaInstruction(f.Integer, constantSize);
            var variableAllocation = new LlvmAllocaInstruction(f.Integer, f.Argument);
            f.Entry.Append(fixedAllocation);
            f.Entry.Append(variableAllocation);
            var add = new LlvmBinaryOperator(LlvmOpcode.Add, f.Argument, constantSize);
            var divide = new LlvmBinaryOperator(LlvmOpcode.SDiv, add, constantSize);
            f.Entry.Append(add); f.Entry.Append(divide);
            var callee = LlvmFunction.Create(f.Function.FunctionType, LlvmLinkage.InternalLinkage, "callee", f.Module);
            var call = new LlvmCallInstruction(callee.FunctionType, callee, [divide]);
            f.Entry.Append(call); f.Entry.Append(new LlvmReturnInstruction(f.Context, call));
            exit.Append(new LlvmReturnInstruction(f.Context, constantSize));
            var metrics = new LlvmCodeMetrics();
            metrics.AnalyzeBasicBlock(f.Entry, f.Target, new HashSet<LlvmValue>());
            Require(metrics.NumInsts.IsValid && metrics.NumInsts.Value == 9,
                "CodeMetrics must use generic source CodeSize costs: static alloca 0, dynamic 1, add 1, division 4, call 2, return 1.");
            Require(metrics.UsesDynamicAlloca && fixedAllocation.IsStaticAlloca && !variableAllocation.IsStaticAlloca,
                "Only a constant-size entry-block alloca without inalloca is static.");
            Require(metrics.NumInlineCandidates == 1 && metrics.NumRets == 1,
                "The source metrics count an internal single-live-use call and the analyzed return.");
            metrics.AnalyzeBasicBlock(exit, f.Target, new HashSet<LlvmValue>());
            Require(metrics.NumInsts.Value == 10 && metrics.NumRets == 2 && metrics.NumBBInsts.Count == 3 &&
                metrics.NumBBInsts[(int)f.Entry.Number].Value == 9 && metrics.NumBBInsts[1].Value == 0 &&
                metrics.NumBBInsts[(int)exit.Number].Value == 1,
                "Per-block source costs preserve function block-number holes and accumulate across analyzed blocks.");
            var aggregate = LlvmConstantStruct.Get(LlvmStructType.Get(f.Context, [callee.Type]), [callee]);
            Require(callee.HasOneLiveUse,
                "A dead constant aggregate user must not hide a source single-live-use function.");
            _ = new LlvmGlobalVariable(f.Module, aggregate.Type, true, LlvmLinkage.InternalLinkage, aggregate, "root");
            Require(!callee.HasOneLiveUse,
                "An aggregate referenced by a global is a live constant use, not dead constant storage.");
            fixedAllocation.InAlloca = true;
            Require(!fixedAllocation.IsStaticAlloca, "Source inalloca storage is not a static alloca.");
        }

        void Attributes()
        {
            Fixture f = new();
            var callee = LlvmFunction.Create(f.Function.FunctionType, LlvmLinkage.InternalLinkage, "callee", f.Module);
            callee.NoInline = callee.NoDuplicate = callee.Convergent = callee.ReturnsTwice = callee.NoMerge = true;
            var call = new LlvmCallInstruction(callee.FunctionType, callee, [f.Argument]);
            f.Entry.Append(call); f.Entry.Append(new LlvmReturnInstruction(f.Context, call));
            var metrics = new LlvmCodeMetrics();
            metrics.AnalyzeBasicBlock(f.Entry, f.Target, new HashSet<LlvmValue>());
            Require(metrics.NotDuplicatable && metrics.NumInlineCandidates == 0 && metrics.Convergence == LlvmConvergenceKind.Uncontrolled,
                "CodeMetrics must read actual callee noinline, noduplicate and convergent attributes.");
            Require(call.IsNoInline && call.CannotDuplicate && call.IsConvergent && call.HasReturnsTwice && call.CannotMerge && !call.NoInline,
                "Effective call flags include the callee without copying attributes into callsite storage.");
            call.NoInline = call.NoDuplicate = call.Convergent = call.ReturnsTwice = call.NoMerge = true;
            var clone = (LlvmCallInstruction)call.Clone();
            Require(clone.NoInline && clone.NoDuplicate && clone.Convergent && clone.ReturnsTwice && clone.NoMerge,
                "Cloning must preserve actual callsite attributes.");
            clone.DropAllReferences();
            f.Function.MinSize = true;
            Require(f.Function.HasMinSize && f.Function.HasOptSize && !f.Function.OptimizeForSize,
                "Source minsize implies optsize without rewriting the stored optimize-for-size attribute.");

            Fixture controlled = new();
            var convergent = LlvmFunction.Create(controlled.Function.FunctionType, LlvmLinkage.ExternalLinkage, "convergent", controlled.Module);
            convergent.Convergent = true;
            var token = LlvmUndefValue.Get(LlvmType.GetTokenTy(controlled.Context));
            var controlledCall = new LlvmCallInstruction(convergent.FunctionType, convergent, [controlled.Argument],
                [new LlvmOperandBundleDef("convergencectrl", [token])]);
            controlled.Entry.Append(controlledCall); controlled.Entry.Append(new LlvmReturnInstruction(controlled.Context, controlledCall));
            var controlledMetrics = new LlvmCodeMetrics();
            controlledMetrics.AnalyzeBasicBlock(controlled.Entry, controlled.Target, new HashSet<LlvmValue>());
            Require(controlledMetrics.Convergence == LlvmConvergenceKind.Controlled &&
                ReferenceEquals(controlledCall.GetConvergenceControlToken(), token),
                "A real convergencectrl bundle, not the callee spelling, establishes controlled convergence.");
        }

        void Ephemeral(bool sharedUse)
        {
            Fixture f = new();
            var one = LlvmConstantInt.Get(f.Integer, 1);
            var first = new LlvmBinaryOperator(LlvmOpcode.Add, f.Argument, one);
            var second = new LlvmBinaryOperator(LlvmOpcode.Add, first, one);
            var compare = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSgt, second, one);
            var assumeType = LlvmFunctionType.Get(LlvmType.GetVoidTy(f.Context), [compare.Type]);
            var assume = LlvmFunction.Create(assumeType, LlvmLinkage.ExternalLinkage, "llvm.assume", f.Module);
            var seed = new LlvmCallInstruction(assumeType, assume, [compare]);
            f.Entry.Append(first); f.Entry.Append(second); f.Entry.Append(compare); f.Entry.Append(seed);
            f.Entry.Append(new LlvmReturnInstruction(f.Context, sharedUse ? first : one));
            HashSet<LlvmValue> ephemeral = [];
            Dictionary<string, ulong> work = [];
            LlvmCodeMetrics.CollectEphemeralValues(f.Function, [seed, null], ephemeral, work);
            Require(ephemeral.Contains(seed) && ephemeral.Contains(compare) && ephemeral.Contains(second) &&
                ephemeral.Contains(first) != sharedUse,
                "The source ephemeral worklist follows all-ephemeral users and stops at a shared ordinary use.");
            Require(!ephemeral.Contains(f.Argument) && work["code_metrics.ephemeral.seeds"] == 1 &&
                work["code_metrics.ephemeral.values"] == (sharedUse ? 2UL : 3UL),
                "Ephemeral closure includes instructions only and reports actual candidate decisions.");
        }

        void LoopSeeds()
        {
            Fixture f = new();
            var header = new LlvmBasicBlock(f.Context, "header", f.Function);
            var exit = new LlvmBasicBlock(f.Context, "exit", f.Function);
            var value = new LlvmBinaryOperator(LlvmOpcode.Add, f.Argument, LlvmConstantInt.Get(f.Integer, 1));
            f.Entry.Append(value); f.Entry.Append(new LlvmUncondBrInstruction(header));
            var compare = new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSgt, value, f.Argument);
            var type = LlvmFunctionType.Get(LlvmType.GetVoidTy(f.Context), [compare.Type]);
            var assume = LlvmFunction.Create(type, LlvmLinkage.ExternalLinkage, "llvm.assume", f.Module);
            var outside = new LlvmCallInstruction(type, assume, [LlvmConstantInt.GetTrue(f.Context)]);
            f.Entry.InsertBefore(outside, f.Entry.GetTerminator());
            var inside = new LlvmCallInstruction(type, assume, [compare]);
            header.Append(compare); header.Append(inside);
            header.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(f.Context), header, exit));
            exit.Append(new LlvmReturnInstruction(f.Context, LlvmConstantInt.Get(f.Integer, 0)));
            var tree = new LlvmDominatorTree(f.Function);
            var loop = new LlvmLoopInfo(f.Function, tree).GetLoopFor(header)!;
            HashSet<LlvmValue> ephemeral = [];
            LlvmCodeMetrics.CollectEphemeralValues(loop, [outside, inside], ephemeral);
            Require(ephemeral.Contains(inside) && !ephemeral.Contains(outside),
                "Loop ephemeral collection filters assumption seed sites outside the loop.");
            HashSet<LlvmValue> noSeeds = [];
            LlvmCodeMetrics.CollectEphemeralValues(loop, null, noSeeds);
            Require(noSeeds.Count == 0, "No provided assumption seeds cannot manufacture ephemeral values.");
        }

        void PhiUseBlock()
        {
            Fixture f = new();
            var exit = new LlvmBasicBlock(f.Context, "exit", f.Function);
            var value = new LlvmBinaryOperator(LlvmOpcode.Add, f.Argument, LlvmConstantInt.Get(f.Integer, 1));
            f.Entry.Append(value); f.Entry.Append(new LlvmUncondBrInstruction(exit));
            var phi = new LlvmPhiInstruction(f.Integer, 1);
            phi.AddIncoming(value, f.Entry); exit.Append(phi); exit.Append(new LlvmReturnInstruction(f.Context, phi));
            Require(!value.IsUsedOutsideOfBlock(f.Entry),
                "A PHI uses an incoming value on its predecessor edge, not in the PHI's parent block.");
            phi.SetIncomingBlock(0, exit);
            Require(value.IsUsedOutsideOfBlock(f.Entry), "The actual incoming edge controls PHI outside-block use queries.");
        }
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmModule Module;
        internal readonly LlvmIntegerType Integer;
        internal readonly LlvmFunction Function;
        internal readonly LlvmBasicBlock Entry;
        internal readonly LlvmTargetTransformInfo Target;
        internal LlvmValue Argument => Function.Arguments[0];

        internal Fixture()
        {
            Module = new("code-metrics", Context);
            Integer = LlvmType.GetInt32Ty(Context);
            Function = LlvmFunction.Create(LlvmFunctionType.Get(Integer, [Integer]), LlvmLinkage.InternalLinkage, "main", Module);
            Entry = new LlvmBasicBlock(Context, "entry", Function);
            Target = new LlvmTargetTransformInfo(Module.DataLayout);
        }
    }
}
