using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime.Native;

internal static class SroaSourceJourney
{
    internal static async Task<int> RunAsync()
    {
        int executions = 0;
        foreach (var test in new[]
        {
            (Name: "captured-location", Source: File.ReadAllText("tests/LlvmIr/Fixtures/CapturedOperandPhi.msync"), Expected: 79.0),
            (Name: "callable-select", Source: File.ReadAllText("tests/LlvmIr/Fixtures/DynamicchooseFns.msync"), Expected: 56.0),
            (Name: "range-object", Source: "let object=scope {value:1}; let items=[object,object]; for item in items {item::value=item::value+1;}; let read=fn {object::value}; read()", Expected: 3.0)
        })
        {
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, "sroa-" + test.Name + ".msync"));
            for (int index = 0; index < graph.Module.Functions.Count; ++index)
            {
                var function = graph.Module.Functions[index];
                if (function.IsDeclaration) continue;
                using var assumptions = new LlvmAssumptionCache(function);
                LlvmSroa.Run(function, new(function), assumptions, new() { PreserveCFG = false });
            }
            NativeProgram original = LlvmNativeLowerer.Lower(graph);
            using MemoryStream bytes = new(); NativeArtifact.Write(original, bytes); bytes.Position = 0;
            foreach (NativeProgram program in new[] { original, NativeArtifact.Read(bytes) })
            {
                await using NativeInterpreter interpreter = new(program);
                NativeValue result = await interpreter.ExecuteAsync();
                Program.Require(result.AsNumber() == test.Expected, "Source SROA result changed: " + test.Name);
                ++executions;
            }
        }
        return executions;
    }
}
