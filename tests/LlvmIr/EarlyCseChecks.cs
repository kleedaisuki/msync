using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class EarlyCseChecks
{
    internal static int Run()
    {
        int checks = 0;
        LlvmContext context = new();
        LlvmModule module = new("early-cse", context);
        var integer = LlvmType.GetInt64Ty(context);
        var pointer = LlvmPointerType.Get(context);
        LlvmFunction Function(string name, params LlvmType[] arguments) =>
            LlvmFunction.Create(LlvmFunctionType.Get(integer, arguments), LlvmLinkage.InternalLinkage, name, module);

        var arithmetic = Function("arithmetic", integer, integer);
        var block = new LlvmBasicBlock(context, "entry", arithmetic);
        var a = new LlvmBinaryOperator(LlvmOpcode.Add, arithmetic.Arguments[0], arithmetic.Arguments[1]);
        var b = new LlvmBinaryOperator(LlvmOpcode.Add, arithmetic.Arguments[1], arithmetic.Arguments[0]);
        var result = new LlvmBinaryOperator(LlvmOpcode.Mul, a, b);
        block.Append(a); block.Append(b); block.Append(result); block.Append(new LlvmReturnInstruction(context, result));
        Program.Require(LlvmEarlyCse.Run(arithmetic) && b.Parent is null && result.Left == a && result.Right == a,
            "EarlyCSE must recognize commuted arithmetic without constructing new instructions.");
        ++checks;

        var forwarding = Function("forward", pointer, integer);
        block = new(context, "entry", forwarding);
        var store = new LlvmStoreInstruction(forwarding.Arguments[1], forwarding.Arguments[0]);
        var load = new LlvmLoadInstruction(integer, forwarding.Arguments[0]);
        var ret = new LlvmReturnInstruction(context, load);
        block.Append(store); block.Append(load); block.Append(ret);
        Program.Require(LlvmEarlyCse.Run(forwarding) && load.Parent is null && ret.ReturnValue == forwarding.Arguments[1],
            "EarlyCSE must forward the latest exact-address store into the load.");
        ++checks;

        var writeback = Function("writeback", pointer);
        block = new(context, "entry", writeback);
        load = new(integer, writeback.Arguments[0]);
        store = new(load, writeback.Arguments[0]);
        block.Append(load); block.Append(store); block.Append(new LlvmReturnInstruction(context, load));
        Program.Require(LlvmEarlyCse.Run(writeback) && store.Parent is null && load.Parent == block,
            "Load/store writeback elimination must preserve the original load.");
        ++checks;

        var barrier = Function("barrier", pointer, pointer, integer);
        block = new(context, "entry", barrier);
        block.Append(new LlvmStoreInstruction(barrier.Arguments[2], barrier.Arguments[0]));
        block.Append(new LlvmStoreInstruction(LlvmConstantInt.Get(integer, 7), barrier.Arguments[1]));
        load = new(integer, barrier.Arguments[0]);
        block.Append(load); block.Append(new LlvmReturnInstruction(context, load));
        LlvmEarlyCse.Run(barrier);
        Program.Require(load.Parent == block, "Non-MSSA EarlyCSE must kill memory availability on any intervening write.");
        ++checks;

        var diamond = Function("diamond", integer, integer, LlvmType.GetInt1Ty(context));
        var entry = new LlvmBasicBlock(context, "entry", diamond);
        var left = new LlvmBasicBlock(context, "left", diamond);
        var right = new LlvmBasicBlock(context, "right", diamond);
        var merge = new LlvmBasicBlock(context, "merge", diamond);
        entry.Append(new LlvmCondBrInstruction(diamond.Arguments[2], left, right));
        a = new(LlvmOpcode.Add, diamond.Arguments[0], diamond.Arguments[1]);
        b = new(LlvmOpcode.Add, diamond.Arguments[0], diamond.Arguments[1]);
        left.Append(a); left.Append(new LlvmUncondBrInstruction(merge));
        right.Append(b); right.Append(new LlvmUncondBrInstruction(merge));
        var phi = new LlvmPhiInstruction(integer); phi.AddIncoming(a, left); phi.AddIncoming(b, right);
        merge.Append(phi); merge.Append(new LlvmReturnInstruction(context, phi));
        LlvmEarlyCse.Run(diamond);
        Program.Require(a.Parent == left && b.Parent == right,
            "A value available in one dominator-tree sibling must not leak into another.");
        ++checks;
        return checks;
    }
}
