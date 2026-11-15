using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CycleInfoChecks
{
    internal static int Run()
    {
        LlvmContext context = new();
        LlvmModule module = new("cycle-info", context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context), [LlvmType.GetInt1Ty(context)]),
            LlvmLinkage.InternalLinkage, "irreducible", module);
        LlvmBasicBlock entry = new(context, "entry", function), a = new(context, "a", function),
            b = new(context, "b", function), exit = new(context, "exit", function), unreachable = new(context, "unreachable", function);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], a, b));
        a.Append(new LlvmCondBrInstruction(function.Arguments[0], b, exit));
        b.Append(new LlvmCondBrInstruction(function.Arguments[0], a, exit));
        exit.Append(new LlvmReturnInstruction(context)); unreachable.Append(new LlvmUncondBrInstruction(unreachable));
        LlvmCycleInfo cycles = new(function);
        LlvmCycle cycle = cycles.GetCycle(a)!;
        Check(ReferenceEquals(cycle, cycles.GetCycle(b)), "irreducible members share a cycle");
        Check(!cycle.IsReducible && cycle.Entries.Count == 2 && cycle.Depth == 1, "two entries retained instead of natural-loop reduction");
        Check(cycle.Contains(a) && cycle.Contains(b) && !cycle.Contains(exit), "cycle membership interval");
        Check(cycles.GetCycle(unreachable) is null && cycles.GetCycle(entry) is null, "unreachable and acyclic blocks remain outside");
        List<LlvmBasicBlock> exits = []; cycle.GetExitBlocks(exits);
        Check(exits.Count == 1 && ReferenceEquals(exits[0], exit), "duplicate outgoing edges give unique exits");
        exits.Clear(); cycle.GetExitBlocks(exits); Check(exits.Count == 1, "exit cache reuses result");

        LlvmFunction nested = LlvmFunction.Create(function.FunctionType, LlvmLinkage.InternalLinkage, "nested", module);
        LlvmBasicBlock pre = new(context, "pre", nested), outer = new(context, "outer", nested), inner = new(context, "inner", nested),
            body = new(context, "body", nested), latch = new(context, "latch", nested), done = new(context, "done", nested);
        pre.Append(new LlvmUncondBrInstruction(outer)); outer.Append(new LlvmCondBrInstruction(nested.Arguments[0], inner, done));
        inner.Append(new LlvmCondBrInstruction(nested.Arguments[0], body, latch)); body.Append(new LlvmUncondBrInstruction(inner));
        latch.Append(new LlvmUncondBrInstruction(outer)); done.Append(new LlvmReturnInstruction(context));
        LlvmCycleInfo nesting = new(nested);
        LlvmCycle child = nesting.GetCycle(inner)!, parent = nesting.GetCycle(outer)!;
        Check(child.Depth == 2 && parent.Depth == 1 && ReferenceEquals(child.ParentCycle, parent), "nested cycle parent and depths");
        Check(parent.Contains(child) && !child.Contains(parent) && ReferenceEquals(nesting.GetTopLevelParentCycle(body), parent), "Euler intervals and outermost query");
        Check(child.IsReducible && parent.IsReducible && ReferenceEquals(child.Blocks[0], inner), "reducible headers and ordered layout");
        return 9;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("CycleInfo: " + message); }
}
