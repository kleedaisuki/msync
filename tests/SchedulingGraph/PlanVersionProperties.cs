using System.Text;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class PlanVersionProperties
{
    private sealed record Operation(Value Source, string Destination);
    private static readonly string[] names = ["a", "b", "c", "d"];

    internal static async Task RunAsync(string scratch)
    {
        string directory = Path.Combine(scratch, "version-properties");
        Directory.CreateDirectory(directory);
        Dictionary<string, string> initial = names.ToDictionary(name => name, name => "initial:" + name);
        foreach (var (name, contents) in initial)
            await File.WriteAllTextAsync(Path.Combine(directory, name), contents);
        int cases = 0, schedules = 0, inputOperations = 0, planOperations = 0;
        Random generation = new(0x51C0DE);
        for (int example = 0; example < 40; example++)
        {
            Operation[] operations = example switch
            {
                0 => [Copy("a", "b"), Copy("c", "b")],
                1 => [Copy("a", "b"), Copy("b", "c"), Copy("d", "b")],
                2 => [Write("old", "b"), Copy("b", "c"), Write("new", "b"), Write("final", "c")],
                _ => Enumerable.Range(0, generation.Next(3, 13)).Select(index =>
                    generation.Next(3) == 0 ? Write($"literal:{example}:{index}", names[generation.Next(names.Length)])
                        : Copy(names[generation.Next(names.Length)], names[generation.Next(names.Length)])).ToArray()
            };
            Dictionary<string, string> expected = new(initial);
            foreach (Operation operation in operations) Apply(expected, operation.Source, operation.Destination);
            string source = Source(operations);
            Module module = Compiler.Compile(source, Path.Combine(directory, $"case-{example}.msync"));
            module = ContextualBackend.Prepare(Binding.Bind(module, new(directory, directory)),
                new(WorkingDirectory: directory, Jobs: 4));
            var code = module.Functions[module.EntryPoint].Instructions;
            Require(code.All(instruction => instruction.Op is not (OpCode.Transfer or OpCode.LocalTransfer)),
                $"case {example}: the straight-line regular-file program still has unprepared transfers");
            ExecutionPlan[] plans = code.Where(instruction => instruction.Op == OpCode.ExecutePlan)
                .Select(instruction => module.ExecutionPlans![instruction.A]).ToArray();
            inputOperations += operations.Length;
            planOperations += plans.Sum(plan => plan.Transfers.Count);
            for (int schedule = 0; schedule < 8; schedule++)
            {
                Dictionary<string, string> actual = new(initial);
                Random ordering = new(example * 4099 + schedule);
                foreach (ExecutionPlan plan in plans) Execute(plan, actual, ordering, example);
                foreach (string name in names)
                    Require(actual[name] == expected[name],
                        $"case {example}, schedule {schedule}, file {name}: expected {expected[name]}, got {actual[name]}; source: {source}");
                schedules++;
            }
            cases++;
        }
        foreach (var (name, contents) in initial)
            Require(await File.ReadAllTextAsync(Path.Combine(directory, name)) == contents,
                "Preparing/modeling a plan executed actual file writes");
        Console.WriteLine($"PASS {cases} file-version programs / {schedules} valid topological executions; operations {inputOperations} -> {planOperations}");
    }

    private static void Execute(ExecutionPlan plan, Dictionary<string, string> state, Random ordering, int example)
    {
        bool[] complete = new bool[plan.Transfers.Count];
        HashSet<int> remaining = Enumerable.Range(0, complete.Length).ToHashSet();
        while (remaining.Count != 0)
        {
            int[] ready = remaining.Where(node => plan.Transfers[node].Predecessors.All(predecessor => complete[predecessor])).ToArray();
            Require(ready.Length != 0, $"case {example}: dependency graph contains a cycle");
            int selected = ready[ordering.Next(ready.Length)];
            PlannedTransfer transfer = plan.Transfers[selected];
            Require(transfer.Kind is PlannedTransferKind.CopyFile or PlannedTransferKind.WriteText,
                $"case {example}: regular-file model unexpectedly needs {transfer.Kind}");
            Apply(state, transfer.Source, Path.GetFileName(transfer.Destination.Raw));
            complete[selected] = true;
            remaining.Remove(selected);
        }
    }

    private static string Source(IEnumerable<Operation> operations)
    {
        StringBuilder source = new();
        foreach (Operation operation in operations)
        {
            string input = operation.Source switch
            {
                PathValue path => path.Raw,
                StringValue text => '"' + text.Data + '"',
                _ => throw new InvalidOperationException("The property generator emitted an unsupported source")
            };
            source.Append(input).Append(" -> ./").Append(operation.Destination).AppendLine(";");
        }
        return source.ToString();
    }

    private static void Apply(Dictionary<string, string> state, Value source, string destination)
    {
        state[destination] = source switch
        {
            PathValue path => state[Path.GetFileName(path.Raw)],
            StringValue text => text.Data,
            _ => throw new InvalidOperationException("The file-version model accepts only Path and String operands")
        };
    }

    private static Operation Copy(string source, string destination) => new(new PathValue("./" + source), destination);
    private static Operation Write(string value, string destination) => new(new StringValue(value), destination);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
