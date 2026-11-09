using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

namespace Msync.SchedulingComponents;

internal sealed record Options(string Root, string Output, int[] Sizes, string[] Cases, int Samples, int Warmups, int Jobs)
{
    internal static Options Parse(string[] arguments)
    {
        string root = Path.GetFullPath(".temp/scheduler-components");
        string? output = null;
        int[] sizes = [1, 100, 1000];
        string[] cases = ["independent", "observe_directory", "chain", "gated_literal", "gated_shared"];
        int samples = 7, warmups = 2, jobs = 4;
        for (int index = 0; index < arguments.Length; index++)
        {
            string option = arguments[index], value = arguments[++index];
            switch (option)
            {
                case "--root": root = Path.GetFullPath(value); break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--sizes": sizes = value.Split(',').Select(Number).ToArray(); break;
                case "--cases": cases = value.Split(','); break;
                case "--samples": samples = Number(value); break;
                case "--warmups": warmups = Number(value); break;
                case "--jobs": jobs = Number(value); break;
                default: throw new ArgumentException($"Unknown scheduling probe option '{option}'");
            }
        }
        return new(root, output ?? Path.Combine(root, "report.json"), sizes, cases, samples, warmups, jobs);
    }
    private static int Number(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}

internal sealed record Image(string Executable, string Sha256, string RuntimeBuild, string CoreBuild, string Framework, string Architecture);
internal sealed record Observation(string Case, int Size, int Iteration, bool Warmup, double ConstructMilliseconds,
    double? AdmissionMilliseconds, double ExecutionMilliseconds, double? DrainMilliseconds, long ConstructAllocatedBytes,
    long ExecutionAllocatedBytes, long AdmittedTransfers, long ResourceNodesVisited, long DependencyEdges, long NativeHazardWaits,
    string Generation);
internal sealed record Report(Image Image, int Samples, int Warmups, int Jobs, Observation[] Observations);

[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;

internal static class Program
{
    internal static async Task Main(string[] arguments)
    {
        if (arguments is ["--gate-child", var gate, var token]) { await GateChild(gate, token); return; }
        Options options = Options.Parse(arguments);
        Directory.CreateDirectory(options.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        string executable = Environment.ProcessPath!;
        Image image = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            Build(typeof(Interpreter)), Build(typeof(Module)), System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        List<Observation> observations = [];
        foreach (string scenario in options.Cases)
            foreach (int size in options.Sizes)
            {
                for (int iteration = 0; iteration < options.Warmups + options.Samples; iteration++)
                {
                    Observation observation = await Run(options, scenario, size, iteration);
                    observations.Add(observation);
                    Save(options, image, observations);
                }
                Observation[] accepted = observations.Where(item => item.Case == scenario && item.Size == size && !item.Warmup).ToArray();
                Console.WriteLine($"{scenario,-18} n={size,-5} execution={Median(accepted.Select(item => item.ExecutionMilliseconds)):F3} ms "
                    + $"allocated={Median(accepted.Select(item => (double)item.ExecutionAllocatedBytes)):F0} B "
                    + $"resource-visits={accepted[0].ResourceNodesVisited}");
            }
    }

    private static async Task<Observation> Run(Options options, string scenario, int size, int iteration)
    {
        string directory = Path.Combine(options.Root, scenario, size.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        string outputs = Path.Combine(directory, "outputs");
        Directory.CreateDirectory(outputs);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        byte[] payload = Encoding.ASCII.GetBytes(token);
        string input = Path.Combine(directory, "input.bin");
        await File.WriteAllBytesAsync(input, scenario == "gated_shared" ? Encoding.ASCII.GetBytes("stale") : payload);
        string gate = Path.Combine(directory, "gate");
        File.Delete(gate);
        string[] destinations = Enumerable.Range(0, size).Select(index => Path.Combine(outputs, $"item-{index:D6}.bin")).ToArray();
        Module module = Workload(scenario, directory, input, outputs, gate, token, destinations);
        Artifact.Validate(module);
        long allocation = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        Interpreter interpreter = new(module, new(WorkingDirectory: directory, Jobs: options.Jobs, Output: TextWriter.Null, Error: TextWriter.Null));
        double construct = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long constructAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocation;
        allocation = GC.GetTotalAllocatedBytes(precise: true);
        started = Stopwatch.GetTimestamp();
        using CancellationTokenSource watchdog = new(TimeSpan.FromMinutes(2));
        ValueTask<Value> execution = interpreter.ExecuteAsync(watchdog.Token);
        double initial = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        int expectedAdmissions = size + (scenario == "gated_shared" ? 1 : 0);
        bool allAdmitted = interpreter.SchedulingStatistics.AdmittedTransfers == expectedAdmissions;
        if (scenario is "gated_literal" or "gated_shared")
        {
            if (!allAdmitted || execution.IsCompleted)
                throw new InvalidOperationException("The gate probe did not reach all admissions before child completion");
            await File.WriteAllTextAsync(gate, "open", watchdog.Token);
        }
        try { await execution; }
        finally { if (scenario is "gated_literal" or "gated_shared" && !File.Exists(gate)) await File.WriteAllTextAsync(gate, "open"); }
        double total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(precise: true) - allocation;
        if (interpreter.FailedTransfers != 0) throw new InvalidOperationException("A measured transfer failed");
        foreach (string destination in destinations)
            if (!(await File.ReadAllBytesAsync(destination)).AsSpan().SequenceEqual(payload))
                throw new InvalidOperationException($"A measured output is stale or incorrect: {destination}");
        SchedulingStatistics statistics = interpreter.SchedulingStatistics;
        if (statistics.AdmittedTransfers != expectedAdmissions) throw new InvalidOperationException("An operation was not admitted");
        return new(scenario, size, iteration, iteration < options.Warmups, construct, allAdmitted ? initial : null, total,
            allAdmitted ? total - initial : null, constructAllocated, bytes, statistics.AdmittedTransfers,
            statistics.ResourceNodesVisited, statistics.DependencyEdges, statistics.NativeHazardWaits, token);
    }

    private static Module Workload(string scenario, string directory, string input, string outputs, string gate, string token, string[] destinations)
    {
        List<Value> constants = [];
        List<Instruction> code = [];
        void Constant(Value value) { int index = constants.Count; constants.Add(value); code.Add(new(OpCode.Constant, index)); }
        int locals = 0;
        if (scenario is "gated_literal" or "gated_shared")
        {
            Constant(new PathValue(Environment.ProcessPath!)); Constant(new StringValue("--gate-child"));
            Constant(new StringValue(gate)); Constant(new StringValue(token));
            code.Add(new(OpCode.ProcessCall, 3, 1)); code.Add(new(OpCode.GetMember, Text: "stdout"));
            code.Add(new(OpCode.StoreLocal, 0)); locals = 1;
        }
        if (scenario == "gated_shared")
        {
            code.Add(new(OpCode.LoadLocal, 0)); Constant(new PathValue(input));
            code.Add(new(OpCode.LocalTransfer)); code.Add(new(OpCode.Pop));
        }
        string previous = input;
        foreach (string destination in destinations)
        {
            if (scenario == "gated_literal") code.Add(new(OpCode.LoadLocal, 0));
            else Constant(new PathValue(previous));
            Constant(new PathValue(destination)); code.Add(new(OpCode.LocalTransfer)); code.Add(new(OpCode.Pop));
            if (scenario == "chain") previous = destination;
            else if (scenario == "observe_directory")
            {
                code.Add(new(OpCode.Acquire)); Constant(new PathValue(outputs));
                code.Add(new(OpCode.NativeCall, 1, Text: "fs.exists")); code.Add(new(OpCode.Pop));
            }
            else if (scenario is not ("independent" or "gated_literal" or "gated_shared")) throw new ArgumentException($"Unknown scenario '{scenario}'");
        }
        code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
        return Optimizer.FormBlocks(new(constants, [new("main", 0, locals, code, TypeRef.Null)], Stage: ArtifactStage.Executable,
            Context: new(directory, directory), OptimizationLevel: 0));
    }

    private static async Task GateChild(string gate, string token)
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(Path.GetDirectoryName(gate)!, Path.GetFileName(gate))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        void Changed(object sender, FileSystemEventArgs arguments) { if (File.Exists(gate)) ready.TrySetResult(); }
        watcher.Created += Changed; watcher.Changed += Changed; watcher.EnableRaisingEvents = true;
        if (File.Exists(gate)) ready.TrySetResult();
        await ready.Task;
        Console.Write(token);
    }

    private static void Save(Options options, Image image, List<Observation> observations) =>
        File.WriteAllText(options.Output, JsonSerializer.Serialize(new Report(image, options.Samples, options.Warmups, options.Jobs, observations.ToArray()), ReportJson.Default.Report));
    private static string Build(Type type) => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    private static double Median(IEnumerable<double> values) { double[] sorted = values.Order().ToArray(); return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2]; }
}
