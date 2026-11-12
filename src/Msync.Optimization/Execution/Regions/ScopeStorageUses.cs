namespace Msync.Optimization.Execution;

internal sealed class ScopeStorageUses
{
    [Flags]
    private enum Use : byte { None = 0, Scope = 1, Other = 2 }

    private readonly bool[]? direct;
    private readonly bool hasScope;
    private bool[]? graph;

    internal ScopeStorageUses(Module image, IReadOnlyList<Instruction> code)
    {
        int loads = 0;
        for (int pc = 0; pc < code.Count; pc++)
        {
            var instruction = code[pc];
            if (instruction.Op == OpCode.LoadStorage)
            {
                loads++;
                continue;
            }
            if (instruction.Op is OpCode.NewScope or OpCode.PublishStatic
                && ScopeOperands.Construction(image, instruction).Fields.Count is >= 2 and var arity)
            {
                hasScope = true;
                int count = Math.Min(loads, arity);
                // The producer's normal form evaluates initializers before
                // pushing field storage. The suffix has no alternate use,
                // even when another edge can enter at a later field.
                if (count != 0)
                    (direct ??= new bool[code.Count]).AsSpan(pc - count, count).Fill(true);
            }
            loads = 0;
        }
    }

    internal bool Contains(Module image, int function, int pc)
    {
        if (direct?[pc] == true) return true;
        if (!hasScope) return false;
        var sites = Volatile.Read(ref graph);
        if (sites is null)
        {
            sites = FindGraphSites(image, function);
            Volatile.Write(ref graph, sites);
        }
        return sites.Length != 0 && sites[pc];
    }

    private static bool[] FindGraphSites(Module image, int function)
    {
        if (!ExecutionRegionImporter.TryImport(image, function, out var region, out _)) return [];

        var uses = new Use[region.ValueCount];
        var pending = new Queue<int>();
        void Add(int value, Use use)
        {
            if ((uses[value] & use) == use) return;
            uses[value] |= use;
            pending.Enqueue(value);
        }

        // A block parameter inherits representation constraints from its
        // consumers, and each incoming operand inherits those constraints.
        var incoming = new Dictionary<int, List<int>>();
        int depth = 0;
        foreach (var block in region.Blocks)
            foreach (var edge in block.Successors)
                depth = Math.Max(depth, edge.Arguments.Depth);
        var scratch = new ExecutionValueId[depth];
        foreach (var block in region.Blocks)
        {
            foreach (var operation in block.Operations)
            {
                var op = operation.Instruction.Op;
                if (op is OpCode.Dup or OpCode.Pop) continue;
                // A singleton row cannot use the numeric scope pages.
                var use = op is OpCode.NewScope or OpCode.PublishStatic && operation.Inputs.Length >= 2 ? Use.Scope : Use.Other;
                foreach (var input in operation.Inputs) Add(input.Index, use);
            }

            foreach (var edge in block.Successors)
            {
                var arguments = scratch.AsSpan(0, edge.Arguments.Depth);
                region.CopyStack(edge.Arguments, arguments);
                if (edge.TargetPc == region.ExitPc)
                {
                    if (!arguments.IsEmpty) Add(arguments[^1].Index, Use.Other);
                    continue;
                }

                var parameters = region.BlockAt(edge.TargetPc)!.Parameters;
                for (int i = 0; i < arguments.Length; i++)
                {
                    int parameter = parameters[i].Index;
                    int argument = arguments[i].Index;
                    if (parameter == argument) continue;
                    if (!incoming.TryGetValue(parameter, out var predecessors))
                        incoming.Add(parameter, predecessors = []);
                    predecessors.Add(argument);
                }
            }
        }

        while (pending.TryDequeue(out int parameter))
            if (incoming.TryGetValue(parameter, out var predecessors))
                foreach (int predecessor in predecessors) Add(predecessor, uses[parameter]);

        bool[]? sites = null;
        foreach (var block in region.Blocks)
            foreach (var operation in block.Operations)
                if (operation.Instruction.Op == OpCode.LoadStorage
                    && operation.Result is { } value && uses[value.Index] == Use.Scope)
                    (sites ??= new bool[region.ExitPc])[operation.Pc] = true;
        return sites ?? [];
    }
}
