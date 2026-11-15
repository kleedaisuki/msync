using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class UnreachableBlockProperties
{
    internal static int Run()
    {
        DeadCycle();
        FoldConstantBranch();
        InvalidStore();
        SkipInstructionFolds();
        ValidAndVolatileNullStores();
        InvalidCallee();
        NoReturnCall();
        MustTailNoReturnCall();
        return 8;
    }

    private static void DeadCycle()
    {
        Fixture f = new();
        f.Entry.Append(new LlvmUncondBrInstruction(f.Exit));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        var first = new LlvmBasicBlock(f.Context, "dead.first", f.Function);
        var second = new LlvmBasicBlock(f.Context, "dead.second", f.Function);
        first.Append(new LlvmUncondBrInstruction(second));
        second.Append(new LlvmUncondBrInstruction(first));
        var tree = new LlvmDominatorTree(f.Function);
        using var updater = new LlvmDomTreeUpdater(tree);
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function, updater) &&
            f.Function.BasicBlocks.Count == 2 && first.Parent is null && second.Parent is null,
            "Unreachable cyclic blocks must be removed together, not retained by predecessor counts.");
        Program.Require(tree.GetNode(f.Exit)?.IDom?.Block == f.Entry,
            "Deleting a dead cycle must preserve the live dominator tree.");
    }

    private static void FoldConstantBranch()
    {
        Fixture f = new();
        var removed = new LlvmBasicBlock(f.Context, "untaken", f.Function);
        f.Entry.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(f.Context), f.Exit, removed));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        removed.Append(new LlvmReturnInstruction(f.Context));
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function) && removed.Parent is null &&
            f.Entry.GetTerminator() is LlvmUncondBrInstruction { Destination: var destination } && destination == f.Exit,
            "The initial reachability scan must fold constant terminators before discovering successors.");
    }

    private static void InvalidStore()
    {
        Fixture f = new();
        var store = f.StoreToNull();
        f.Entry.Append(new LlvmUncondBrInstruction(f.Exit));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        var tree = new LlvmDominatorTree(f.Function);
        using var updater = new LlvmDomTreeUpdater(tree);
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function, updater) && store.Parent is null &&
            f.Function.BasicBlocks.Count == 1 && f.Entry.GetTerminator() is LlvmUnreachableInstruction,
            "A nonvolatile AS0 null store must become unreachable and disconnect its successor.");
    }

    private static void SkipInstructionFolds()
    {
        Fixture f = new();
        var store = f.StoreToNull();
        f.Entry.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(f.Context), f.Exit, f.Exit));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        Program.Require(!LlvmLocalCfg.RemoveUnreachableBlocks(f.Function, foldInstsToUnreachable: false) &&
            store.Parent == f.Entry && f.Entry.GetTerminator() is LlvmCondBrInstruction,
            "The source second-iteration option must skip both instruction folding and ConstantFoldTerminator.");
    }

    private static void ValidAndVolatileNullStores()
    {
        for (int kind = 0; kind < 3; ++kind)
        {
            Fixture f = new();
            f.Function.NullPointerIsValid = kind == 0;
            var store = f.StoreToNull(isVolatile: kind == 1, addressSpace: kind == 2 ? 1U : 0U);
            f.Entry.Append(new LlvmUncondBrInstruction(f.Exit));
            f.Exit.Append(new LlvmReturnInstruction(f.Context));
            Program.Require(!LlvmLocalCfg.RemoveUnreachableBlocks(f.Function) && store.Parent == f.Entry && f.Exit.Parent == f.Function,
                "Null-valid function attributes, volatile stores and nonzero address spaces must keep their source behavior.");
        }
    }

    private static void InvalidCallee()
    {
        Fixture f = new();
        var signature = LlvmFunctionType.Get(LlvmType.GetVoidTy(f.Context), []);
        var call = new LlvmCallInstruction(signature, LlvmConstantPointerNull.Get(LlvmPointerType.Get(f.Context)), []);
        f.Entry.Append(call);
        f.Entry.Append(new LlvmUncondBrInstruction(f.Exit));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function) && call.Parent is null &&
            f.Entry.GetTerminator() is LlvmUnreachableInstruction && f.Exit.Parent is null,
            "An undefined null callee must cut off the block from the call, not just drop dead successors.");
    }

    private static void NoReturnCall()
    {
        Fixture f = new();
        var signature = LlvmFunctionType.Get(LlvmType.GetVoidTy(f.Context), []);
        var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, "never_returns", f.Module);
        target.NoReturn = true;
        var call = new LlvmCallInstruction(signature, target, []);
        f.Entry.Append(call);
        var store = f.StoreToNull();
        f.Entry.Append(new LlvmUncondBrInstruction(f.Exit));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function) && call.Parent == f.Entry &&
            call.Next is LlvmUnreachableInstruction && store.Parent is null && f.Exit.Parent is null,
            "A noreturn call must remain while only its following instructions become unreachable.");
        Program.Require(!LlvmLocalCfg.RemoveUnreachableBlocks(f.Function),
            "An existing unreachable immediately after noreturn must not be rewritten repeatedly.");
    }

    private static void MustTailNoReturnCall()
    {
        Fixture f = new();
        var signature = LlvmFunctionType.Get(LlvmType.GetVoidTy(f.Context), []);
        var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, "musttail_never_returns", f.Module);
        target.NoReturn = true;
        var call = new LlvmCallInstruction(signature, target, []) { TailCallKind = LlvmTailCallKind.MustTail };
        f.Entry.Append(call);
        f.Entry.Append(new LlvmReturnInstruction(f.Context));
        f.Exit.Append(new LlvmReturnInstruction(f.Context));
        Program.Require(LlvmLocalCfg.RemoveUnreachableBlocks(f.Function) && call.Next is LlvmReturnInstruction && f.Exit.Parent is null,
            "Noreturn musttail calls must retain their mandatory following return.");
    }

    private sealed class Fixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmModule Module;
        internal readonly LlvmFunction Function;
        internal readonly LlvmBasicBlock Entry;
        internal readonly LlvmBasicBlock Exit;
        internal Fixture()
        {
            Module = new("unreachable-properties", Context);
            Function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(Context), []),
                LlvmLinkage.InternalLinkage, "subject", Module);
            Entry = new(Context, "entry", Function);
            Exit = new(Context, "exit", Function);
        }
        internal LlvmStoreInstruction StoreToNull(bool isVolatile = false, uint addressSpace = 0)
        {
            var store = new LlvmStoreInstruction(LlvmConstantInt.Get(LlvmType.GetInt32Ty(Context), 7),
                LlvmConstantPointerNull.Get(LlvmPointerType.Get(Context, addressSpace)), isVolatile: isVolatile);
            Entry.Append(store);
            return store;
        }
    }
}
