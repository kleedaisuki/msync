using System.Collections.Immutable;
using System.Reflection;
using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;

internal static class ScopeLayoutAliasChecks
{
    internal static async Task<int> RunAsync()
    {
        const string source = "fn edge_value(edge) { edge::ay + edge::by }; edge_value(scope { ax:1, ay:2, bx:3, by:4 })";
        var hir = Compiler.Analyze(source, "scope-layout-alias.msync");
        int functionIndex = Enumerable.Range(0, hir.Functions.Length).Single(index => hir.Functions[index].Name == "edge_value");
        var function = hir.Functions[functionIndex];
        int shape = Enumerable.Range(0, hir.ScopeLayouts.Length).First(index =>
            hir.ScopeLayouts[index].Fields.Select(field => field.Name).SequenceEqual(new[] { "ax", "ay", "bx", "by" }));
        int aliasShape = hir.ScopeLayouts.Length;
        var aliasType = new HirTypeId(hir.Types.Length);
        var parameters = function.ParameterEntryTypes.IsDefaultOrEmpty
            ? function.Parameters.Select(parameter => hir.Locations[parameter.Value].StorageType).ToImmutableArray()
            : function.ParameterEntryTypes;
        hir = hir with
        {
            Types = hir.Types.Add(HirType.ConstructedScope(aliasShape)),
            ScopeLayouts = hir.ScopeLayouts.Add(hir.ScopeLayouts[shape]),
            Functions = hir.Functions.SetItem(functionIndex, function with { ParameterEntryTypes = parameters.SetItem(0, aliasType) })
        };
        var graph = LlvmHirLowerer.Lower(hir);
        var formal = graph.SourceFunctions.Single(pair => pair.Value.SourceFunction?.Value == functionIndex).Key;
        int nativeLayout = graph.Types[formal.Arguments[0].LogicalType].Layouts!.Single();
        Program.Require(graph.StructLayouts.Any(pair => pair.Value == nativeLayout),
            "A distinct formal row shape must publish its declared LLVM storage structure even without an allocation of that shape ID.");
        typeof(Compiler).GetMethod("OptimizeLlvm", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [graph, 2, null]);
        NativeProgram program = LlvmNativeLowerer.Lower(graph);
        foreach (var candidate in new[] { program, RoundTrip(program) })
        {
            await using var interpreter = new NativeInterpreter(candidate);
            Program.Require((await interpreter.ExecuteAsync()).AsNumber() == 6,
                "Byte-GEP lowering changed a structural formal's physical member access.");
        }
        return 3;
    }

    internal static async Task<int> RunZeroSizeAsync()
    {
        const string source = "let text=\"\"; for n in $std::ranges::generate(4, fn(index:Number) { index }) { text=$std::string::concat(text,n as String); }; text";
        var graph = LlvmHirLowerer.Lower(Compiler.Analyze(source, "generated-index-storage.msync"));
        typeof(Compiler).GetMethod("OptimizeLlvm", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [graph, 2, null]);
        Program.Require(graph.Module.Functions.SelectMany(function => function.BasicBlocks)
            .SelectMany(block => block.Instructions).OfType<LlvmLoadInstruction>().Any(load =>
                load.Type.IsDoubleTy() && load.GetOperandUse(0).TargetStorageType is { } structure &&
                graph.Layouts[graph.StructLayouts[structure]].Fields.Any(field => field.Name == "index")),
            "The actual generated-index zero GEP must retain its declared builtin storage view after folding.");
        NativeProgram program = LlvmNativeLowerer.Lower(graph);
        foreach (var candidate in new[] { program, RoundTrip(program) })
        {
            await using var interpreter = new NativeInterpreter(candidate);
            Program.Require((await interpreter.ExecuteAsync()).AsString() == "0123",
                "Generate must consume each actual index, not repeatedly read a semantic Null field as numeric zero.");
        }
        return 3;
    }

    internal static async Task<int> RunBuiltinRecordsAsync()
    {
        int checks = 0;
        foreach (var test in new[]
        {
            (Name: "zip", Source: "fn second(row) { row::second }; let total=0; for row in $std::ranges::zip([1,2],[10,20]) { total=total+second(row); }; total", Expected: 30.0),
            (Name: "enumerate", Source: "fn value(row) { row::value }; let total=0; for row in $std::ranges::enumerate([10,20]) { total=total+value(row); }; total", Expected: 30.0)
        })
        {
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, "builtin-record-" + test.Name + ".msync"));
            typeof(Compiler).GetMethod("OptimizeLlvm", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [graph, 2, null]);
            var program = LlvmNativeLowerer.Lower(graph);
            foreach (var candidate in new[] { program, RoundTrip(program) })
            {
                await using var interpreter = new NativeInterpreter(candidate);
                Program.Require((await interpreter.ExecuteAsync()).AsNumber() == test.Expected,
                    "Builtin record physical member access changed after full O2: " + test.Name);
                ++checks;
            }
        }
        const string functions = "let text=\"\"; for row in $std::ranges::enumerate([fn {2},fn {3}]) { text=$std::string::concat(text,row::value() as String); }; text";
        var functionGraph = LlvmHirLowerer.Lower(Compiler.Analyze(functions, "builtin-record-enumerated-functions.msync"));
        typeof(Compiler).GetMethod("OptimizeLlvm", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [functionGraph, 2, null]);
        var functionProgram = LlvmNativeLowerer.Lower(functionGraph);
        foreach (var candidate in new[] { functionProgram, RoundTrip(functionProgram) })
        {
            await using var interpreter = new NativeInterpreter(candidate);
            Program.Require((await interpreter.ExecuteAsync()).AsString() == "23",
                "Enumerated Function components lost the parent's declared physical storage after GEP flattening.");
            ++checks;
        }
        return checks;
    }

    private static NativeProgram RoundTrip(NativeProgram program)
    {
        using MemoryStream bytes = new();
        NativeArtifact.Write(program, bytes);
        bytes.Position = 0;
        return NativeArtifact.Read(bytes);
    }
}
