using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;

internal static class TypeBridgeChecks
{
    internal static int Run()
    {
        Check([new(TypeKind.Number), new(TypeKind.Number | TypeKind.String | TypeKind.Bool),
            new(TypeKind.Number | TypeKind.String)], []);
        Check([new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Scope, Layouts: [0, 1, 2]),
            new(TypeKind.Scope, Layouts: [0, 1])], [new([]), new([]), new([])]);
        return 6;
    }

    private static void Check(NativeCodeType[] types, NativeCodeLayout[] layouts)
    {
        NativeProgram program = new([], [new("type-bridge", [new(0, NativeSlotRole.Parameter), new(1), new(2)],
            [new(NativeOp.Copy, 1, 0), new(NativeOp.Narrow, 2, 1, Type: 2), new(NativeOp.Return, A: 2)],
            new([0], 2, []), ParameterCount: 1)], types, layouts);
        NativeArtifact.Validate(program);
        NativeProgram optimized = NativeOptimizer.Optimize(program);
        NativeArtifact.Validate(optimized);
        NativeFunctionCode function = optimized.Functions[0];
        NativeInstruction narrow = function.Instructions.Single(instruction => instruction.Op == NativeOp.Narrow);
        if (function.Slots[narrow.A].Type != 1)
            throw new InvalidOperationException("Copy propagation erased the widening ABI required by a later Narrow.");
        using MemoryStream bytes = new();
        NativeArtifact.Write(optimized, bytes);
        bytes.Position = 0;
        NativeProgram decoded = NativeArtifact.Read(bytes);
        if (!decoded.Functions[0].Instructions.SequenceEqual(function.Instructions))
            throw new InvalidOperationException("A checked type bridge changed after artifact roundtrip.");
        NativeArtifact.Validate(NativeOptimizer.Optimize(decoded));
    }
}
