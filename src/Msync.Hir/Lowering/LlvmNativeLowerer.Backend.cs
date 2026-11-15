using Msync.Core;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private static NativeBackendCode Backend(NativeProgram program, CompilationTrace? trace) => trace is null
        ? BackendImage(program, null)
        : trace.Measure("compiler.llvm.target.image", (program, trace), static state => BackendImage(state.program, state.trace));

    private static NativeBackendCode BackendImage(NativeProgram program, CompilationTrace? trace)
    {
        var functions = new NativeBackendFunction[program.Functions.Count];
        for (int index = 0; index < functions.Length; ++index)
        {
            var function = program.Functions[index];
            functions[index] = trace?.Detail is not { } detail ? NativeStandardBackendEmitter.EmitFunction(program, index)
                : detail.MeasureDetailed("compiler.llvm.target.image.function", (program, index, detail),
                    static state => NativeStandardBackendEmitter.EmitFunction(state.program, state.index, state.detail),
                    attributes: FunctionAttributes(function.Name, index, function.Instructions.FirstOrDefault(instruction => instruction.Source is not null)?.Source));
        }
        trace?.Metric("functions", functions.Length);
        trace?.Metric("instructions", functions.Sum(function => function.Instructions.Count));
        trace?.Metric("handlers.standard", functions.Sum(function => function.Instructions.Count));
        // Standard handlers resolve connections through the existing runtime;
        // no ConnectionQualify specialization or resource-index operands are emitted.
        return new(functions, [new(ConnectionKind.Local)]);
    }
}
