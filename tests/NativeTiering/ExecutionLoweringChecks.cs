using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using NativeMemory = Msync.Runtime.Native.NativeMemory;

internal static class ExecutionLoweringChecks
{
    internal static async Task<int> RunAsync()
    {
        (TypeKind Kind, string Source)[] cases =
        [
            (TypeKind.Null, "null"), (TypeKind.String, "\"held𐀀\""), (TypeKind.Path, "./held"),
            (TypeKind.Connection, "$local"), (TypeKind.Function, "fn() { 17 }"),
            (TypeKind.Range, "[1,2]"), (TypeKind.Type, "Number"), (TypeKind.Scope, "scope { n: 1 }")
        ];
        foreach ((TypeKind kind, string source) in cases)
        {
            NativeProgram producerProgram = Compiler.Compile(source, "virtual-pointer-value.msync", 0);
            await using NativeInterpreter producer = new(producerProgram, new() { Tiers = new(NativeJitPolicy.Off) });
            NativeValue value = await producer.ExecuteAsync();
            await BuiltinAsync(kind, value);
        }
        await BuiltinAsync(TypeKind.Process, NativeValue.FromProcess(new(0, "", "")));
        await TransferAsync(TypeKind.Number, NativeValue.FromNumber(-0.0));
        await TransferAsync(TypeKind.Bool, NativeValue.FromBool(true));
        await EnvironmentAliasAsync();
        await UnknownBindAsync();
        await AccessSpansAsync();
        return cases.Length + 44;
    }

    private static async Task UnknownBindAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 9)],
            [new("unknown-bind", [new(1, NativeSlotRole.Parameter), new(0), new(1), new(0)],
                [new(NativeOp.BindLocation, A: 1, B: 0), new(NativeOp.Constant, 1, 0),
                 new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Load, 3, 2, Type: 0), new(NativeOp.Return, A: 3)],
                new([1], 0, []), ParameterCount: 1)], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeArtifact.Validate(program);
        NativeExecutionFunction function = new(new(program, new()), 0, new(NativeJitPolicy.Force));
        if (function.RawWritable.Any(value => value)) throw new InvalidOperationException("An unknown binding was assumed external to the private frame.");
        NativeAddress address = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
        address.Store(NativeValue.FromNumber(1));
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(address)]);
        if (result.AsNumber() != 9 || address.Load().AsNumber() != 9)
            throw new InvalidOperationException("Unknown pointer binding lost its canonical managed write.");
    }

    private static async Task EnvironmentAliasAsync()
    {
        SourceSpan span = new("environment-alias.msync", 100, 5);
        NativeProgram program = new([new(NativeKind.Number, Number: 7), new(NativeKind.Null)], [
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
             new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Never, Pointee: 1), new(TypeKind.Null)], [new([new("0", 1)])]);
        using MemoryStream artifact = new();
        NativeArtifact.Write(program, artifact);
        artifact.Position = 0;
        NativeProgram detached = NativeArtifact.Read(artifact);
        NativeExecutionFunction function = new(new(detached, new()), 0, new(NativeJitPolicy.Force));
        if (function.RawWritable.Any(value => value)) throw new InvalidOperationException("Internal environment recovery retained a private-pointer proof.");
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(detached, new() { Tiers = new(policy, 1, 1, 1) });
            try { await interpreter.ExecuteAsync(); throw new InvalidOperationException("An internal frame alias hid a cleared-cell read."); }
            catch (MsyncException error) when (error.Span == span && error.Message.Contains("uninitialized", StringComparison.OrdinalIgnoreCase)) { }
        }
    }

    private static async Task BuiltinAsync(TypeKind kind, NativeValue value)
    {
        NativeCodeType contract = new(kind, Element: kind == TypeKind.Range ? 2 : -1,
            Layouts: kind == TypeKind.Scope ? [0] : null);
        NativeCodeLayout[] layouts = kind == TypeKind.Scope ? [new([new("n", 2)])] : [];
        NativeProgram program = new([], [new("builtin-address", [new(0, NativeSlotRole.Parameter), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0), new(NativeOp.Return, A: 2)],
            new([0], 0, []), ParameterCount: 1)], [contract, new(TypeKind.Never, Pointee: 0), new(TypeKind.Number)], layouts);
        NativeArtifact.Validate(program);
        NativeExecutionFunction lowered = new(new(program, new()), 0, new(NativeJitPolicy.Off));
        if (lowered.Instructions[0].Span != 2 || lowered.Instructions[0].Op != NativeOp.Copy)
            throw new InvalidOperationException($"A static {kind} address/load round trip survived backend lowering.");
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(NativeJitPolicy.Force, 1, 1, 1) });
        NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
        if (actual != value || interpreter.TierStatistics.Entries == 0)
            throw new InvalidOperationException($"Lowered {kind} transfer lost its complete builtin representation.");
        await TransferAsync(kind, value);
    }

    private static async Task TransferAsync(TypeKind kind, NativeValue value)
    {
        TypeKind extra = kind == TypeKind.String ? TypeKind.Bool : TypeKind.String;
        NativeCodeType Contract(TypeKind members) => new(members, Element: kind == TypeKind.Range ? 3 : -1,
            Layouts: kind == TypeKind.Scope ? [0] : null);
        NativeProgram source = new([], [new("carrier-transfer", [new(0, NativeSlotRole.Parameter), new(1), new(2)],
            [new(NativeOp.Copy, 1, 0, Type: 1), new(NativeOp.Copy, 2, 1, Type: 2), new(NativeOp.Return, A: 2)],
            new([0], 2, []), ParameterCount: 1)],
            [Contract(kind), Contract(kind | TypeKind.Null), Contract(kind | TypeKind.Null | extra), new(TypeKind.Number)],
            kind == TypeKind.Scope ? [new([new("n", 3)])] : []);
        using MemoryStream artifact = new(); NativeArtifact.Write(source, artifact); artifact.Position = 0;
        NativeProgram program = NativeArtifact.Read(artifact);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy, 1, 1, 1) });
            NativeValue actual = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [value]);
            if (actual != value) throw new InvalidOperationException($"{kind} changed during mono/nullable/wide physical carrier transfers in {policy}.");
        }
    }

    private static async Task AccessSpansAsync()
    {
        SourceSpan span = new("lowered-access.msync", 71, 4);
        NativeFunctionCode function = new("checked-load", [new(0), new(1), new(0)],
            [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Load, 2, 1, Type: 0, Source: span), new(NativeOp.Return, A: 2)], new([], 0, []));
        NativeProgram fused = new([], [function], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0)], []);
        NativeProgram branch = fused with
        {
            Functions = [function with { Instructions = [new(NativeOp.FrameAddress, 1, 0), new(NativeOp.Jump, A: 4),
                new(NativeOp.Nop), new(NativeOp.FrameAddress, 1, 0),
                new(NativeOp.Load, 2, 1, Type: 0, Source: span), new(NativeOp.Return, A: 2)] }]
        };
        foreach (NativeProgram program in new[] { fused, branch })
        {
            using MemoryStream stream = new(); NativeArtifact.Write(program, stream); stream.Position = 0;
            NativeProgram detached = NativeArtifact.Read(stream);
            NativeExecutionFunction code = new(new(detached, new()), 0, new(NativeJitPolicy.Off));
            if (ReferenceEquals(program, branch) && code.Instructions[3].Span != 1)
                throw new InvalidOperationException("Backend fusion crossed an independently reachable access PC.");
            foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
            {
                await using NativeInterpreter interpreter = new(detached, new() { Tiers = new(policy, 1, 1, 1) });
                try { await interpreter.ExecuteAsync(); throw new InvalidOperationException("An uninitialized lowered read succeeded."); }
                catch (MsyncException error) when (error.Span == span && error.Message.Contains("uninitialized", StringComparison.OrdinalIgnoreCase)) { }
            }
        }
    }

}
