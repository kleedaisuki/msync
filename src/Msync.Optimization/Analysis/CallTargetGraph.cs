namespace Msync.Optimization;

// A portable optimization phase can share discovery only for the exact
// immutable module version: function IDs/layouts and constants are operands.
internal sealed class CallTargetGraph(Module module)
{
    private CallTargets.Result[]? facts;
    private Dictionary<int, int>[]? targets;
    private bool[]? recursive;
    private int? captureCeiling;
    private bool? entryClosed;

    private CallTargets.Result[] Facts => facts ??= Analyze();

    internal Dictionary<int, int>[] Targets => targets ??= Facts.Select(fact => fact.Targets).ToArray();
    internal IReadOnlySet<int>? RawArguments(int function) => Facts[function].RawArguments;
    internal bool[] Recursive => recursive ??= FindRecursive(Targets);

    private CallTargets.Result[] Analyze()
    {
        int ceiling = captureCeiling ??= CaptureCeiling();
        var plainReturns = module.Functions.Select(function => CallTargets.ReturnsPlain(function, module.Constants)).ToArray();
        return module.Functions.Select(function => CallTargets.Analyze(module, function, module.Constants,
            requireKnownCallee: true, layouts: module.Functions, trackArguments: true, captureCeiling: ceiling,
            plainReturns: plainReturns)).ToArray();
    }

    // The caller can have been rewritten since the module snapshot. Callee
    // layouts and the existing constant IDs are still owned by the snapshot.
    internal CallTargets.Result AnalyzeBody(FunctionCode caller, IReadOnlyList<Value> constants, int originalId)
    {
        int ceiling = captureCeiling ??= CaptureCeiling();
        if (originalId == module.EntryPoint && ceiling != int.MaxValue && (entryClosed ??= EntryIsClosed()))
            ceiling = caller.CaptureCount;
        return CallTargets.Analyze(module, caller, constants, requireKnownCallee: true, layouts: module.Functions,
            captureCeiling: ceiling);
    }

    // The standalone entry starts without a closure. Bytecode can also select
    // it as a callable, so do not assume entry slots are private in that case.
    private bool EntryIsClosed()
    {
        int entry = module.EntryPoint;
        foreach (var value in module.Constants)
            if (value is FunctionValue { NativeName: null } function && function.Function == entry) return false;
        foreach (var function in module.Functions)
            foreach (var instruction in function.Instructions)
            {
                if (instruction.Op == OpCode.MakeClosure && instruction.A == entry) return false;
                if (instruction.Op is not (OpCode.Call or OpCode.SpecializeClosure)) continue;
                if (instruction.Text is { } dispatch)
                {
                    if (FunctionDispatch.Targets(dispatch).Values.Contains(entry)) return false;
                }
                else if (instruction.Op == OpCode.Call ? instruction.B == entry + 1 : instruction.A == entry) return false;
            }
        return true;
    }

    private int CaptureCeiling()
    {
        // Closed code produces source closures through MakeClosure, whose size
        // the producer checks against the function table. Compound constants
        // and live sessions can carry externally supplied closure locations.
        if (module.PreserveLocals || module.SessionDispatches is { Count: > 0 }
            || module.Constants.Any(value => value is not
                (NullValue or BoolValue or NumberValue or StringValue or PathValue or ConnectionValue or TypeValue
                    or FunctionValue { Captures: null or { Count: 0 } })))
            return int.MaxValue;
        int ceiling = 0;
        foreach (var function in module.Functions) ceiling = Math.Max(ceiling, function.CaptureCount);
        return ceiling;
    }

    internal static bool[] FindRecursive(Dictionary<int, int>[] targets)
    {
        int[] components = Components(targets);
        var sizes = new int[components.Length];
        foreach (int component in components) sizes[component]++;
        var recursive = new bool[targets.Length];
        for (int index = 0; index < recursive.Length; index++)
            recursive[index] = sizes[components[index]] > 1 || targets[index].ContainsValue(index);
        return recursive;
    }

    internal static int[] Components(Dictionary<int, int>[] targets)
    {
        var edges = targets.Select(target => target.Values.Distinct().ToArray()).ToArray();
        var reverse = targets.Select(_ => new List<int>()).ToArray();
        for (int vertex = 0; vertex < edges.Length; vertex++) foreach (int target in edges[vertex]) reverse[target].Add(vertex);
        var seen = new bool[targets.Length];
        var order = new List<int>(targets.Length);
        var stack = new Stack<(int Vertex, int Next)>();
        for (int root = 0; root < edges.Length; root++)
        {
            if (seen[root]) continue;
            seen[root] = true; stack.Push((root, 0));
            while (stack.TryPop(out var frame))
            {
                if (frame.Next == edges[frame.Vertex].Length) { order.Add(frame.Vertex); continue; }
                stack.Push((frame.Vertex, frame.Next + 1));
                int next = edges[frame.Vertex][frame.Next];
                if (seen[next]) continue;
                seen[next] = true; stack.Push((next, 0));
            }
        }
        var result = new int[targets.Length]; Array.Fill(result, -1);
        var pending = new Stack<int>(); int component = 0;
        for (int position = order.Count - 1; position >= 0; position--)
        {
            int root = order[position];
            if (result[root] != -1) continue;
            result[root] = component; pending.Push(root);
            while (pending.TryPop(out int vertex))
                foreach (int predecessor in reverse[vertex])
                    if (result[predecessor] == -1) { result[predecessor] = component; pending.Push(predecessor); }
            component++;
        }
        return result;
    }

}
