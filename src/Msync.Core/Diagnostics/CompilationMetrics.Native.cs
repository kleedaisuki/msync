using Msync.Core.Native;

namespace Msync.Core;

public static class CompilationMetrics
{
    public static IReadOnlyDictionary<string, ulong> Of(NativeProgram program)
    {
        Dictionary<string, ulong> metrics = new()
        {
            ["native.types"] = (ulong)program.Types.Count,
            ["native.layouts"] = (ulong)program.Layouts.Count,
            ["native.fields"] = (ulong)program.Layouts.Sum(layout => layout.Fields.Count),
            ["native.constants"] = (ulong)program.Constants.Count,
            ["native.projections"] = (ulong)program.Projections.Count,
            ["native.projection_cases"] = (ulong)program.Projections.Sum(projection => projection.Cases.Count),
            ["native.statics"] = (ulong)program.Statics.Count,
            ["native.execution_plans"] = (ulong)(program.ExecutionPlans?.Count ?? 0)
        };
        foreach (NativeAllocationPlacement placement in Enum.GetValues<NativeAllocationPlacement>())
            metrics["allocations." + placement] = (ulong)program.Allocations.Count(plan => plan.Placement == placement);
        ulong[] operations = new ulong[Enum.GetValues<NativeOp>().Max(static op => (int)op) + 1];
        foreach (NativeFunctionCode function in program.Functions)
            foreach (NativeInstruction instruction in function.Instructions) operations[(int)instruction.Op]++;
        foreach (NativeOp op in Enum.GetValues<NativeOp>())
            if (operations[(int)op] != 0) metrics["instructions." + op] = operations[(int)op];
        return metrics;
    }
}
