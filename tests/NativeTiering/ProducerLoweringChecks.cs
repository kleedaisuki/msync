using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class ProducerLoweringChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 7)],
            [new("producer-consumer", [new(0), new(2), new(1), new(3), new(0)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeFunction, 1, 1, C: 0),
                 new(NativeOp.Call, 2, 1, Inputs: [0]), new(NativeOp.ProjectAddress, 3, 2, 0),
                 new(NativeOp.Load, 4, 3, Type: 0), new(NativeOp.Return, A: 4)], new([], 0, [])),
             new("fresh-producer", [new(0, NativeSlotRole.Parameter), new(1), new(3)],
                [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
                 new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.Return, A: 1)],
                new([0], 1, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Function),
             new(TypeKind.Never, Pointee: 0)], [new([new("item", 0)])])
        { Projections = [new([new(0, 0)])] };
        NativeArtifact.Validate(program);
        NativeImage image = new(program, new());
        NativeExecutionFunction execution = new(image, 0, new(NativeJitPolicy.Force));
        if (!execution.Operands[3].FixedLayout || !execution.RawWritable[0] || !execution.RawWritable[4])
            throw new Exception("Backend lowering failed to consume the shared actual producer proof.");
        using MemoryStream wire = new();
        NativeArtifact.Write(program, wire);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            wire.Position = 0;
            await using NativeInterpreter interpreter = new(NativeArtifact.Read(wire),
                new() { Tiers = new(policy, 1, 1, 1) });
            if ((await interpreter.ExecuteAsync()).AsNumber() != 7)
                throw new Exception($"Actual producer lowering changed the value under {policy}.");
            if (policy == NativeJitPolicy.Force && interpreter.TierStatistics.RawOperations == 0)
                throw new Exception("The producer proof was not exercised by an actual native raw operation.");
        }
        return 4;
    }
}
