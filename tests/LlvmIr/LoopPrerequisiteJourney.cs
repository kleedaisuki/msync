using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record LoopPrerequisiteReceipt(int SourceCases, int Executions, int LiveOutUsesBefore,
    int LiveOutUsesAfter, int LcssaExitPhis, int MaximumLoopDepth, int CanonicalGraphBlocksAdded);

internal static class LoopPrerequisiteJourney
{
    private sealed record Case(string Name, string Source, double Expected);
    private static readonly Case[] Cases =
    [
        new("carried-after-exit", """
            let index = 0; let total = 0;
            loop { if index >= 4 { break; }; total = total + index; index = index + 1; };
            total * 10 + index
            """, 64),
        new("nested", """
            let outer = 0; let total = 0;
            loop {
                if outer >= 3 { break; };
                let inner = 0;
                loop {
                    if inner >= 2 { break; };
                    total = total + outer * 10 + inner;
                    inner = inner + 1;
                };
                outer = outer + 1;
            };
            total + outer
            """, 66),
        new("early-break", """
            let index = 0; let total = 0;
            loop { index = index + 1; if index == 3 { break; }; total = total + index; };
            total * 10 + index
            """, 33),
        new("defined-before-break", """
            let index = 0; let latest = 0;
            loop { index = index + 1; latest = index * 3; if index >= 4 { break; }; };
            latest + index
            """, 16),
        new("iteration-captures", """
            let first = fn { 0 }; let second = fn { 0 }; let third = fn { 0 };
            for item in [1, 2, 3] {
                if item == 1 { first = fn { item }; } else {
                    if item == 2 { second = fn { item }; } else { third = fn { item }; };
                };
            };
            first() + second() * 10 + third() * 100
            """, 321)
    ];

    internal static async Task<LoopPrerequisiteReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "loop-prerequisites");
        Directory.CreateDirectory(root);
        List<CompilationObservation> records = [];
        CompilationTrace trace = new(records.Add, includeOptimizerPasses: true);
        int before = 0, after = 0, depth = 0, executions = 0;
        foreach (Case test in Cases)
        {
            string work = Path.Combine(root, test.Name);
            Directory.CreateDirectory(work);
            string sourceFile = Path.Combine(work, "source.msync");
            File.WriteAllText(sourceFile, test.Source);
            HirModule hir = Compiler.Analyze(test.Source, sourceFile);
            File.WriteAllText(Path.Combine(work, "typed-hir.txt"), HirPrinter.Print(hir));
            LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(hir, trace);
            File.WriteAllText(Path.Combine(work, "source.ll"), LlvmIrWriter.Write(graph.Module));
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count > 0))
            {
                LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function, trace), trace);
                var initialLoops = new LlvmLoopInfo(function, new LlvmDominatorTree(function, trace), trace);
                LlvmBasicBlock[] headers = initialLoops.GetLoopsInPreorder().Reverse().Select(loop => loop.Header).ToArray();
                foreach (LlvmBasicBlock header in headers)
                {
                    // These source constructs already have preheaders and single latches.
                    // Only the landed dedicated-exit and header-PHI helpers are applied here.
                    LlvmDominatorTree currentTree = new(function, trace);
                    LlvmLoopInfo currentLoops = new(function, currentTree, trace);
                    LlvmLoop loop = currentLoops.GetLoopFor(header)!;
                    Program.Require(LlvmLoopSimplify.GetLoopPreheader(loop) is not null
                        && LlvmLoopSimplify.GetLoopLatch(loop) is not null,
                        "Structured source loops must retain their existing preheader and single latch.");
                    LlvmLoopSimplify.FormDedicatedExitBlocks(loop, trace);
                    currentTree = new(function, trace);
                    currentLoops = new(function, currentTree, trace);
                    LlvmLoopSimplify.SimplifyHeaderPhis(currentLoops.GetLoopFor(header)!, currentTree, trace);
                }
                LlvmDominatorTree tree = new(function, trace);
                LlvmLoopInfo loops = new(function, tree, trace);
                depth = Math.Max(depth, loops.GetLoopsInPreorder().Select(loop => loop.LoopDepth).DefaultIfEmpty().Max());
                before += NonClosedUses(loops);
                LlvmLcssa.Run(function, tree, loops, trace);
                after += NonClosedUses(loops);
            }
            File.WriteAllText(Path.Combine(work, "lcssa.ll"), LlvmIrWriter.Write(graph.Module));
            NativeProgram program = LlvmNativeLowerer.Lower(graph, trace);
            await using NativeInterpreter interpreter = new(program,
                new ExecutionOptions(WorkingDirectory: work, SourceFile: sourceFile));
            NativeValue actual = await interpreter.ExecuteAsync();
            Program.Require(actual.AsNumber() == test.Expected,
                $"{test.Name}: expected {test.Expected}, actual {actual.AsNumber()} after loop helpers/LCSSA.");
            executions++;
        }
        int graphBlocks = CanonicalGraph(trace);
        int exitPhis = records.Where(record => record.Name == "optimizer.pass.llvm-lcssa")
            .Sum(record => checked((int)(record.Metrics?.GetValueOrDefault("phis.exit_inserted") ?? 0)));
        Program.Require(before > 0 && after == 0 && exitPhis > 0 && depth == 2,
            "Real source loops must close actual escaping SSA uses through exit PHIs, including nested loops.");
        File.WriteAllLines(Path.Combine(root, "compiler.trace.jsonl"), records.Select(record => JsonSerializer.Serialize(new
        { record.Name, record.Outcome, record.Attributes, record.Metrics, error = record.Error?.Message })));
        return new(Cases.Length, executions, before, after, exitPhis, depth, graphBlocks);
    }

    private static int NonClosedUses(LlvmLoopInfo loops)
    {
        int count = 0;
        foreach (LlvmLoop loop in loops.GetLoopsInPreorder())
        foreach (LlvmInstruction definition in loop.Blocks.SelectMany(block => block.Instructions))
        foreach (LlvmUse use in definition.Uses)
        {
            if (use.User is not LlvmInstruction user || user.Parent is null) continue;
            LlvmBasicBlock useBlock = user is LlvmPhiInstruction phi
                ? phi.GetIncomingBlock(use.OperandNo) : user.Parent;
            if (!loop.Contains(useBlock)) count++;
        }
        return count;
    }

    private static int CanonicalGraph(CompilationTrace trace)
    {
        LlvmContext context = new();
        LlvmIntegerType integer = LlvmType.GetInt64Ty(context);
        LlvmIntegerType boolean = LlvmType.GetInt1Ty(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(integer, [boolean, boolean]),
            LlvmLinkage.InternalLinkage, "critical-exit-and-two-backedges");
        LlvmBasicBlock entry = new(context, "entry", function), header = new(context, "header", function),
            body = new(context, "body", function), left = new(context, "left", function),
            right = new(context, "right", function), exit = new(context, "exit", function);
        LlvmConstantInt zero = LlvmConstantInt.Get(integer, 0), one = LlvmConstantInt.Get(integer, 1),
            two = LlvmConstantInt.Get(integer, 2), forty = LlvmConstantInt.Get(integer, 40);
        entry.Append(new LlvmCondBrInstruction(function.Arguments[0], header, exit));
        LlvmPhiInstruction carried = new(integer, 3);
        header.Append(carried);
        header.Append(new LlvmCondBrInstruction(function.Arguments[1], body, exit));
        body.Append(new LlvmCondBrInstruction(function.Arguments[0], left, right));
        LlvmBinaryOperator leftValue = new(LlvmOpcode.Add, carried, one);
        left.Append(leftValue); left.Append(new LlvmUncondBrInstruction(header));
        LlvmBinaryOperator rightValue = new(LlvmOpcode.Add, carried, two);
        right.Append(rightValue); right.Append(new LlvmUncondBrInstruction(header));
        carried.AddIncoming(zero, entry); carried.AddIncoming(leftValue, left); carried.AddIncoming(rightValue, right);
        LlvmPhiInstruction result = new(integer, 2);
        result.AddIncoming(forty, entry); result.AddIncoming(carried, header);
        exit.Append(result); exit.Append(new LlvmReturnInstruction(context, result));
        int initialBlocks = function.BasicBlocks.Count;

        LlvmLoop FindLoop() => new LlvmLoopInfo(function, new LlvmDominatorTree(function)).GetLoopFor(header)!;
        LlvmLoop loop = FindLoop();
        Program.Require(LlvmLoopSimplify.GetLoopPreheader(loop) is null && LlvmLoopSimplify.GetLoopLatch(loop) is null,
            "The supplementary graph must actually require preheader and backedge helpers.");
        LlvmBasicBlock preheader = LlvmLoopSimplify.InsertPreheaderForLoop(loop, trace);
        loop = FindLoop();
        LlvmBasicBlock? latch = LlvmLoopSimplify.InsertUniqueBackedgeBlock(loop, preheader, trace);
        loop = FindLoop();
        Program.Require(latch is not null && ReferenceEquals(LlvmLoopSimplify.GetLoopLatch(loop), latch),
            "The exact backedge helper did not join the two real backedges.");
        Program.Require(LlvmLoopSimplify.FormDedicatedExitBlocks(loop, trace),
            "A critical exit with an outside predecessor must be split by the dedicated-exit helper.");
        loop = FindLoop();
        Program.Require(result.IncomingCount == 2
            && ReferenceEquals(result.GetIncomingValue(result.GetBasicBlockIndex(entry)), forty)
            && Enumerable.Range(0, result.IncomingCount).Where(index => !ReferenceEquals(result.GetIncomingBlock(index), entry))
                .All(index => LlvmDominatorTree.Predecessors(result.GetIncomingBlock(index)).All(loop.Contains)),
            "Critical-exit splitting corrupted the existing PHI's outside value or loop-edge ownership.");
        LlvmSsaUpdater updater = new(trace: trace);
        updater.Initialize(integer, "joined-exit");
        updater.AddAvailableValue(entry, forty);
        updater.AddAvailableValue(header, carried);
        LlvmValue joined = updater.GetValueAtEndOfBlock(exit);
        Program.Require(joined is LlvmPhiInstruction joinedPhi && joinedPhi.IncomingCount == 2
            && ReferenceEquals(joinedPhi.Parent, exit), "SSAUpdater did not join distinct values across the split critical exit.");
        foreach (LlvmPhiInstruction phi in function.BasicBlocks.SelectMany(block => block.Instructions).OfType<LlvmPhiInstruction>())
            Program.Require(phi.IncomingCount == LlvmDominatorTree.Predecessors(phi.Parent!).Count(),
                "Canonical helper rewrites left a PHI with missing or extra predecessor edges.");
        return function.BasicBlocks.Count - initialBlocks;
    }
}
