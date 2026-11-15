using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class PtrUseVisitorProperties
{
    internal static int Run()
    {
        int checks = 0;
        var context = new LlvmContext();
        var i8 = LlvmType.GetInt8Ty(context); var i32 = LlvmType.GetInt32Ty(context); var i64 = LlvmType.GetInt64Ty(context);
        var pointer = LlvmType.GetPointerTy(context);
        var module = new LlvmModule("pointer-use-visitor", context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [i64]), LlvmLinkage.InternalLinkage, "offsets", module);
        var entry = new LlvmBasicBlock(context, "entry", function);
        var pair = LlvmStructType.Get(context, [i8, LlvmType.GetDoubleTy(context)]);
        var alloca = new LlvmAllocaInstruction(pair, LlvmConstantInt.Get(i64, 1));
        var field = new LlvmGetElementPtrInstruction(pair, alloca, [LlvmConstantInt.Get(i64, 0), LlvmConstantInt.Get(i32, 1)]);
        var byteOffset = new LlvmGetElementPtrInstruction(i8, field, [LlvmConstantInt.Get(i64, 3)]);
        var fixedLoad = new LlvmLoadInstruction(i8, byteOffset);
        var varying = new LlvmGetElementPtrInstruction(i8, alloca, [function.Arguments[0]]);
        var varyingLoad = new LlvmLoadInstruction(i8, varying);
        entry.Append(alloca); entry.Append(field); entry.Append(byteOffset); entry.Append(fixedLoad);
        entry.Append(varying); entry.Append(varyingLoad); entry.Append(new LlvmReturnInstruction(context));
        var metrics = new Dictionary<string, ulong>();
        var visitor = new RecordingVisitor(module.DataLayout, metrics);
        var info = visitor.VisitPtr(alloca);
        Require(!info.IsAborted && !info.IsEscaped, "Pointer-use traversal escaped simple derived loads.");
        var fixedVisit = visitor.Visits[fixedLoad.GetOperandUse(0)];
        Require(fixedVisit.Known && fixedVisit.Offset.GetZExtValue() == 11,
            "Pointer-use offsets did not combine physical struct layout and byte GEP.");
        Require(!visitor.Visits[varyingLoad.GetOperandUse(0)].Known,
            "Variable pointer offset gained a fabricated constant fact.");
        Require(metrics.GetValueOrDefault("pointer_uses.unknown_gep") == 1 && metrics.GetValueOrDefault("pointer_uses.constant_gep") == 2,
            "Pointer-use diagnostics lost actual offset decisions.");

        var store = new LlvmStoreInstruction(alloca, alloca);
        entry.InsertBefore(store, entry.GetTerminator());
        var stored = new RecordingVisitor(module.DataLayout);
        var storedInfo = stored.VisitPtr(alloca);
        Require(ReferenceEquals(storedInfo.EscapingInstruction, store) && !storedInfo.IsAborted,
            "PtrUseVisitor did not retain its source pointer-store escape outcome.");
        Require(stored.Visits.ContainsKey(store.GetOperandUse(0)) && stored.Visits.ContainsKey(store.GetOperandUse(1)),
            "Pointer-use deduplication collapsed two distinct uses of one store instruction.");

        var cyclicFunction = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "cycles", module);
        var cycleEntry = new LlvmBasicBlock(context, "entry", cyclicFunction);
        var block = new LlvmBasicBlock(context, "block", cyclicFunction);
        var cycleExit = new LlvmBasicBlock(context, "exit", cyclicFunction);
        var cycleAlloca = new LlvmAllocaInstruction(i8, LlvmConstantInt.Get(i64, 4));
        cycleEntry.Append(cycleAlloca); cycleEntry.Append(new LlvmUncondBrInstruction(block));
        var phi = new LlvmPhiInstruction(pointer, 2);
        phi.AddIncoming(cycleAlloca, cycleEntry); phi.AddIncoming(phi, block);
        var cycleLoad = new LlvmLoadInstruction(i8, phi);
        block.Append(phi); block.Append(cycleLoad);
        block.Append(new LlvmCondBrInstruction(LlvmConstantInt.GetTrue(context), block, cycleExit));
        cycleExit.Append(new LlvmReturnInstruction(context));
        var cyclic = new RecordingVisitor(module.DataLayout);
        cyclic.VisitPtr(cycleAlloca);
        Require(cyclic.Visits.Count == 3 && cyclic.Visits.ContainsKey(cycleLoad.GetOperandUse(0)),
            "Use-based cycle handling revisited a use or lost a derived load.");

        var narrowLayout = new LlvmDataLayout("e-p:64:64:64:32");
        var narrowFunction = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "index-width", module);
        var narrowEntry = new LlvmBasicBlock(context, "entry", narrowFunction);
        var root = new LlvmAllocaInstruction(i8, LlvmConstantInt.Get(i64, 8));
        var negative = new LlvmGetElementPtrInstruction(i8, root, [LlvmConstantInt.Get(i64, ulong.MaxValue)]);
        var plusTwo = new LlvmGetElementPtrInstruction(i8, negative, [LlvmConstantInt.Get(i64, 2)]);
        var finalLoad = new LlvmLoadInstruction(i8, plusTwo);
        narrowEntry.Append(root); narrowEntry.Append(negative); narrowEntry.Append(plusTwo); narrowEntry.Append(finalLoad);
        narrowEntry.Append(new LlvmReturnInstruction(context));
        var narrow = new RecordingVisitor(narrowLayout);
        narrow.VisitPtr(root);
        var result = narrow.Visits[finalLoad.GetOperandUse(0)];
        Require(result.Known && result.Offset.BitWidth == 32 && result.Offset.GetZExtValue() == 1,
            "Pointer-use APInt offsets ignored index width or signed modular addition.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private sealed class RecordingVisitor(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
        : LlvmPtrUseVisitor(layout, metrics)
    {
        internal readonly Dictionary<LlvmUse, (bool Known, LlvmAPInt Offset)> Visits = [];
        protected override void Visit(LlvmInstruction instruction)
        {
            Visits.Add(CurrentUse, (IsOffsetKnown, Offset));
            base.Visit(instruction);
        }
        protected override void VisitPhi(LlvmPhiInstruction instruction) => EnqueueUsers(instruction);
    }
}
