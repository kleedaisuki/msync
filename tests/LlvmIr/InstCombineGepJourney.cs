using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class InstCombineGepJourney
{
    internal static async Task<int> RunAsync(string? output)
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("captured-mixed-layout", """
                fn update(flag: Bool) {
                    let item = scope { prefix: true, value: 2, suffix: "tail" };
                    let alias = item;
                    let reader = fn { alias::value };
                    if flag { item::value = 11; } else { alias::value = 7; };
                    reader()
                };
                update(true) + update(false)
                """, 18),
            ("first-function-field", """
                let item = scope { callback: fn (x: Number) { x + 3 }, value: 4 };
                item::callback(item::value)
                """, 7),
            ("nested-shared-fields", """
                let item = scope { prefix: false, inner: scope { value: 2 }, tail: 3 };
                let alias = item::inner;
                let reader = fn { item::inner::value };
                alias::value = 12;
                reader() + item::tail
                """, 15),
            ("range-shared-field-loop", """
                let first = scope { value: 2 };
                let second = scope { value: 5 };
                for item in [first, second] { item::value = item::value + 1; };
                first::value + second::value
                """, 9)
        ];
        int executions = 0;
        foreach (var test in cases)
        foreach (bool combine in new[] { false, true })
        {
            string work = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "instcombine-gep", test.Name, combine ? "combined" : "raw");
            Directory.CreateDirectory(work);
            string source = Path.Combine(work, "source.msync");
            File.WriteAllText(source, test.Source);
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, source));
            File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
            if (combine)
            {
                foreach (LlvmFunction function in graph.Module.Functions.Where(function => !function.IsDeclaration).ToArray())
                {
                    var tree = new LlvmDominatorTree(function);
                    LlvmPromoteMemoryToRegister.Run(function, tree);
                    using var assumptions = new LlvmAssumptionCache(function);
                    LlvmInstCombine.Run(function, new LlvmAAResults(function, tree: tree), assumptions,
                        new LlvmTargetLibraryInfo(graph.Module), new LlvmTargetTransformInfo(graph.Module.DataLayout), tree);
                }
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
