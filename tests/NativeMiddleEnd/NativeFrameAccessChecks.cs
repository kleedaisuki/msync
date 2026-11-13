using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;
using Msync.Runtime.Native;

internal static class NativeFrameAccessChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        await AllBuiltins(check);
        await BoundLocation(check);
        await CapturedLocation(check);
        await Merge(check);
        await Uninitialized(check);
        await EnvironmentAlias(check);
        await CheckedStore(check);
        await Conversion(check);
        await ForeignAllocation(check);
    }

    private static NativeProgram Detached(NativeProgram program)
    {
        NativeArtifact.Validate(program);
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        stream.Position = 0;
        return NativeArtifact.Read(stream);
    }

    private static async Task ForeignAllocation(Action<bool, string> check)
    {
        NativeProgram source = new([new(NativeKind.Number, Number: 13)], [
            new("foreign-scope", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Local), new(1),
                new(2), new(3), new(1), new(0), new(0), new(0)],
                [new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
                 new(NativeOp.MakeFunction, 3, 1), new(NativeOp.Call, 4, 3, 1),
                 new(NativeOp.AddressOffset, 5, 4, 0, 0), new(NativeOp.Load, 6, 5, Type: 0),
                 new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Load, 7, 2, Type: 0),
                 new(NativeOp.Binary, 8, 6, 7, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 8)],
                new([0], 0, []), ParameterCount: 1),
            new("allocate", [new(3), new(1), new(0)],
                [new(NativeOp.Allocate, 0, 0), new(NativeOp.AddressOffset, 1, 0, 0, 0),
                 new(NativeOp.Constant, 2, 0), new(NativeOp.Store, A: 1, B: 2, C: 1, Type: 0), new(NativeOp.Return, A: 0)],
                new([], 3, []))],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Function), new(TypeKind.Scope, Layouts: [0])],
            [new([new("n", 0)])]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
            if (level != 0)
                check(program.Functions[0].Instructions.All(instruction => instruction.Op != NativeOp.FrameAddress),
                    $"A foreign allocation disabled unrelated private numeric frame lowering at O{level}.");
            await using NativeInterpreter interpreter = new(program);
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromNumber(42)]);
            check(result.AsNumber() == 55, $"Foreign allocation frame lowering changed the result at O{level}.");
        }
    }

    private static async Task AllBuiltins(Action<bool, string> check)
    {
        NativeValue[] values = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0d),
            NativeValue.FromString("layout-𐀀"), NativeValue.FromPath(new("relative", "/origin")),
            NativeValue.FromConnection(new(ConnectionKind.Ssh, "configured-alias")), NativeValue.FromFunction(new(1)),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(7)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "actual", "")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromAddress(new NativeAllocation(new NativeLayout(), 0).Address)];
        foreach (NativeValue value in values)
        {
            NativeCodeType item = new(value.Type.Kind, Element: value.Kind == NativeKind.Range ? 2 : -1,
                Layouts: value.Kind == NativeKind.Address ? [0] : null);
            NativeProgram source = new([new(NativeKind.Null)], [
                new("frame-access", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Local), new(1), new(0)],
                    [new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0),
                     new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.Return, A: 3)],
                    new([0], 0, []), ParameterCount: 1),
                new("callable-value", [new(3)], [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 3, []))],
                [item, new(TypeKind.Never, Pointee: 0), new(TypeKind.Number), new(TypeKind.Null)], [new([])]);
            foreach (int level in new[] { 1, 2, 3 })
            {
                NativeProgram optimized = Detached(NativeOptimizer.Optimize(source, level));
                check(optimized.Functions[0].Instructions.All(instruction => instruction.Op is not
                    (NativeOp.FrameAddress or NativeOp.Load or NativeOp.Store)), value.Kind + ": private frame traffic survived");
                await using NativeInterpreter interpreter = new(optimized, new() { Tiers = new(NativeJitPolicy.Off) });
                NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
                check(result == value, value.Kind + ": frame canonicalization changed value or identity");
                if (value.Kind == NativeKind.Number)
                    check(BitConverter.DoubleToUInt64Bits(result.AsNumber()) == BitConverter.DoubleToUInt64Bits(value.AsNumber()),
                        "Frame canonicalization changed signed zero");
            }
        }
    }

    private static async Task BoundLocation(Action<bool, string> check)
    {
        NativeProgram source = new([], [new("bound-location", [new(1, NativeSlotRole.Parameter),
            new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Local), new(1), new(0)],
            [new(NativeOp.BindLocation, A: 2, B: 0), new(NativeOp.FrameAddress, 3, 2),
             new(NativeOp.Store, A: 3, B: 1, C: 1, Type: 0), new(NativeOp.FrameAddress, 3, 2),
             new(NativeOp.Load, 4, 3, Type: 0), new(NativeOp.Return, A: 4)], new([1, 0], 0, []), ParameterCount: 2)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeAddress cell = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Number))).Field(0);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            cell.Store(NativeValue.FromNumber(1));
            NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
            await using NativeInterpreter interpreter = new(program);
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromAddress(cell), NativeValue.FromNumber(42)]);
            check(result.AsNumber() == 42 && cell.Load().AsNumber() == 42, "A bound location lost its actual storage write");
        }
    }

    private static async Task CapturedLocation(Action<bool, string> check)
    {
        NativeProgram source = new([], [new("capture", [new(0, NativeSlotRole.Capture), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
            new([], 0, [new(0, NativeCaptureMode.Location)]), CaptureCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeAddress cell = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Number))).Field(0);
        NativeEnvironment environment = new(new NativeEnvironmentLayout([new(NativeType.Of(NativeKind.Number), true)]),
            [new NativeCapture(cell)]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
            await using NativeInterpreter interpreter = new(program);
            cell.Store(NativeValue.FromNumber(10 + level));
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0, environment)), []);
            check(result.AsNumber() == 10 + level, "A captured location became a copied frame register");
        }
    }

    private static async Task Merge(Action<bool, string> check)
    {
        NativeProgram source = new([], [new("merge", [new(0, NativeSlotRole.Parameter), new(0, NativeSlotRole.Parameter),
            new(1, NativeSlotRole.Parameter), new(2), new(0)],
            [new(NativeOp.FrameAddress, 3, 1), new(NativeOp.Branch, A: 2, B: 2, C: 3),
             new(NativeOp.FrameAddress, 3, 0), new(NativeOp.Load, 4, 3, Type: 0), new(NativeOp.Return, A: 4)],
            new([0, 0, 1], 0, []), ParameterCount: 3)],
            [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Never, Pointee: 0)], []);
        foreach (int level in new[] { 0, 1, 2, 3 })
            foreach (bool branch in new[] { false, true })
            {
                await using NativeInterpreter interpreter = new(Detached(NativeOptimizer.Optimize(source, level)));
                NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                    [NativeValue.FromNumber(10), NativeValue.FromNumber(20), NativeValue.FromBool(branch)]);
                check(result.AsNumber() == (branch ? 10 : 20), "A block-entry Load used the lexical predecessor's FrameAddress");
            }
    }

    private static async Task Uninitialized(Action<bool, string> check)
    {
        SourceSpan span = new("uninitialized-frame.msync", 3, 5);
        NativeProgram source = new([], [new("uninitialized", [new(0, NativeSlotRole.Local), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0, Source: span), new(NativeOp.Return, A: 2)],
            new([], 0, []))], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
            await using NativeInterpreter interpreter = new(program);
            try { await interpreter.ExecuteAsync(); check(false, "An uninitialized checked frame read disappeared"); }
            catch (MsyncException error)
            {
                check(error.Span == span && error.Message.Contains("Uninitialized", StringComparison.OrdinalIgnoreCase),
                    "An uninitialized frame read lost its source diagnostic");
            }
        }
    }

    private static async Task Conversion(Action<bool, string> check)
    {
        NativeProgram source = new([], [new("path-store", [new(1, NativeSlotRole.Parameter), new(0, NativeSlotRole.Local), new(2), new(0), new(0)],
            [new(NativeOp.Convert, 4, 0, Type: 0), new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Store, A: 2, B: 4, C: 1, Type: 0),
             new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.Return, A: 3)],
            new([1], 0, []), ParameterCount: 1)],
            [new(TypeKind.Path), new(TypeKind.String), new(TypeKind.Never, Pointee: 0)], []);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = Detached(NativeOptimizer.Optimize(source, level));
            await using NativeInterpreter interpreter = new(program, new(WorkingDirectory: Environment.CurrentDirectory));
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromString("relative")]);
            check(result.AsPath().Raw == "relative" && result.AsPath().WorkingDirectory == Environment.CurrentDirectory,
                "Frame canonicalization bypassed implicit Path conversion or its origin");
        }
    }

    private static async Task EnvironmentAlias(Action<bool, string> check)
    {
        SourceSpan span = new("environment-alias.msync", 100, 5);
        NativeProgram source = new([new(NativeKind.Number, Number: 7), new(NativeKind.Null)], [
            new("main", [new(0, NativeSlotRole.Local), new(1), new(2), new(3), new(4), new(1),
                new(0, NativeSlotRole.Local), new(1), new(0), new(5)],
                [new(NativeOp.Constant, 0, 0), new(NativeOp.FrameAddress, 1, 0),
                 new(NativeOp.MakeFunction, 2, 1, 0, Inputs: [1]), new(NativeOp.EnvironmentAddress, 3, 2, 0),
                 new(NativeOp.AddressOffset, 4, 3, 0, 0), new(NativeOp.Load, 5, 4, Type: 1),
                 new(NativeOp.BindLocation, A: 6, B: 5), new(NativeOp.Drop, A: 6),
                 new(NativeOp.FrameAddress, 7, 0), new(NativeOp.Load, 8, 7, Type: 0, Source: span),
                 new(NativeOp.Constant, 9, 1), new(NativeOp.Return, A: 9)], new([], 5, [])),
            new("captured", [new(0, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
                new([], 0, [new(0, NativeCaptureMode.Location)]), CaptureCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Function),
             new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 1), new(TypeKind.Null)],
            [new([new("0", 1)])]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            await using NativeInterpreter interpreter = new(Detached(NativeOptimizer.Optimize(source, level)));
            try { await interpreter.ExecuteAsync(); check(false, "An internal closure alias erased a checked read"); }
            catch (MsyncException error)
            {
                check(error.Span == span && error.Message.Contains("uninitialized", StringComparison.OrdinalIgnoreCase),
                    "Recovering a local frame pointer from its environment lost the unused Load trap");
            }
        }
    }

    private static async Task CheckedStore(Action<bool, string> check)
    {
        SourceSpan span = new("uninitialized-capture.msync", 100, 5);
        NativeProgram source = new([new(NativeKind.Null)], [
            new("main", [new(0, NativeSlotRole.Capture), new(0, NativeSlotRole.Parameter), new(1), new(0), new(2)],
                [new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Store, A: 2, B: 0, C: 1, Type: 0, Source: span),
                 new(NativeOp.Jump, A: 3), new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Load, 3, 2, Type: 0),
                 new(NativeOp.Constant, 4, 0), new(NativeOp.Return, A: 4)],
                new([0], 2, [new(0, NativeCaptureMode.Location)]), ParameterCount: 1, CaptureCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Null)], []);
        NativeAddress cell = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Number))).Field(0);
        NativeEnvironment environment = new(new NativeEnvironmentLayout([new(NativeType.Of(NativeKind.Number), true)]),
            [new NativeCapture(cell)]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            await using NativeInterpreter interpreter = new(Detached(NativeOptimizer.Optimize(source, level)));
            try
            {
                await interpreter.InvokeAsync(NativeValue.FromFunction(new(0, environment)), [NativeValue.FromNumber(7)]);
                check(false, "An unused checked Store source read disappeared");
            }
            catch (MsyncException error)
            {
                check(error.Span == span && error.Message.Contains("uninitialized", StringComparison.OrdinalIgnoreCase),
                    "Frame Store promotion lost the captured-source initialization check");
            }
        }
    }
}
