using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SimplifyCfgCoreProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        LlvmModule module = new("cfg-core", context);
        LlvmIntegerType boolean = LlvmType.GetInt1Ty(context);
        LlvmIntegerType integer = LlvmType.GetInt64Ty(context);
        LlvmPointerType pointer = LlvmPointerType.Get(context);
        var target = new LlvmTargetTransformInfo(module.DataLayout);
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [boolean, boolean]),
                LlvmLinkage.InternalLinkage, "nested", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            var left = new LlvmBasicBlock(context, "left", function);
            var right = new LlvmBasicBlock(context, "right", function);
            var yes = new LlvmBasicBlock(context, "yes", function);
            var no = new LlvmBasicBlock(context, "no", function);
            entry.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
            left.Append(new LlvmCondBrInstruction(function.Arguments[1], yes, no));
            right.Append(new LlvmCondBrInstruction(function.Arguments[1], no, yes));
            yes.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(integer, 1)));
            no.Append(new LlvmReturnInstruction(context, LlvmConstantInt.Get(integer, 2)));
            var tree = new LlvmDominatorTree(function);
            var updater = new LlvmDomTreeUpdater(tree);
            Program.Require(LlvmSimplifyCfg.SimplifyCFG(entry, target, updater,
                new() { SpeculateBlocks = false }, tuning: new() { HoistCommon = false }), "Nested swapped branches must fold.");
            Program.Require(entry.GetTerminator() is LlvmCondBrInstruction { Condition: LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } } folded &&
                folded.GetSuccessor(0) == no && folded.GetSuccessor(1) == yes, "Nested branch folding must retain source XOR successor orientation.");
        }
        foreach (bool nullPointerIsValid in new[] { false, true })
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [boolean, pointer]),
                LlvmLinkage.InternalLinkage, "undefined-phi", module);
            function.NullPointerIsValid = nullPointerIsValid;
            var entry = new LlvmBasicBlock(context, "entry", function);
            var nullPath = new LlvmBasicBlock(context, "null", function);
            var livePath = new LlvmBasicBlock(context, "live", function);
            var merge = new LlvmBasicBlock(context, "merge", function);
            entry.Append(new LlvmCondBrInstruction(function.Arguments[0], nullPath, livePath));
            nullPath.Append(new LlvmUncondBrInstruction(merge));
            livePath.Append(new LlvmUncondBrInstruction(merge));
            var phi = new LlvmPhiInstruction(pointer);
            phi.AddIncoming(LlvmConstantPointerNull.Get(pointer), nullPath);
            phi.AddIncoming(function.Arguments[1], livePath);
            merge.Append(phi);
            var load = new LlvmLoadInstruction(integer, phi);
            merge.Append(load); merge.Append(new LlvmReturnInstruction(context, load));
            bool changed = LlvmSimplifyCfg.SimplifyCFG(merge, target, options: new() { SpeculateBlocks = false });
            Program.Require(changed != nullPointerIsValid,
                "The source null-pointer-is-valid attribute must determine whether the null load witnesses UB.");
            Program.Require(nullPointerIsValid ? nullPath.GetTerminator() is LlvmUncondBrInstruction :
                nullPath.GetTerminator() is LlvmUnreachableInstruction,
                "Only an undefined null incoming path becomes unreachable, without a runtime guard.");
        }
        {
            var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [pointer]),
                LlvmLinkage.InternalLinkage, "unreachable-store", module);
            var entry = new LlvmBasicBlock(context, "entry", function);
            entry.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 7), function.Arguments[0]));
            entry.Append(new LlvmUnreachableInstruction(context));
            Program.Require(LlvmSimplifyCfg.SimplifyCFG(entry, target), "A guaranteed-transfer suffix before unreachable is removable even with effects.");
            Program.Require(entry.Instructions.Count == 1 && entry.FirstInstruction is LlvmUnreachableInstruction,
                "Unreachable suffix deletion must remove the actual store.");
        }
        return 8;
    }
}
