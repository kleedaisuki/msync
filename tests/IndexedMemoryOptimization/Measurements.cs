using Msync.Optimization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal sealed record Observation(string Name, int NativeReads, string ArtifactSha256, double[] OptimizeMs,
    double OptimizeBytes, double[] ExecuteMs, double ExecuteBytes);
internal sealed record Report(string CoreSha256, string RuntimeSha256, Observation[] Observations);
[JsonSerializable(typeof(Report))]
internal sealed partial class MeasurementJson : JsonSerializerContext;

internal static class Measurements
{
    private static object? retained;
    internal static async Task Run()
    {
        List<Observation> observations = [];
        foreach (string name in new[] { "repeated", "size_set", "unique", "no_buffer" })
        {
            Module raw = Build(name);
            Module optimized = Optimizer.Optimize(raw, 2);
            Value result = await Interpreter.ExecuteAsync(optimized);
            double expected = name switch { "repeated" => 163840, "size_set" => 4096, "unique" => 30720, _ => 16384 };
            if (result is not NumberValue number || number.Data != expected) throw new InvalidOperationException(name + ": " + result);
            for (int index = 0; index < 24; index++) retained = Optimizer.Optimize(raw, 2);
            for (int index = 0; index < 5; index++) retained = await Interpreter.ExecuteAsync(optimized);
            double[] optimize = new double[9], execute = new double[9];
            for (int sample = 0; sample < optimize.Length; sample++)
            {
                long begin = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < 8; iteration++) retained = Optimizer.Optimize(raw, 2);
                optimize[sample] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds / 8;
                begin = Stopwatch.GetTimestamp();
                for (int iteration = 0; iteration < 8; iteration++) retained = await Interpreter.ExecuteAsync(optimized);
                execute[sample] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds / 8;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 16; index++) retained = Optimizer.Optimize(raw, 2);
            double optimizeBytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 16.0;
            allocated = GC.GetTotalAllocatedBytes(true);
            for (int index = 0; index < 8; index++) retained = await Interpreter.ExecuteAsync(optimized);
            double executeBytes = (GC.GetTotalAllocatedBytes(true) - allocated) / 8.0;
            using MemoryStream bytes = new(); Artifact.Write(optimized, bytes);
            observations.Add(new(name, optimized.Functions.Sum(function => function.Instructions.Count(instruction =>
                instruction.Op == OpCode.NativeCall && instruction.Text is "storage.buffer_get" or "storage.buffer_size")),
                Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())), optimize, optimizeBytes, execute, executeBytes));
        }
        Report report = new(Hash(typeof(Optimizer).Assembly.Location), Hash(typeof(Interpreter).Assembly.Location), observations.ToArray());
        Console.WriteLine(JsonSerializer.Serialize(report, MeasurementJson.Default.Report));
        GC.KeepAlive(retained);
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static Module Build(string name)
    {
        Code code = new();
        if (name != "no_buffer") code.Buffer(10, 20);
        code.Number(0); code.Emit(OpCode.InitializeLocal, 1);
        code.Number(0); code.Emit(OpCode.InitializeLocal, 2);
        int header = code.Count;
        code.Emit(OpCode.LoadLocal, 1); code.Number(1024); code.Emit(OpCode.Less);
        int end = code.Jump(OpCode.JumpIfFalse);
        code.Emit(OpCode.LoadLocal, 2);
        if (name == "size_set")
        {
            code.Size(); code.Emit(OpCode.Add); code.Set(1, 99); code.Size(); code.Emit(OpCode.Add);
        }
        else
        {
            int reads = name == "unique" ? 2 : 16;
            for (int index = 0; index < reads; index++)
            {
                if (name == "no_buffer") code.Number(1); else code.Get(name == "unique" ? index : 0);
                code.Emit(OpCode.Add);
            }
        }
        code.Emit(OpCode.StoreLocal, 2);
        code.Emit(OpCode.LoadLocal, 1); code.Number(1); code.Emit(OpCode.Add); code.Emit(OpCode.StoreLocal, 1);
        code.Emit(OpCode.Jump, header); code.Target(end);
        code.Emit(OpCode.LoadLocal, 2); code.Emit(OpCode.Return);
        return new(code.Constants, [code.Function()], Stage: ArtifactStage.Executable,
            Context: new(Environment.CurrentDirectory, Environment.CurrentDirectory), OptimizationLevel: 0);
    }
}
