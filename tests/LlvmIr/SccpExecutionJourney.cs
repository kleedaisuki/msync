using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal sealed record SccpReceipt(int SourceCases, int Executions, int ArtifactExecutions, int ChangedFunctions);

internal static class SccpExecutionJourney
{
    private sealed record Case(string Name, string Source, double Expected);

    private static readonly Case[] Cases =
    [
        new("constant-branch", """
            let value = 2;
            let answer = 0;
            if value * 3 == 6 { answer = 41; } else { answer = 99; };
            answer + 1
            """, 42),
        new("loop-carried", """
            let index = 0;
            let total = 0;
            loop {
                if index >= 4 { break total; };
                total = total + 2;
                index = index + 1;
            }
            """, 8),
        new("nullable-value", """
            let value: Number? = 7;
            if value is null { 0 } else { value }
            """, 7),
        new("scope-store", """
            let item = scope { value: 1 };
            let choice = 3;
            if choice == 3 { item::value = 9; } else { item::value = 22; };
            item::value
            """, 9),
        new("function-descriptor", """
            fn increment(value: Number) { value + 1 };
            increment(8)
            """, 9),
        new("range-function-value", """
            let effects = 0;
            let items = [fn { effects = effects + 1; 9 }];
            let count = 0;
            for item in items { count = count + 1; };
            effects * 10 + count
            """, 1)
    ];

    internal static async Task<SccpReceipt> RunAsync(string? output)
    {
        string root = Path.Combine(output ?? Path.GetFullPath(".temp/llvm-ir"), "sccp");
        int executions = 0, artifacts = 0, changed = 0;
        foreach (Case test in Cases)
        {
            string work = Path.Combine(root, test.Name);
            Directory.CreateDirectory(work);
            string source = Path.Combine(work, "source.msync");
            File.WriteAllText(source, test.Source);
            LlvmHirLoweringResult graph = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, source));
            File.WriteAllText(Path.Combine(work, "before.ll"), LlvmIrWriter.Write(graph.Module));
            foreach (LlvmFunction function in graph.Module.Functions.Where(function => function.BasicBlocks.Count != 0))
            {
                LlvmPromoteMemoryToRegister.Run(function, new LlvmDominatorTree(function));
                if (LlvmSccp.Run(function)) changed++;
                LlvmDeadCodeElimination.Run(function);
            }
            if (test.Name == "constant-branch")
                Program.Require(!graph.Entry.BasicBlocks.SelectMany(block => block.Instructions)
                    .OfType<LlvmCondBrInstruction>().Any(),
                    "SCCP must remove the infeasible constant source branch.");
            File.WriteAllText(Path.Combine(work, "after.ll"), LlvmIrWriter.Write(graph.Module));
            NativeProgram program = LlvmNativeLowerer.Lower(graph);
            await Expect(program, work, source, test.Expected);
            executions++;
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            File.WriteAllBytes(Path.Combine(work, "program.native"), bytes.ToArray());
            bytes.Position = 0;
            await Expect(NativeArtifact.Read(bytes), work, source, test.Expected);
            executions++;
            artifacts++;
        }
        return new(Cases.Length, executions, artifacts, changed);
    }

    private static async Task Expect(NativeProgram program, string work, string source, double expected)
    {
        await using NativeInterpreter interpreter = new(program,
            new ExecutionOptions(WorkingDirectory: work, SourceFile: source));
        NativeValue result = await interpreter.ExecuteAsync();
        Program.Require(result.AsNumber() == expected,
            $"{source}: expected {expected}, actual {result.AsNumber()}.");
    }
}
