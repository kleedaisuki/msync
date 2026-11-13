using System.Diagnostics;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class PerformanceCases
{
    internal static void Run()
    {
        string root = Environment.CurrentDirectory;
        BindingContext context = new(root, root, Path.Combine(root, "script.msync"));
        Dictionary<string, object> results = [];
        foreach ((string name, string source) in new[] { ("literal", "true"), ("copy-1", Copies(1)), ("copy-128", Copies(128)), ("branch-128", Branches(128)) })
        {
            NativeProgram program = Compiler.Compile(source, "preparation-cost.msync");
            NativeArtifact.Validate(program);
            for (int index = 0; index < 10; index++) _ = NativePreparation.Prepare(program, context);
            int count = name == "branch-128" ? 40 : 300;
            double[] elapsed = new double[count];
            long[] allocations = new long[count];
            for (int index = 0; index < count; index++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                NativeProgram bound = NativePreparation.Prepare(program, context);
                elapsed[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocations[index] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (bound.Stage != ArtifactStage.Executable) throw new InvalidOperationException("Not bound");
            }
            Array.Sort(elapsed); Array.Sort(allocations);
            results[name] = new { stage = "native_preparation", medianMs = elapsed[count / 2], p95Ms = elapsed[(int)(count * .95)], bytes = allocations[count / 2], instructions = program.Functions.Sum(function => function.Instructions.Count) };
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static string Copies(int count) => string.Join('\n', Enumerable.Range(0, count).Select(index => $"\"payload-{index}\" -> ./output{index};"));
    private static string Branches(int count) => string.Join('\n', Enumerable.Range(0, count).Select(index =>
        $"if $std::fs::exists(./selected) {{ ($local @ ./input{index}) -> ./output{index}; }};"));
}
