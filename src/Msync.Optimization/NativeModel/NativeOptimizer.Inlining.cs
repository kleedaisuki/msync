using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

public static partial class NativeOptimizer
{
    public static NativeProgram InlineExecutable(NativeProgram program, CompilationTrace? trace = null,
        CancellationToken cancellationToken = default)
    {
        if (program.Stage != ArtifactStage.Executable || program.Backend is not null)
            throw new ArgumentException("Executable inlining requires context-bound code before backend selection.", nameof(program));
        if (program.OptimizationLevel < 2 || program.PreserveLocals) return program;
        var state = (program, trace, cancellationToken);
        return trace?.Detail is not { } detail ? InlineBound(state)
            : detail.MeasureDetailed("backend.inline_executable", state, InlineBound,
                CompilationSize.Of(program), CompilationSize.Of);
    }

    private static NativeProgram InlineBound((NativeProgram Program, CompilationTrace? Trace, CancellationToken Token) state)
    {
        var (program, trace, token) = state;
        int level = program.OptimizationLevel;
        int[] budgets = Enumerable.Repeat(level == 2 ? 384 : 1536, program.Functions.Count).ToArray();
        NativeOptimizationProfile? profile = trace?.IncludeOptimizerPasses == true ? new(trace) : null;
        NativeAddressAnalysis.ProgramFacts? facts = null;
        try
        {
            NativeProgram expanded = NativeInlining.Run(program, level, profile, budgets, ref facts,
                executable: true, cancellationToken: token);
            if (ReferenceEquals(expanded, program)) return program;
            int rewritten = expanded.Functions.Count(function => function.Storage is null);
            trace?.Detail?.Metric("inline.replanned_functions", rewritten);
            trace?.Detail?.Metric("inline.added_allocation_sites", expanded.Allocations.Count - program.Allocations.Count);
            trace?.Detail?.Metric("inline.relocated_plan_ranges", (program.ExecutionPlans ?? [])
                .Zip(expanded.ExecutionPlans ?? []).Count(pair => pair.First.Start != pair.Second.Start || pair.First.End != pair.Second.End));
            NativeProgram planned = NativeStoragePlanning.Run(expanded, trace, facts);
            return planned with
            {
                Allocations = planned.Allocations.Select(plan =>
                    expanded.Functions[plan.Function].Storage is not null || plan.Placement != NativeAllocationPlacement.Frame
                        ? plan : plan with
                        {
                            Placement = (planned.Functions[plan.Function].Storage!.Instructions[plan.Instruction] & NativeInstructionStorage.FrameAllocation) != 0
                                ? NativeAllocationPlacement.Frame : NativeAllocationPlacement.LogicalHeap
                        }).ToArray()
            };
        }
        catch (Exception error) { profile?.Fail(error); throw; }
        finally { profile?.Publish(); }
    }

    private static NativeProgram InlineCleaned(NativeProgram program, int level, NativeOptimizationProfile? profile,
        int[] budgets, ref NativeAddressAnalysis.ProgramFacts? shared)
    {
        NativeProgram expanded = NativeInlining.Run(program, level, profile, budgets, ref shared);
        int[] changed = Enumerable.Range(0, program.Functions.Count)
            .Where(index => !ReferenceEquals(program.Functions[index], expanded.Functions[index])).ToArray();
        if (changed.Length == 0) return program;
        NativeFunctionCode[] functions = expanded.Functions.ToArray();
        NativeConstantPool constants = new(expanded.Constants);
        List<NativeAllocationPlan> plans = expanded.Allocations.ToList();
        foreach (int index in changed)
        {
            profile?.Function(index);
            var state = (program: expanded with { Functions = functions }, index, constants, plans, profile, level, shared);
            (functions[index], shared) = profile is null
                ? OptimizeFunction(state.program, index, constants, plans, null, level, shared)
                : profile.Trace.MeasureDetailed("optimizer.inlined_function", state,
                    static item =>
                    {
                        try { return OptimizeFunction(item.program, item.index, item.constants, item.plans, item.profile, item.level, item.shared); }
                        catch (Exception error) { item.profile!.Fail(error); throw; }
                    }, CompilationSize.Of(functions[index]), static result => CompilationSize.Of(result.Function),
                    attributes: new Dictionary<string, string>
                    { ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["function.name"] = functions[index].Name });
        }
        return Compact(expanded with { Functions = functions, Constants = constants.Values.ToArray(), Allocations = plans.ToArray() }, profile, shared);
    }
}
