using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;

internal static class MemoryBarrierChecks
{
    internal static int Run()
    {
        NativeValue[] samples = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(7),
            NativeValue.FromString("Klee𐐀"), NativeValue.FromPath(new("./file", "/origin")),
            NativeValue.FromConnection(new(ConnectionKind.Ssh, "alias")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromFunction(new(0)), NativeValue.FromRange(NativeRange.Snapshot([NativeValue.True], TypeRef.Bool)),
            NativeValue.FromProcess(new(0, "output", "")), NativeValue.FromAddress(new NativeAllocation(new()).Address)];
        int checks = 0;
        foreach (NativeValue sample in samples)
        {
            foreach (bool rebind in new[] { false, true })
            {
                NativeProgram original = IndependentStorage(sample.Type.Kind, rebind);
                NativeProgram optimized = NativeOptimizer.Optimize(original);
                NativeArtifact.Validate(original); NativeArtifact.Validate(optimized);
                if (Machine.Run(original, [sample]) != sample || Machine.Run(optimized, [sample]) != sample)
                    throw new InvalidOperationException("A private drop/rebinding changed another location's value.");
                if (optimized.Functions[0].Instructions.Any(instruction => instruction.Op == NativeOp.Load))
                    throw new InvalidOperationException("A private drop/rebinding erased unrelated memory facts.");
                checks += 3;
            }
        }
        RebindSnapshot();
        ExposedDrop();
        return checks + 4;
    }

    private static NativeProgram IndependentStorage(TypeKind kind, bool rebind)
    {
        List<NativeInstruction> code = [new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
            new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0)];
        if (rebind)
        {
            code.Add(new(NativeOp.Allocate, 5, 0));
            code.Add(new(NativeOp.AddressOffset, 6, 5, 0, 0));
            code.Add(new(NativeOp.BindLocation, A: 7, B: 6));
        }
        else
        {
            code.Add(new(NativeOp.Constant, 4, 0));
            code.Add(new(NativeOp.Drop, A: 4));
        }
        code.Add(new(NativeOp.Load, 3, 2, Type: 0)); code.Add(new(NativeOp.Return, A: 3));
        return new([new(NativeKind.Null)], [new("independent-storage", [new(0, NativeSlotRole.Parameter),
            new(1), new(2), new(0), new(3), new(1), new(2), new(0, NativeSlotRole.Local)], code,
            new([0], 0, []), ParameterCount: 1)],
            [new(kind), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0), new(TypeKind.Null)],
            [new([new("value", 0)])]);
    }

    private static void RebindSnapshot()
    {
        NativeProgram original = new([new(NativeKind.Number, Number: 7), new(NativeKind.Number, Number: 9)],
            [new("rebind-snapshot", [new(0), new(1), new(2), new(0, NativeSlotRole.Local),
                new(0), new(1), new(2), new(0), new(0), new(0), new(2)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
                 new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.BindLocation, A: 3, B: 2),
                 new(NativeOp.Load, 4, 2, Type: 0), new(NativeOp.Allocate, 5, 0), new(NativeOp.AddressOffset, 6, 5, 0, 0),
                 new(NativeOp.Constant, 7, 1), new(NativeOp.Store, A: 6, B: 7, C: 1, Type: 0),
                 new(NativeOp.BindLocation, A: 3, B: 6), new(NativeOp.FrameAddress, 10, 3), new(NativeOp.Load, 8, 10, Type: 0),
                 new(NativeOp.Binary, 9, 4, 8, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 9)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)], [new([new("value", 0)])]);
        NativeProgram optimized = NativeOptimizer.Optimize(original);
        NativeArtifact.Validate(original); NativeArtifact.Validate(optimized);
        if (Machine.Run(original, []).AsNumber() != 16 || Machine.Run(optimized, []).AsNumber() != 16)
            throw new InvalidOperationException("Rebinding changed a value already copied from the old location.");
    }

    private static void ExposedDrop()
    {
        NativeProgram original = new([new(NativeKind.Number, Number: 7)],
            [new("exposed-drop", [new(0), new(1), new(2), new(0, NativeSlotRole.Local), new(0)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.Allocate, 1, 0), new(NativeOp.AddressOffset, 2, 1, 0, 0),
                 new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0), new(NativeOp.BindLocation, A: 3, B: 2),
                 new(NativeOp.Constant, 3, 0), new(NativeOp.Drop, A: 3), new(NativeOp.Load, 4, 2, Type: 0), new(NativeOp.Return, A: 4)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)], [new([new("value", 0)])]);
        foreach (NativeProgram program in new[] { original, NativeOptimizer.Optimize(original) })
        {
            NativeArtifact.Validate(program);
            try { _ = Machine.Run(program, []); }
            catch (InvalidOperationException error) when (error.Message.Contains("uninitialized", StringComparison.Ordinal)) { continue; }
            throw new InvalidOperationException("Dropping a bound location must still invalidate its aliased payload.");
        }
    }
}
