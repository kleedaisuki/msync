using Msync.Compiler;
using Msync.Core.Llvm;
using Msync.Core.Native;
using Msync.Hir;
using Msync.Runtime.Native;

internal static class FreezeLoweringChecks
{
    internal static async Task<int> RunAsync()
    {
        int executions = 0;
        foreach (bool whole in new[] { false, true })
        {
            const string source = "fn make(seed: Number) { fn { seed } }; let read = make(9); read()";
            var graph = LlvmHirLowerer.Lower(Compiler.Analyze(source, "freeze-function.msync"));
            var definitions = graph.Module.Functions.SelectMany(function => function.BasicBlocks)
                .SelectMany(block => block.Instructions).Where(instruction => instruction.Type is LlvmStructType
                    { HasName: true, Name: "msync.function" }
                    && (whole ? instruction is LlvmCallInstruction : instruction is LlvmInsertValueInstruction
                        && instruction.Uses.Any(use => use.User is not LlvmInsertValueInstruction))).ToArray();
            if (definitions.Length == 0) throw new InvalidOperationException("The source must contain actual whole/split Function definitions.");
            foreach (var definition in definitions)
            {
                var uses = definition.Uses.ToArray();
                var freeze = new LlvmFreezeInstruction(definition) { LogicalType = definition.LogicalType, Source = definition.Source };
                definition.Parent!.InsertBefore(freeze, definition.Next);
                foreach (var use in uses) use.Set(freeze);
            }
            executions += await ExecuteAsync(LlvmNativeLowerer.Lower(graph), 9);
        }
        var scalar = LlvmHirLowerer.Lower(Compiler.Analyze("0", "freeze-undef.msync"));
        var context = scalar.Module.Context;
        var module = new LlvmModule("freeze-undef", context);
        var function = LlvmFunction.Create(LlvmFunctionType.Get(LlvmType.GetDoubleTy(context), []),
            LlvmLinkage.InternalLinkage, "freeze-undef", module);
        var block = new LlvmBasicBlock(context, "entry", function);
        var frozen = new LlvmFreezeInstruction(LlvmUndefValue.Get(LlvmType.GetDoubleTy(context)));
        var difference = new LlvmBinaryOperator(LlvmOpcode.FSub, frozen, frozen);
        block.Append(frozen); block.Append(difference); block.Append(new LlvmReturnInstruction(context, difference));
        int number = scalar.Types.ToList().FindIndex(type => type.Kind == Msync.Core.TypeKind.Number && type.Pointee < 0);
        executions += await ExecuteAsync(LlvmNativeLowerer.Lower(scalar with
        {
            Module = module, Entry = function,
            SourceFunctions = new Dictionary<LlvmFunction, LlvmSourceFunctionInfo> { [function] = new(0, number) }
        }), 0);
        return executions;
    }

    private static async Task<int> ExecuteAsync(NativeProgram program, double expected)
    {
        using MemoryStream bytes = new(); NativeArtifact.Write(program, bytes); bytes.Position = 0;
        foreach (NativeProgram executable in new[] { program, NativeArtifact.Read(bytes) })
        {
            await using NativeInterpreter interpreter = new(executable);
            double actual = (await interpreter.ExecuteAsync()).AsNumber();
            if (actual != expected) throw new InvalidOperationException($"Freeze target: expected {expected}, got {actual}.");
        }
        return 2;
    }
}
