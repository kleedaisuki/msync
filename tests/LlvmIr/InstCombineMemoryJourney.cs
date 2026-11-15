using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class InstCombineMemoryJourney
{
    internal static async Task<int> RunAsync(string? output)
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("shared-scope-fields", """
                let item = scope { left: 1, right: 2 };
                let alias = item;
                item::left = 3;
                alias::left = 9;
                item::left + alias::right
                """, 11),
            ("branch-field-store", """
                fn update(flag: Bool) {
                    let item = scope { value: 1 };
                    if flag { item::value = 7; } else { item::value = 9; };
                    item::value
                };
                update(true) + update(false)
                """, 16),
            ("captured-field-address", """
                let item = scope { value: 2 };
                let reader = fn { item::value };
                item::value = 11;
                reader()
                """, 11)
        ];
        int executions = 0;
        foreach (var test in cases)
        {
            string work = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "instcombine-memory", test.Name);
            Directory.CreateDirectory(work);
            string source = Path.Combine(work, "source.msync");
            File.WriteAllText(source, test.Source);
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, source));
            File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
            foreach (var function in graph.Module.Functions.Where(function => function.BasicBlocks.Count != 0).ToArray())
            {
                var tree = new LlvmDominatorTree(function);
                LlvmPromoteMemoryToRegister.Run(function, tree);
                using var assumptions = new LlvmAssumptionCache(function);
                LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                    new LlvmTargetLibraryInfo(graph.Module), new LlvmTargetTransformInfo(graph.Module.DataLayout), tree);
            }
            File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
            var program = LlvmNativeLowerer.Lower(graph);
            await Expect(program, work, source, test.Expected); ++executions;
            using var artifact = new MemoryStream();
            NativeArtifact.Write(program, artifact);
            File.WriteAllBytes(Path.Combine(work, "program.native"), artifact.ToArray());
            artifact.Position = 0;
            await Expect(NativeArtifact.Read(artifact), work, source, test.Expected); ++executions;
        }
        return executions;
    }

    private static async Task Expect(NativeProgram program, string work, string source, double expected)
    {
        await using var interpreter = new NativeInterpreter(program, new ExecutionOptions(WorkingDirectory: work, SourceFile: source));
        var result = await interpreter.ExecuteAsync();
        Program.Require(result.AsNumber() == expected, $"{source}: expected {expected}, actual {result.AsNumber()}.");
    }
}
