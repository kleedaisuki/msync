using Msync.Optimization;
using System.Diagnostics;
using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class LoopPerformance
{
    internal static async Task RunAsync()
    {
        var source = "let total = 0; for item in [" + string.Join(',', Enumerable.Range(1, 16)) + "] { total = total + item; }; total";
        var baseline = Compiler.Compile(source, "loop-performance", 0);
        var constants = baseline.Constants.ToList();
        var method = typeof(Optimizer).Assembly.GetType("Msync.Optimization.LoopOptimization")!.GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
        var loop = baseline with { Functions = baseline.Functions.Select(function => (FunctionCode)method.Invoke(null, [baseline, function, constants, 3])!).ToArray() };
        var full = Optimizer.Optimize(baseline, 3);
        var context = new BindingContext(Environment.CurrentDirectory, HostContext.HomeDirectory);
        Msync.Core.Module[] modules = [Binding.Bind(baseline, context), Binding.Bind(loop, context), Binding.Bind(full, context)];
        for (var warm = 0; warm < 32; warm++)
            foreach (var module in modules) await Interpreter.ExecuteAsync(module);
        List<double>[] timings = [[], [], []];
        List<long>[] allocations = [[], [], []];
        for (var pair = 0; pair < 9; pair++)
            for (var offset = 0; offset < 3; offset++)
                await Sample((pair + offset) % 3);
        Console.WriteLine("variant,instructions,median_ms,allocated_bytes");
        for (var index = 0; index < 3; index++)
            Console.WriteLine($"{new[] { "O0", "loop-only", "O3" }[index]},{modules[index].Functions.Sum(function => function.Instructions.Count)},{timings[index].Order().ElementAt(4):F6},{allocations[index].Order().ElementAt(4)}");

        async Task Sample(int index)
        {
            var allocated = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < 128; iteration++)
                if (await Interpreter.ExecuteAsync(modules[index]) is not NumberValue { Data: 136 }) throw new InvalidOperationException("Loop performance result differs");
            timings[index].Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds / 128);
            allocations[index].Add((GC.GetTotalAllocatedBytes(true) - allocated) / 128);
        }
    }
}
