using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

public static partial class NativeOptimizer
{
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
