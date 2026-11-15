using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class SroaAggregateSpeculationProperties
{
    internal static int Run()
    {
        AggregateLeaves(false);
        AggregateLeaves(true);
        VolatileAggregate();
        EmptyAggregate();
        UnfoldSelectIndex();
        UnfoldPhiIndex();
        return 6;
    }

    private static void AggregateLeaves(bool packed)
    {
        LlvmContext context = new();
        LlvmModule module = new("sroa-aggregate-leaves", context);
        LlvmType type = LlvmStructType.Get(context,
            [LlvmType.GetInt8Ty(context), LlvmArrayType.Get(LlvmType.GetInt32Ty(context), 2), LlvmType.GetDoubleTy(context)], packed);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]),
            LlvmLinkage.InternalLinkage, "subject", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        var alloca = new LlvmAllocaInstruction(type, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1), alignment: new(16));
        entry.Append(alloca);
        var store = new LlvmStoreInstruction(function.Arguments[0], alloca, new(16));
        entry.Append(store);
        Escape(module, entry, alloca);
        var load = new LlvmLoadInstruction(type, alloca, new(16));
        entry.Append(load);
        entry.Append(new LlvmReturnInstruction(context, load));
        LlvmSroaResult result = LlvmSroa.Run(function, new LlvmDominatorTree(function));
        var loads = entry.Instructions.OfType<LlvmLoadInstruction>().ToArray();
        var stores = entry.Instructions.OfType<LlvmStoreInstruction>().ToArray();
        Program.Require(result.Changed && !result.CFGChanged && load.Parent is null && store.Parent is null &&
            loads.Length == 4 && stores.Length == 4 && loads.All(item => item.Type.IsSingleValueType()) &&
            stores.All(item => item.ValueOperand.Type.IsSingleValueType()),
            "FCA rewriting must recursively split all nested struct/array leaves even when the alloca subsequently escapes.");
        ulong[] expected = packed ? [16, 1, 1, 1] : [16, 4, 8, 16];
        Program.Require(loads.Select(item => item.Alignment.Value).SequenceEqual(expected) &&
            stores.Select(item => item.Alignment.Value).SequenceEqual(expected),
            "Split memory alignment must use DataLayout's packed/unpacked byte offsets and commonAlignment.");
    }

    private static void VolatileAggregate()
    {
        LlvmContext context = new();
        LlvmModule module = new("sroa-volatile-aggregate", context);
        var type = LlvmStructType.Get(context, [LlvmType.GetInt32Ty(context), LlvmType.GetInt32Ty(context)]);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type]),
            LlvmLinkage.InternalLinkage, "subject", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        var alloca = new LlvmAllocaInstruction(type, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1));
        entry.Append(alloca);
        var store = new LlvmStoreInstruction(function.Arguments[0], alloca, isVolatile: true);
        entry.Append(store);
        Escape(module, entry, alloca);
        var load = new LlvmLoadInstruction(type, alloca, isVolatile: true);
        entry.Append(load);
        entry.Append(new LlvmReturnInstruction(context, load));
        LlvmSroa.Run(function, new LlvmDominatorTree(function));
        Program.Require(store.Parent == entry && load.Parent == entry && store.IsVolatile && load.IsVolatile,
            "The source FCA visitor must not split volatile aggregate accesses into multiple operations.");
    }

    private static void EmptyAggregate()
    {
        LlvmContext context = new();
        LlvmModule module = new("sroa-empty-aggregate", context);
        var type = LlvmStructType.Get(context, []);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(type, []),
            LlvmLinkage.InternalLinkage, "subject", module);
        LlvmBasicBlock entry = new(context, "entry", function);
        var alloca = new LlvmAllocaInstruction(type, LlvmConstantInt.Get(LlvmType.GetInt64Ty(context), 1));
        entry.Append(alloca);
        var load = new LlvmLoadInstruction(type, alloca);
        entry.Append(load);
        entry.Append(new LlvmReturnInstruction(context, load));
        Program.Require(!LlvmSroa.Run(function, new LlvmDominatorTree(function)).Changed && load.Parent == entry,
            "The source zero-size alloca exclusion happens before FCA rewriting and must not create artificial leaves.");
    }

    private static void UnfoldSelectIndex()
    {
        IndexFixture f = new();
        var index = new LlvmSelectInstruction(f.Function.Arguments[0], f.Zero, f.One);
        f.Entry.Append(index);
        var gep = new LlvmGetElementPtrInstruction(f.Array, f.Alloca, [f.Zero, index], LlvmGepNoWrapFlags.InBounds);
        f.Entry.Append(gep);
        var load = new LlvmLoadInstruction(f.I32, gep);
        f.Entry.Append(load);
        f.Entry.Append(new LlvmReturnInstruction(f.Context, load));
        LlvmSroaResult result = LlvmSroa.Run(f.Function, new LlvmDominatorTree(f.Function));
        Program.Require(result.Changed && gep.Parent is null && load.PointerOperand is LlvmSelectInstruction pointerSelect &&
            pointerSelect.TrueValue is LlvmGetElementPtrInstruction { HasAllConstantIndices: true } &&
            pointerSelect.FalseValue is LlvmGetElementPtrInstruction { HasAllConstantIndices: true },
            "FCA pointer traversal must distribute a single selected GEP index into a select of constant-index GEPs.");
    }

    private static void UnfoldPhiIndex()
    {
        IndexFixture f = new();
        LlvmBasicBlock left = new(f.Context, "left", f.Function), right = new(f.Context, "right", f.Function),
            join = new(f.Context, "join", f.Function);
        f.Entry.Append(new LlvmCondBrInstruction(f.Function.Arguments[0], left, right));
        left.Append(new LlvmUncondBrInstruction(join));
        right.Append(new LlvmUncondBrInstruction(join));
        var index = new LlvmPhiInstruction(f.I32, 2);
        index.AddIncoming(f.Zero, left); index.AddIncoming(f.One, right);
        join.Append(index);
        var gep = new LlvmGetElementPtrInstruction(f.Array, f.Alloca, [f.Zero, index], LlvmGepNoWrapFlags.InBounds);
        join.Append(gep);
        var load = new LlvmLoadInstruction(f.I32, gep);
        join.Append(load);
        join.Append(new LlvmReturnInstruction(f.Context, load));
        LlvmSroaResult result = LlvmSroa.Run(f.Function, new LlvmDominatorTree(f.Function));
        Program.Require(result.Changed && gep.Parent is null && load.PointerOperand is LlvmPhiInstruction pointerPhi &&
            pointerPhi.IncomingCount == 2 && pointerPhi.Operands.All(value => value is LlvmGetElementPtrInstruction
                { HasAllConstantIndices: true, Parent: var block } && block == f.Entry),
            "A GEP over a constant-valued PHI index must become pointer PHI inputs computed at the entry terminator.");
    }

    private static void Escape(LlvmModule module, LlvmBasicBlock block, LlvmValue pointer)
    {
        var signature = LlvmFunctionType.Get(LlvmType.GetVoidTy(module.Context), [pointer.Type]);
        var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, "escape", module);
        block.Append(new LlvmCallInstruction(signature, target, [pointer]));
    }

    private sealed class IndexFixture
    {
        internal readonly LlvmContext Context = new();
        internal readonly LlvmModule Module;
        internal readonly LlvmFunction Function;
        internal readonly LlvmBasicBlock Entry;
        internal readonly LlvmIntegerType I32;
        internal readonly LlvmArrayType Array;
        internal readonly LlvmAllocaInstruction Alloca;
        internal readonly LlvmConstantInt Zero, One;
        internal IndexFixture()
        {
            Module = new("sroa-index-distribution", Context);
            I32 = LlvmType.GetInt32Ty(Context);
            Array = LlvmArrayType.Get(I32, 2);
            Function = LlvmFunction.Create(LlvmFunctionType.Get(I32, [LlvmType.GetInt1Ty(Context)]),
                LlvmLinkage.InternalLinkage, "subject", Module);
            Entry = new(Context, "entry", Function);
            Zero = LlvmConstantInt.Get(I32, 0); One = LlvmConstantInt.Get(I32, 1);
            Alloca = new(Array, One);
            Entry.Append(Alloca);
            Escape(Module, Entry, Alloca);
        }
    }
}
