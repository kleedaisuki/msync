// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp combineInstructionsOverFunction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    public static bool Run(LlvmFunction function, LlvmAAResults alias, LlvmAssumptionCache assumptions,
        LlvmTargetLibraryInfo libraryInfo, LlvmTargetTransformInfo targetInfo, LlvmDominatorTree dominators,
        LlvmInstCombineOptions? options = null, CompilationTrace? trace = null, LlvmInstCombineTuning? tuning = null)
    {
        Debug.Assert(function.BasicBlocks.Count != 0);
        trace = trace?.Detail;
        var pass = new Driver(function, alias, assumptions, libraryInfo, targetInfo, dominators,
            options ?? new(), trace, tuning ?? new());
        return trace is null ? pass.Run() : trace.MeasureDetailed("optimizer.pass.llvm-instcombine", pass,
            static state => state.Run(), attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => pass.Metrics!);
    }
    private sealed class Driver(LlvmFunction function, LlvmAAResults alias, LlvmAssumptionCache assumptions,
        LlvmTargetLibraryInfo libraryInfo, LlvmTargetTransformInfo targetInfo, LlvmDominatorTree dominators,
        LlvmInstCombineOptions options, CompilationTrace? trace, LlvmInstCombineTuning tuning)
    {
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        internal bool Run()
        {
            var worklist = new LlvmInstructionWorklist(Metrics);
            IReadOnlyList<LlvmBasicBlock> reversePostorder = ReversePostorder(function.BasicBlocks[0]);
            bool changed = false;
            uint iteration = 0;
            while (true)
            {
                if (iteration >= options.MaxIterations && !options.VerifyFixpoint) break;
                ++iteration;
                Count("iterations");
                var work = new Work(worklist, function, alias, assumptions, libraryInfo, targetInfo, dominators,
                    reversePostorder, tuning, trace, Metrics);
                bool iterationChanged = work.PrepareWorklist();
                iterationChanged |= work.Run();
                if (!iterationChanged) break;
                changed = true;
                if (iteration > options.MaxIterations)
                    throw new InvalidOperationException($"Instruction Combining on {function.Name} did not reach a fixpoint after {options.MaxIterations} iterations. Use no-verify-fixpoint for this source pass configuration.");
            }
            Count(iteration switch { 1 => "functions.one-iteration", 2 => "functions.two-iterations", 3 => "functions.three-iterations", _ => "functions.four-or-more-iterations" });
            return changed;
        }
        private void Count(string key)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + 1; }
    }
    private static IReadOnlyList<LlvmBasicBlock> ReversePostorder(LlvmBasicBlock entry)
    {
        List<LlvmBasicBlock> postorder = [];
        HashSet<LlvmBasicBlock> visited = [entry];
        List<(LlvmBasicBlock Block, int Successor)> stack = [(entry, 0)];
        while (stack.Count != 0)
        {
            var frame = stack[^1];
            LlvmInstruction terminator = frame.Block.GetTerminator();
            if (frame.Successor == terminator.SuccessorCount)
            { stack.RemoveAt(stack.Count - 1); postorder.Add(frame.Block); continue; }
            stack[^1] = (frame.Block, frame.Successor + 1);
            LlvmBasicBlock successor = terminator.GetSuccessor(frame.Successor);
            if (visited.Add(successor)) stack.Add((successor, 0));
        }
        postorder.Reverse();
        return postorder;
    }
}
