using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;

int count = int.Parse(Option("--submissions", "256"), CultureInfo.InvariantCulture);
string scenario = Option("--case", "fixed");
bool describe = args.Contains("--describe", StringComparer.Ordinal);
string root = Path.GetFullPath(Option("--root", ".temp/repl-tail/component-work"));
Directory.CreateDirectory(root);
CompilerSession compiler = new();
await using ExecutionSession execution = new(new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null));
Document shell = Parser.Parse(File.ReadAllText("stdlib/shell.msync"), "msync-stdlib:///0.8.0/shell.msync");
string prelude = string.Join('\n', shell.Statements.Select(statement => statement switch
{
    FunctionDeclaration function => function.Name,
    NativeDeclaration native => native.Name,
    ScopeDeclaration scope => scope.Name,
    LetStatement { Pattern: NamePattern binding } => binding.Name,
    _ => null
}).OfType<string>().Select(name => $"let {name} = $std::shell::{name};"));
await Submit(prelude);
await Submit("let state = scope { n: 0 }; 0");
List<Observation> observations = [];
for (int index = 1; index <= count; index++)
{
    string prefix = scenario == "growing" ? $"let r{index} = scope {{ s: \"row_{index}\", p: ./item, b: true }}; " : "";
    string source = prefix + "state::n = state::n + 1; state::n";
    var before = Counter.Read();
    Module module = compiler.Compile(source);
    var afterCompile = Counter.Read();
    Value value = await execution.ExecuteAsync(module);
    var afterExecute = Counter.Read();
    compiler.AcceptSubmission(execution.GetLocalValue);
    var afterAccept = Counter.Read();
    int bindings = describe ? compiler.DescribeBindings().Count : 0;
    if (describe) _ = compiler.DescribeConcepts();
    var afterDescribe = Counter.Read();
    if (value is not NumberValue number || number.Data != index)
        throw new InvalidOperationException($"Request {index} returned {value}, expected retained-state increment.");
    observations.Add(new(index, compiler.BindingNames.Count, bindings, module.Functions.Count,
        Phase.Between(before, afterCompile), Phase.Between(afterCompile, afterExecute),
        Phase.Between(afterExecute, afterAccept), Phase.Between(afterAccept, afterDescribe)));
}
Dictionary<string, string> assemblies = new(StringComparer.Ordinal);
foreach (string name in new[] { "Msync.Core", "Msync.Hir", "Msync.Compiler", "Msync.Optimization", "Msync.Runtime" })
    assemblies[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name + ".dll"))));
Report report = new(scenario, describe, RuntimeInformation.FrameworkDescription,
    Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), assemblies, observations.ToArray());
string output = Path.GetFullPath(Option("--output", ".temp/repl-tail/component.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ProbeJson.Default.Report));
Console.WriteLine($"PASS {count} exact session results; report {output}");

async Task Submit(string source)
{
    Module module = compiler.Compile(source);
    await execution.ExecuteAsync(module);
    compiler.AcceptSubmission(execution.GetLocalValue);
}

string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}

readonly record struct Counter(long Timestamp, long Allocated, int Gen0, int Gen1, int Gen2)
{
    public static Counter Read() => new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(precise: false),
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}
sealed record Phase(double Milliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2)
{
    public static Phase Between(Counter before, Counter after) => new(
        Stopwatch.GetElapsedTime(before.Timestamp, after.Timestamp).TotalMilliseconds,
        after.Allocated - before.Allocated, after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2);
}
sealed record Observation(int Submission, int Exports, int DescribedBindings, int Functions,
    Phase Compile, Phase Execute, Phase Accept, Phase Describe);
sealed record Report(string Case, bool DescribeBindings, string Framework, string? TieredCompilation,
    Dictionary<string, string> Assemblies, Observation[] Samples);
[JsonSerializable(typeof(Report))]
internal partial class ProbeJson : JsonSerializerContext;
