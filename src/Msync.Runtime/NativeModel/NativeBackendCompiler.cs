using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static class NativeBackendCompiler
{
    public static NativeProgram Compile(NativeProgram program, NativeRuntimeContext context, CompilationTrace? trace = null) =>
        Compile(program, context, trace, CancellationToken.None);

    internal static NativeProgram Compile(NativeProgram program, NativeRuntimeContext context, CompilationTrace? trace,
        CancellationToken cancellationToken)
    {
        if (program.Backend is not null) return program;
        NativeImage image = new(program, context);
        NativeBackendFunction[] functions = new NativeBackendFunction[program.Functions.Count];
        for (int index = 0; index < functions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            functions[index] = trace?.Detail is not { } detail ? Lower(image, index)
                : detail.MeasureDetailed("backend.function", index, function => Lower(image, function, detail),
                    attributes: new Dictionary<string, string>
                    {
                        ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["function.name"] = program.Functions[index].Name
                    }, metrics: BackendMetrics);
        }
        functions = trace?.Detail is not { } workTrace ? NativeWorkPlanning.Refine(program, functions, null)
            : workTrace.MeasureDetailed("backend.work_scope", functions,
                bodies => NativeWorkPlanning.Refine(program, bodies, workTrace));
        HashSet<NativeConnection> connections = [new(ConnectionKind.Local)];
        foreach (NativeConstant value in program.Constants)
        {
            if (value.Kind == NativeKind.Connection) connections.Add(new(value.ConnectionKind, value.Text ?? ""));
            if (value.Path?.Connection is { } endpoint) connections.Add(endpoint);
        }
        foreach (NativeExecutionPlan plan in program.ExecutionPlans ?? [])
            foreach (NativePlannedTransfer transfer in plan.Transfers)
            {
                if (transfer.Destination.Connection is { } destination) connections.Add(destination);
                Collect(transfer.Source, connections);
            }
        NativeConnection[] endpoints = connections.OrderBy(item => item.Kind).ThenBy(item => item.Address, StringComparer.Ordinal).ToArray();
        Dictionary<NativeConnection, int> resources = endpoints.Select((value, index) => (value, index)).ToDictionary(pair => pair.value, pair => pair.index);
        for (int index = 0; index < functions.Length; index++)
            functions[index] = NativeConnectionLowering.Link(program, index, functions[index], resources);
        trace?.Detail?.Metric("backend.connections", endpoints.Length);
        trace?.Detail?.Metric("backend.connection_specializations", functions.Sum(function => function.Instructions.Count(code => code.Connection >= 0)));
        return program with { Backend = new(functions, endpoints) };
    }

    private static Dictionary<string, ulong> BackendMetrics(NativeBackendFunction body)
    {
        Dictionary<string, ulong> counts = new()
        {
            ["instructions"] = (ulong)body.Instructions.Count,
            ["handlers.executed_sites"] = 0, ["handlers.absorbed_sites"] = 0,
            ["u64.results"] = 0, ["u64.operations"] = 0, ["copy.compiled_sites"] = 0,
            ["fused.instructions"] = 0
        };
        for (int pc = 0; pc < body.Instructions.Count;)
        {
            var code = body.Instructions[pc];
            string handler = $"handlers.{code.Handler}";
            counts[handler] = counts.GetValueOrDefault(handler) + 1;
            counts["handlers.executed_sites"]++;
            counts["handlers.absorbed_sites"] += (ulong)(code.Span - 1);
            if (code.Span > 1) counts["fused.instructions"]++;
            if (code.Numbers.HasFlag(NativeNumberForm.UnsignedResult)) counts["u64.results"]++;
            if (code.Numbers.HasFlag(NativeNumberForm.UnsignedA | NativeNumberForm.UnsignedB)) counts["u64.operations"]++;
            if (code.Handler == NativeBackendHandler.CompiledCopy) counts["copy.compiled_sites"]++;
            pc += code.Span;
        }
        return counts;
    }

    private static NativeBackendFunction Lower(NativeImage image, int index, CompilationTrace? trace = null)
    {
        using NativeExecutionFunction function = new(image, index, new(NativeJitPolicy.Off), lowering: true, trace: trace);
        return function.ExportBackend();
    }

    private static void Collect(NativePlanValue value, HashSet<NativeConnection> connections)
    {
        if (value.Path?.Connection is { } endpoint) connections.Add(endpoint);
        if (value.Items is { } items) foreach (NativePlanValue item in items) Collect(item, connections);
    }
}
