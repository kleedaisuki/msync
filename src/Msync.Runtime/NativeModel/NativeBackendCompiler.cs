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
        NativeCallWindowCandidate[]?[] windows = new NativeCallWindowCandidate[functions.Length][];
        for (int index = 0; index < functions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lowered = trace?.Detail is not { } detail ? Lower(image, index)
                : detail.MeasureDetailed("backend.function", index, function => Lower(image, function, detail),
                    attributes: new Dictionary<string, string>
                    {
                        ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["function.name"] = program.Functions[index].Name
                    }, metrics: result => BackendMetrics(result.Body));
            (functions[index], windows[index]) = lowered;
        }
        functions = trace?.Detail is not { } workTrace ? NativeWorkPlanning.Refine(program, functions, null)
            : workTrace.MeasureDetailed("backend.work_scope", functions,
                bodies => NativeWorkPlanning.Refine(program, bodies, workTrace));
        program = trace?.Detail is not { } callTrace ? PlanParameters(program, functions, null)
            : callTrace.MeasureDetailed("backend.call_parameters", program,
                body => PlanParameters(body, functions, callTrace));
        program = trace?.Detail is not { } windowTrace ? NativeCallWindowPlanning.Plan(program, functions, windows, null)
            : windowTrace.MeasureDetailed("backend.call_windows", program,
                body => NativeCallWindowPlanning.Plan(body, functions, windows, windowTrace));
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

    private static NativeProgram PlanParameters(NativeProgram program, NativeBackendFunction[] backend, CompilationTrace? trace)
    {
        NativeFunctionCode[]? updated = null;
        int eligible = 0, planned = 0, omitted = 0;
        for (int index = 0; index < backend.Length; index++)
        {
            NativeFunctionCode function = program.Functions[index];
            if (!backend[index].ContextFree || function.IsTemplate || function.ParameterCount == 0) continue;
            eligible++;
            ulong unread = UnreadParameters(function);
            if (unread == 0) continue;
            NativeStoragePlan storage = function.Storage!;
            NativeSlotStorage[] slots = storage.Slots.ToArray();
            int count = 0;
            for (int parameter = 0; parameter < function.ParameterCount; parameter++)
            {
                if ((unread & (1UL << parameter)) == 0) continue;
                slots[function.CaptureCount + parameter] |= NativeSlotStorage.UnreadParameter;
                count++;
            }
            (updated ??= program.Functions.ToArray())[index] = function with { Storage = storage with { Slots = slots } };
            planned++;
            omitted += count;
            trace?.Report(new("backend.call_parameters.function", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string>
                { ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["function.name"] = function.Name },
                Metrics = new Dictionary<string, ulong> { ["parameters.unread"] = (ulong)count }
            });
        }
        trace?.Metric("functions.eligible", eligible);
        trace?.Metric("functions.planned", planned);
        trace?.Metric("parameters.unread", omitted);
        return updated is null ? program : program with { Functions = updated };
    }

    private static ulong UnreadParameters(NativeFunctionCode code)
    {
        int count = code.ParameterCount;
        if (count is 0 or > 64) return 0;
        ulong unread = ulong.MaxValue >> (64 - count);
        foreach (NativeInstruction instruction in code.Instructions)
        {
            if (instruction.Op is NativeOp.IndexAddress or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory)
                return 0;
            foreach (int slot in NativeInstructionFacts.Reads(instruction))
            {
                int parameter = slot - code.CaptureCount;
                if ((uint)parameter < (uint)count) unread &= ~(1UL << parameter);
            }
            if (unread == 0) break;
        }
        return unread;
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

    private static (NativeBackendFunction Body, NativeCallWindowCandidate[]? Windows) Lower(
        NativeImage image, int index, CompilationTrace? trace = null)
    {
        using NativeExecutionFunction function = new(image, index, new(NativeJitPolicy.Off), lowering: true, trace: trace);
        return (function.ExportBackend(), function.WindowCandidates);
    }

    private static void Collect(NativePlanValue value, HashSet<NativeConnection> connections)
    {
        if (value.Path?.Connection is { } endpoint) connections.Add(endpoint);
        if (value.Items is { } items) foreach (NativePlanValue item in items) Collect(item, connections);
    }
}
