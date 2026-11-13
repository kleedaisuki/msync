using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;
using Msync.Runtime.Native;

internal static class NativeBindingLoweringChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        NativeAllocation child = new(new NativeLayout(NativeType.Of(NativeKind.Number)), 1);
        child.Field(0).Store(NativeValue.FromNumber(31));
        NativeValue[] values = [NativeValue.Null, NativeValue.True, NativeValue.FromNumber(-0.0),
            NativeValue.FromString("可莉𐐀"), NativeValue.FromPath(new("./path", "/origin")),
            NativeValue.FromConnection(new(ConnectionKind.Ssh, "configured")),
            NativeValue.FromFunction(new(-1, NativeName: "math.abs")),
            NativeValue.FromRange(NativeRange.Snapshot([NativeValue.FromNumber(3)], TypeRef.Number)),
            NativeValue.FromProcess(new(0, "output", "error")), NativeValue.FromType(TypeRef.Number),
            NativeValue.FromAddress(child.Address)];
        foreach (NativeValue value in values)
        {
            NativeProgram source = new([], [new("constructor", [new(0, NativeSlotRole.Parameter), new(1),
                new(2), new(0, NativeSlotRole.Local), new(2)],
                [new(NativeOp.Allocate, 1, A: 0), new(NativeOp.AddressOffset, 2, A: 1, B: 0, C: 0),
                 new(NativeOp.BindLocation, A: 3, B: 2), new(NativeOp.FrameAddress, 4, A: 3),
                 new(NativeOp.Store, A: 4, B: 0, Type: 0, C: 1), new(NativeOp.Return, A: 1)],
                new([0], 1, []), ParameterCount: 1)],
                [new(value.Type.Kind, Element: value.Kind == NativeKind.Range ? 3 : -1,
                    Layouts: value.Kind == NativeKind.Address ? [1] : null),
                 new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0), new(TypeKind.Number)],
                [new([new("value", 0)]), new([new("n", 3)])]);
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                NativeProgram optimized = NativeOptimizer.Optimize(source, level);
                NativeArtifact.Validate(optimized);
                if (level != 0)
                {
                    check(optimized.Functions[0].Instructions.All(instruction => instruction.Op is not (NativeOp.BindLocation or NativeOp.FrameAddress)),
                        $"A static {value.Kind} constructor retained lexical address intermediates at O{level}.");
                    check(optimized.Functions[0].Slots.All(slot => slot.Role != NativeSlotRole.Local),
                        $"A static {value.Kind} constructor allocated an unused lexical cell at O{level}.");
                }
                using MemoryStream wire = new(); NativeArtifact.Write(optimized, wire); wire.Position = 0;
                await using NativeInterpreter interpreter = new(NativeArtifact.Read(wire));
                NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
                check(actual.AsAddress().Field(0).Load() == value,
                    $"Lexical binding elimination changed {value.Kind} storage at O{level}.");
            }
        }
        await ReusedPointer(check);
        await WritesThroughBinding(check);
        await BindReadsBinding(check);
        await UninitializedSource(check);
        await PersistentBinding(check);
    }

    private static async Task ReusedPointer(Action<bool, string> check)
    {
        NativeProgram source = new([new(NativeKind.Number, Number: 41), new(NativeKind.Number, Number: 17)],
            [new("reused-pointer", [new(1), new(2), new(0, NativeSlotRole.Local), new(0), new(2), new(0)],
                [new(NativeOp.Allocate, 0, A: 0), new(NativeOp.AddressOffset, 1, A: 0, B: 0, C: 0),
                 new(NativeOp.BindLocation, A: 2, B: 1), new(NativeOp.Allocate, 0, A: 0),
                 new(NativeOp.AddressOffset, 1, A: 0, B: 0, C: 0), new(NativeOp.Constant, 3, A: 0, Type: 0),
                 new(NativeOp.Store, A: 1, B: 3, Type: 0, C: 1), new(NativeOp.FrameAddress, 4, A: 2),
                 new(NativeOp.Constant, 3, A: 1, Type: 0), new(NativeOp.Store, A: 4, B: 3, Type: 0, C: 1),
                 new(NativeOp.Load, 5, A: 1, Type: 0), new(NativeOp.Return, A: 5)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
            [new([new("n", 0)])]);
        await Numbers(source, 41, check);
    }

    private static async Task WritesThroughBinding(Action<bool, string> check)
    {
        NativeProgram source = new([new(NativeKind.Number, Number: 17)],
            [new("bound-destination", [new(1), new(2), new(0, NativeSlotRole.Local), new(0)],
                [new(NativeOp.Allocate, 0, A: 0), new(NativeOp.AddressOffset, 1, A: 0, B: 0, C: 0),
                 new(NativeOp.BindLocation, A: 2, B: 1), new(NativeOp.Constant, 2, A: 0, Type: 0),
                 new(NativeOp.Load, 3, A: 1, Type: 0), new(NativeOp.Return, A: 3)], new([], 0, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
            [new([new("n", 0)])]);
        await Numbers(source, 17, check);
    }

    private static async Task BindReadsBinding(Action<bool, string> check)
    {
        NativeProgram source = new([new(NativeKind.Number, Number: 17)],
            [new("binding-rhs", [new(1, NativeSlotRole.Parameter), new(2), new(3),
                new(1, NativeSlotRole.Local), new(0, NativeSlotRole.Local), new(3)],
                [new(NativeOp.Allocate, 1, A: 0), new(NativeOp.AddressOffset, 2, A: 1, B: 0, C: 0),
                 new(NativeOp.BindLocation, A: 3, B: 2), new(NativeOp.FrameAddress, 5, A: 3),
                 new(NativeOp.Store, A: 5, B: 0, Type: 1, C: 1), new(NativeOp.BindLocation, A: 4, B: 3),
                 new(NativeOp.Constant, 4, A: 0, Type: 0), new(NativeOp.Return, A: 4)],
                 new([1], 0, []), ParameterCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.Scope, Layouts: [0]),
                new(TypeKind.Never, Pointee: 1)], [new([new("pointer", 1)])]);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeAllocation cell = new(new NativeLayout(NativeType.Of(NativeKind.Number)));
            NativeProgram program = NativeOptimizer.Optimize(source, level);
            NativeArtifact.Validate(program);
            await using NativeInterpreter interpreter = new(program);
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(cell.Field(0))]);
            check(result.AsNumber() == 17 && cell.Field(0).Load().AsNumber() == 17,
                $"A binding RHS stopped reading an existing alias at O{level}.");
        }
    }

    private static async Task UninitializedSource(Action<bool, string> check)
    {
        SourceSpan span = new("uninitialized-binding-source", 5, 7);
        NativeProgram source = new([new(NativeKind.Number, Number: 17)],
            [new("checked-binding", [new(1, NativeSlotRole.Capture), new(0, NativeSlotRole.Local), new(0)],
                [new(NativeOp.BindLocation, A: 1, B: 0, Source: span), new(NativeOp.Constant, 2, A: 0, Type: 0),
                 new(NativeOp.Return, A: 2)], new([], 0, [new(1, NativeCaptureMode.Location)]), CaptureCount: 1)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = NativeOptimizer.Optimize(source, level);
            NativeArtifact.Validate(program);
            NativeAllocation cell = new(new NativeLayout(NativeType.Of(NativeKind.Address)));
            NativeEnvironment environment = new(new([new(NativeType.Of(NativeKind.Address), true)]), [new(cell.Field(0))]);
            await using NativeInterpreter interpreter = new(program);
            bool failed = false;
            try { _ = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0, environment)), []); }
            catch (MsyncException error) { failed = error.Span == span && error.Message.Contains("uninitialized", StringComparison.OrdinalIgnoreCase); }
            check(failed, $"A dead binding hid an uninitialized source read at O{level}.");
        }
    }

    private static async Task Numbers(NativeProgram source, double expected, Action<bool, string> check)
    {
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = NativeOptimizer.Optimize(source, level);
            using MemoryStream stream = new(); NativeArtifact.Write(program, stream); stream.Position = 0;
            await using NativeInterpreter interpreter = new(NativeArtifact.Read(stream));
            check((await interpreter.ExecuteAsync()).AsNumber() == expected,
                $"Location binding changed a checked value at O{level}.");
        }
    }

    private static async Task PersistentBinding(Action<bool, string> check)
    {
        NativeProgram source = new([new(NativeKind.Number, Number: 17)],
            [new("persistent-alias", [new(1), new(2), new(0, NativeSlotRole.Local, Name: "alias"), new(0), new(2)],
                [new(NativeOp.Allocate, 0, A: 0), new(NativeOp.AddressOffset, 1, A: 0, B: 0, C: 0),
                 new(NativeOp.BindLocation, A: 2, B: 1), new(NativeOp.Constant, 3, A: 0, Type: 0),
                 new(NativeOp.FrameAddress, 4, A: 2), new(NativeOp.Store, A: 4, B: 3, Type: 0, C: 1),
                 new(NativeOp.Return, A: 0)], new([], 1, []))],
            [new(TypeKind.Number), new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 0)],
            [new([new("n", 0)])]) { PreserveLocals = true, SessionRoots = [2] };
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            NativeProgram program = NativeOptimizer.Optimize(source, level);
            NativeArtifact.Validate(program);
            await using NativeInterpreter interpreter = new(program);
            NativeValue result = await interpreter.ExecuteAsync();
            NativeValue? alias = interpreter.GetLocalValue(2);
            check(alias?.AsNumber() == 17 && result.AsAddress().Field(0).Load().AsNumber() == 17,
                $"Eliminating lexical intermediates lost a persistent alias at O{level}.");
            result.AsAddress().Field(0).Store(NativeValue.FromNumber(23));
            check(interpreter.GetLocalValue(2)?.AsNumber() == 23,
                $"A persistent binding stopped aliasing its namespace field at O{level}.");
        }
    }
}
