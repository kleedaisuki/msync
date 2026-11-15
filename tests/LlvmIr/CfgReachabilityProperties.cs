using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class CfgReachabilityProperties
{
    internal static int Run()
    {
        var context = new LlvmContext();
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "reachability");
        var entry = new LlvmBasicBlock(context, "entry", function);
        var header = new LlvmBasicBlock(context, "header", function);
        var body = new LlvmBasicBlock(context, "body", function);
        var exit = new LlvmBasicBlock(context, "exit", function);
        var dead = new LlvmBasicBlock(context, "dead", function);
        var condition = LlvmUndefValue.Get(LlvmIntegerType.Get(context, 1));
        var first = new LlvmFreezeInstruction(condition);
        var second = new LlvmFreezeInstruction(condition);
        entry.Append(new LlvmUncondBrInstruction(header));
        header.Append(new LlvmUncondBrInstruction(body));
        body.Append(first); body.Append(second);
        body.Append(new LlvmCondBrInstruction(condition, header, exit));
        exit.Append(new LlvmReturnInstruction(context));
        dead.Append(new LlvmReturnInstruction(context));
        var tree = new LlvmDominatorTree(function);
        var loops = new LlvmLoopInfo(function, tree);
        var metrics = new Dictionary<string, ulong>();
        int checks = 0;
        foreach (bool useTree in new[] { false, true })
        foreach (bool useLoops in new[] { false, true })
        {
            LlvmDominatorTree? dt = useTree ? tree : null;
            LlvmLoopInfo? li = useLoops ? loops : null;
            Require(LlvmCfgReachability.IsPotentiallyReachable(entry, exit, tree: dt, loops: li, metrics: metrics), "Entry cannot reach the loop exit.");
            Require(!LlvmCfgReachability.IsPotentiallyReachable(exit, header, tree: dt, loops: li, metrics: metrics), "Return block reached a loop header.");
            Require(!LlvmCfgReachability.IsPotentiallyReachable(header, exit, new HashSet<LlvmBasicBlock> { body }, dt, li, metrics), "A loop hole incorrectly permitted an exit shortcut.");
            Require(LlvmCfgReachability.IsPotentiallyReachable(header, body, new HashSet<LlvmBasicBlock> { body }, dt, li, metrics), "Source endpoint-before-exclusion semantics changed.");
            Require(LlvmCfgReachability.IsPotentiallyReachable(second, first, tree: dt, loops: li, metrics: metrics), "A backedge failed to reach an earlier instruction.");
            Require(!LlvmCfgReachability.IsPotentiallyReachable(second, first, new HashSet<LlvmBasicBlock> { header }, dt, li, metrics), "An excluded backedge reached an earlier instruction.");
            Require(!LlvmCfgReachability.IsPotentiallyReachable(entry, dead, tree: dt, loops: li, metrics: metrics), "An isolated block became reachable.");
        }
        foreach (int length in new[] { 31, 32 })
        {
            var chain = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetVoidTy(context)), LlvmLinkage.InternalLinkage, "reachability-budget");
            var blocks = Enumerable.Range(0, length).Select(index => new LlvmBasicBlock(context, $"b{index}", chain)).ToArray();
            for (int index = 0; index + 1 < blocks.Length; ++index) blocks[index].Append(new LlvmUncondBrInstruction(blocks[index + 1]));
            blocks[^1].Append(new LlvmReturnInstruction(context));
            var target = new LlvmBasicBlock(context, "isolated", chain);
            target.Append(new LlvmReturnInstruction(context));
            var worklist = new List<LlvmBasicBlock> { blocks[0] };
            Require(LlvmCfgReachability.IsPotentiallyReachableFromMany(worklist, target, metrics: metrics) == (length == 32), "Source 32-block exploration budget changed.");
        }
        Require(metrics.GetValueOrDefault("cfg.reachability.loop.hole") > 0
            && metrics.GetValueOrDefault("cfg.reachability.loop.exit_walks") > 0
            && metrics.GetValueOrDefault("cfg.reachability.limit_reached") == 1,
            "Reachability counters lost loop-hole, exit-walk or budget work.");
        return checks;
        void Require(bool conditionValue, string message) { Program.Require(conditionValue, message); ++checks; }
    }
}
