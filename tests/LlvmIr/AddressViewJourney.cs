using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Optimization.Llvm;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class AddressViewJourney
{
    internal static async Task RunAsync()
    {
        (string Name, string Source, double Result)[] cases =
        [
            ("first-number", "let item = scope { value: 1, other: 2 }; item::value = 7; item::value + item::other", 9),
            ("first-function", "let item = scope { run: fn { 4 }, other: 2 }; item::run = fn { 7 }; item::run() + item::other", 9),
            ("captured-root", "let value = 1; fn read() { value }; value = 7; read()", 7)
        ];
        foreach (var test in cases)
        {
            LlvmHirLoweringResult input = LlvmHirLowerer.Lower(Compiler.Analyze(test.Source, "address-view-" + test.Name + ".msync"));
            int before = ZeroGeps(input.Module);
            foreach (var function in input.Module.Functions.Where(function => !function.IsDeclaration))
                LlvmInstSimplifyPass.Run(function);
            Program.Require(before > ZeroGeps(input.Module), test.Name + ": the actual zero-index GEP simplifier must run.");
            NativeProgram program = LlvmNativeLowerer.Lower(input);
            string work = Path.GetFullPath(Path.Combine(".temp", "llvm-ir", "address-views", test.Name));
            Directory.CreateDirectory(work);
            await Execute(program, work, test.Result);
            if (test.Name != "captured-root") continue;
            using MemoryStream artifact = new();
            NativeArtifact.Write(program, artifact);
            artifact.Position = 0;
            await Execute(NativeArtifact.Read(artifact), work, test.Result);
        }
    }

    private static int ZeroGeps(LlvmModule module) => module.Functions.SelectMany(function => function.BasicBlocks)
        .SelectMany(block => block.Instructions).OfType<LlvmGetElementPtrInstruction>()
        .Count(address => address.IndexCount == 2 && address.GetIndex(0) is LlvmConstantInt { IsZero: true }
            && address.GetIndex(1) is LlvmConstantInt { IsZero: true });

    private static async Task Execute(NativeProgram program, string work, double expected)
    {
        await using NativeInterpreter interpreter = new(program, new ExecutionOptions(WorkingDirectory: work));
        NativeValue result = await interpreter.ExecuteAsync();
        Program.Require(result.AsNumber() == expected,
            $"Address view journey {work}: expected {expected}, actual {result.AsNumber()}.");
    }
}
