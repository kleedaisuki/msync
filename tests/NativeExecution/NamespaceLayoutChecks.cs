using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class NamespaceLayoutChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeCodeLayout[] layouts = [new([new("value", 2)]), new([new("value", 2)])];
        NativeProgram input = new([], [new("has-layout", [new(0, NativeSlotRole.Parameter), new(1)],
            [new(NativeOp.Unary, 1, 0, 0, Immediate: (int)NativeUnary.HasLayout), new(NativeOp.Return, A: 1)],
            new([0], 1, []), ParameterCount: 1)],
            [new(TypeKind.Any, Layouts: [0, 1]), new(TypeKind.Bool), new(TypeKind.Number)], layouts);
        using MemoryStream bytes = new();
        NativeArtifact.Write(input, bytes);
        bytes.Position = 0;
        await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes));
        NativeLayout row = new(NativeType.Of(NativeKind.Number));
        NativeValue[] values = [NativeValue.FromAddress(new NativeAllocation(row, 0).Address),
            NativeValue.FromAddress(new NativeAllocation(row, 1).Address), NativeValue.Null, NativeValue.FromNumber(1)];
        int checks = 0;
        for (int index = 0; index < values.Length; index++)
        {
            NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [values[index]]);
            if (result.AsBool() != (index == 0))
                throw new Exception("HasLayout conflated logical identities or treated a scalar as a namespace");
            checks++;
        }
        for (int selected = 0; selected < 2; selected++)
        {
            NativeProgram placed = new([], [new("placed-layout", [new(0), new(1)],
                [new(NativeOp.Allocate, 0, 1), new(NativeOp.Unary, 1, 0, selected, Immediate: (int)NativeUnary.HasLayout),
                 new(NativeOp.Return, A: 1)], new([], 1, []))],
                [new(TypeKind.Scope, Layouts: [1]), new(TypeKind.Bool), new(TypeKind.Number)], layouts)
            { Allocations = [new(0, 1, NativeAllocationPlacement.Frame)] };
            await using NativeInterpreter frame = new(placed);
            if ((await frame.ExecuteAsync()).AsBool() != (selected == 1))
                throw new Exception("Coallocation replaced a logical namespace tag with its physical frame tag");
            checks++;
        }
        for (int selected = 0; selected < 2; selected++)
        {
            NativeProgram environment = new([new(NativeKind.Number, Number: 17)],
                [new("environment-tag", [new(0), new(1), new(2), new(3)],
                    [new(NativeOp.Constant, 0, 0), new(NativeOp.MakeFunction, 1, 1, selected, Inputs: [0]),
                     new(NativeOp.AdaptFunction, 1, 1, 1), new(NativeOp.EnvironmentAddress, 2, 1, selected),
                     new(NativeOp.Unary, 3, 2, 0, Immediate: (int)NativeUnary.HasLayout), new(NativeOp.Return, A: 3)], new([], 3, [])),
                 new("capture", [new(0, NativeSlotRole.Capture)], [new(NativeOp.Return, A: 0)],
                     new([], 0, [new(0, NativeCaptureMode.Value)]), CaptureCount: 1)],
                [new(TypeKind.Number), new(TypeKind.Function), new(TypeKind.Scope, Layouts: [0, 1]), new(TypeKind.Bool)],
                [new([new("0", 0)]), new([new("0", 0)])]);
            using MemoryStream environmentBytes = new();
            NativeArtifact.Write(environment, environmentBytes);
            environmentBytes.Position = 0;
            await using NativeInterpreter captured = new(NativeArtifact.Read(environmentBytes));
            if ((await captured.ExecuteAsync()).AsBool() != (selected == 0))
                throw new Exception("Explicit constructor environment identity collapsed across adaptation");
            checks++;
        }
        checks += await EmptyDomainsAsync();
        Console.WriteLine("PASS namespace-layout: exact tags distinguish equal schemas, scalars and coallocated views");
        return checks;
    }

    private static async Task<int> EmptyDomainsAsync()
    {
        SourceSpan span = new("namespace-empty-domain.msync", 0, 5);
        int checks = 0;
        foreach (bool finite in new[] { false, true })
        {
            NativeProgram program = new([], [new("empty-domain", [new(0), new(1)],
                [new(NativeOp.Allocate, 0, 0), new(finite ? NativeOp.Narrow : NativeOp.Copy, 1, 0, Type: 1, Source: span), new(NativeOp.Return, A: 1)],
                new([], 1, []))], [new(TypeKind.Scope, Layouts: [0]), new(TypeKind.Scope, Layouts: finite ? [] : null)], [new([])]);
            using MemoryStream bytes = new();
            NativeArtifact.Write(program, bytes);
            bytes.Position = 0;
            await using NativeInterpreter interpreter = new(NativeArtifact.Read(bytes));
            try
            {
                NativeValue result = await interpreter.ExecuteAsync();
                if (finite || result.Kind != NativeKind.Address) throw new Exception("Finite empty namespace domain admitted an allocation");
            }
            catch (MsyncException error) when (finite && error.Span == span) { }
            checks++;
        }
        NativeProgram nullable = new([new(NativeKind.Null)], [new("empty-nullable-domain", [new(0), new(1)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Copy, 1, 0), new(NativeOp.Return, A: 1)], new([], 1, []))],
            [new(TypeKind.Null), new(TypeKind.Scope | TypeKind.Null, Layouts: [])], []);
        using MemoryStream encoded = new();
        NativeArtifact.Write(nullable, encoded);
        encoded.Position = 0;
        await using NativeInterpreter nullValue = new(NativeArtifact.Read(encoded));
        if ((await nullValue.ExecuteAsync()).Kind != NativeKind.Null)
            throw new Exception("Finite empty Scope alternatives removed the independent Null alternative");
        checks++;
        NativeCodeType[] domains = [new(TypeKind.Scope), new(TypeKind.Scope, Layouts: [])];
        if (!NativeTypeRelations.Accepts(domains, 0, 1) || NativeTypeRelations.Accepts(domains, 1, 0))
            throw new Exception("Unrestricted and empty namespace contracts have incorrect subset direction");
        NativeProgram impossible = new([], [new("impossible-allocation", [new(0)],
            [new(NativeOp.Allocate, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.Scope, Layouts: [])], [new([])]);
        try { NativeArtifact.Validate(impossible); throw new Exception("A physical allocation was admitted directly into an empty namespace domain"); }
        catch (MsyncException) { }
        return checks + 2;
    }
}
