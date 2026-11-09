using System.Text;
using Msync.Core;
using Msync.Runtime;

string scratch = Path.Combine(Directory.GetCurrentDirectory(), ".temp", "scheduling-graph-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    if (args.Contains("--retirement-only", StringComparer.Ordinal) || args.Contains("--retirement-observe", StringComparer.Ordinal))
    {
        await FrontierRetirementChecks.RunAsync(scratch, observe: args.Contains("--retirement-observe", StringComparer.Ordinal));
        return;
    }
    if (args.Contains("--namespace-only", StringComparer.Ordinal))
    {
        await PathNamespaceChecks.RunAsync(scratch);
        return;
    }
    SchedulingStatistics single = await IndependentCopies(1);
    SchedulingStatistics small = await IndependentCopies(100);
    SchedulingStatistics large = await IndependentCopies(1000);
    Require(small.ResourceNodesVisited > 0, "Resource lookups did not record their traversal cost.");
    Require((large.ResourceNodesVisited - small.ResourceNodesVisited) * 99 == (small.ResourceNodesVisited - single.ResourceNodesVisited) * 900,
        $"Independent same-depth resource work is not affine-linear: {small.ResourceNodesVisited} vs {large.ResourceNodesVisited} nodes.");
    await ResourceOrder();
    Console.WriteLine($"PASS: actual 100/1000 independent file copies; resource visits {small.ResourceNodesVisited}/{large.ResourceNodesVisited}; RAW/WAR ordering and native observation.");
}
finally { Directory.Delete(scratch, recursive: true); }

async Task<SchedulingStatistics> IndependentCopies(int count)
{
    string directory = Path.Combine(scratch, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Directory.CreateDirectory(directory);
    List<Value> constants = [];
    List<Instruction> code = [];
    for (int i = 0; i < count; i++)
    {
        string source = Path.Combine(directory, $"source-{i}");
        string destination = Path.Combine(directory, $"target-{i}");
        await File.WriteAllBytesAsync(source, Payload(i));
        constants.Add(new PathValue(source));
        constants.Add(new PathValue(destination));
        code.Add(new(OpCode.Constant, 2 * i));
        code.Add(new(OpCode.Constant, 2 * i + 1));
        code.Add(new(OpCode.Transfer));
        code.Add(new(OpCode.Pop));
    }
    code.Add(new(OpCode.Null));
    code.Add(new(OpCode.Return));
    Interpreter interpreter = new(Program(constants, code), new(WorkingDirectory: directory, Jobs: 4));
    await interpreter.ExecuteAsync();
    Require(interpreter.FailedTransfers == 0, "An independent copy failed.");
    for (int i = 0; i < count; i++)
        Require((await File.ReadAllBytesAsync(Path.Combine(directory, $"target-{i}"))).AsSpan().SequenceEqual(Payload(i)),
            $"Independent target {i} did not retain its own source bytes.");
    SchedulingStatistics statistics = interpreter.SchedulingStatistics;
    Require(statistics.AdmittedTransfers == count, "Transfer admission count does not match actual files.");
    Require(statistics.DependencyEdges == 0, "Independent sibling files acquired artificial dependency edges.");
    Require(statistics.NativeHazardWaits == 0, "Independent copies unexpectedly waited on a native observation.");
    return statistics;
}

async Task ResourceOrder()
{
    string shared = Path.Combine(scratch, "shared");
    string first = Path.Combine(scratch, "first");
    string second = Path.Combine(scratch, "second");
    List<Value> constants = [new StringValue("first"), new PathValue(shared), new PathValue(first), new StringValue("second"), new PathValue(second)];
    List<Instruction> code = [];
    void Transfer(int source, int destination)
    {
        code.Add(new(OpCode.Constant, source)); code.Add(new(OpCode.Constant, destination));
        code.Add(new(OpCode.Transfer)); code.Add(new(OpCode.Pop));
    }
    Transfer(0, 1);
    Transfer(1, 2);
    Transfer(3, 1);
    Transfer(1, 4);
    code.Add(new(OpCode.Constant, 4));
    code.Add(new(OpCode.NativeCall, 1, Text: "fs.exists"));
    code.Add(new(OpCode.Return));
    Interpreter interpreter = new(Program(constants, code), new(WorkingDirectory: scratch, Jobs: 4));
    Require(await interpreter.ExecuteAsync() is BoolValue { Data: true }, "Native exists observed a destination before its copy completed.");
    Require(await File.ReadAllTextAsync(first) == "first", "A write overtook the preceding read of the shared path.");
    Require(await File.ReadAllTextAsync(second) == "second", "A read overtook the preceding write of the shared path.");
    Require(await File.ReadAllTextAsync(shared) == "second", "Same-path writes lost their source order.");
    Require(interpreter.SchedulingStatistics.AdmittedTransfers == 4, "Resource chain dropped a transfer.");
}

static byte[] Payload(int index) => Encoding.UTF8.GetBytes($"file {index}: 可莉\n");
static Module Program(IReadOnlyList<Value> constants, IReadOnlyList<Instruction> code)
    => new(constants, [new("main", 0, 0, code, TypeRef.Any)], OptimizationLevel: 0);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
