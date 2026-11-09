using System.Diagnostics;
using System.Text.Json;
using Msync.Core;
using Module = Msync.Core.Module;

internal static class PerformanceCases
{
    internal static void Run()
    {
        string root = Environment.CurrentDirectory;
        BindingContext context = new(root, root, Path.Combine(root, "script.msync"));
        Dictionary<string, object> results = [];
        foreach ((string name, Module module) in new[] { ("literal", Literal()), ("copy-1", Copies(1)), ("copy-128", Copies(128)), ("branch-128", Branches(128)) })
        {
            Artifact.Validate(module);
            Module optimized = Optimizer.Optimize(module);
            for (int i = 0; i < 10; i++) _ = Binding.Bind(optimized, context);
            int samples = name == "branch-128" ? 40 : 300;
            double[] elapsed = new double[samples];
            long[] allocations = new long[samples];
            for (int i = 0; i < samples; i++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                Module bound = Binding.Bind(optimized, context);
                elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                allocations[i] = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (bound.Stage != ArtifactStage.Executable) throw new Exception("not bound");
            }
            Array.Sort(elapsed); Array.Sort(allocations);
            results[name] = new { medianMs = elapsed[samples / 2], p95Ms = elapsed[(int)(samples * .95)], bytes = allocations[samples / 2], instructions = optimized.Functions.Sum(f => f.Instructions.Count) };
        }
        Console.WriteLine(JsonSerializer.Serialize(results));
    }

    private static Module Literal() => new([], [new("main", 0, 0, [new(OpCode.True), new(OpCode.Return)], TypeRef.Bool)]);
    private static Module Copies(int count)
    {
        List<Value> constants = []; List<Instruction> code = [];
        for (int i = 0; i < count; i++)
        {
            constants.Add(new PathValue($"./input{i}")); constants.Add(new PathValue($"./output{i}"));
            code.Add(new(OpCode.Constant, i * 2)); code.Add(new(OpCode.Constant, i * 2 + 1)); code.Add(new(OpCode.Transfer)); code.Add(new(OpCode.Pop));
        }
        code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
        return new(constants, [new("main", 0, 0, code, TypeRef.Null)]);
    }
    private static Module Branches(int count)
    {
        List<Value> constants = [new PathValue("./selected")]; List<Instruction> code = [];
        for (int i = 0; i < count; i++)
        {
            constants.Add(new PathValue($"./input{i}")); constants.Add(new PathValue($"./output{i}"));
            code.Add(new(OpCode.Constant, 0)); code.Add(new(OpCode.NativeCall, 1, Text: "fs.exists"));
            int jump = code.Count; code.Add(new(OpCode.Nop));
            code.Add(new(OpCode.LoadBuiltin, Text: "local")); code.Add(new(OpCode.Constant, i * 2 + 1)); code.Add(new(OpCode.Qualify));
            code.Add(new(OpCode.Constant, i * 2 + 2)); code.Add(new(OpCode.Transfer)); code.Add(new(OpCode.Pop));
            code[jump] = new(OpCode.JumpIfFalse, code.Count);
        }
        code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
        return new(constants, [new("main", 0, 0, code, TypeRef.Null)]);
    }
}
