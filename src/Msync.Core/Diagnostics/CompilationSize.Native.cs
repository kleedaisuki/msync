using Msync.Core.Native;

namespace Msync.Core;

public readonly partial record struct CompilationSize
{
    public static CompilationSize Of(NativeFunctionCode function) =>
        new(1, function.Instructions.Count, function.Slots.Count);

    public static CompilationSize Of(NativeProgram program)
    {
        CompilationSize size = new(program.Functions.Count, 0, 0);
        foreach (NativeFunctionCode function in program.Functions)
            size = size with
            {
                Instructions = size.Instructions + function.Instructions.Count,
                Locals = size.Locals + function.Slots.Count
            };
        return size;
    }
}
