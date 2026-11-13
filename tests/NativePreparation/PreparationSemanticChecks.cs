using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class PreparationSemanticChecks
{
    internal static async Task RunAsync(string root)
    {
        NativeConstant[] literals =
        [
            new(NativeKind.Null), new(NativeKind.Bool, Boolean: true),
            new(NativeKind.Number, Number: -0.0), new(NativeKind.Number, Number: 12.5),
            new(NativeKind.String, Text: "12.5"), new(NativeKind.String, Text: "./relative"),
            new(NativeKind.Path, Text: "./relative"),
            new(NativeKind.Connection, Text: "host", ConnectionKind: ConnectionKind.Ssh),
            new(NativeKind.Type, Type: TypeRef.Number)
        ];
        TypeKind[] targets =
        [TypeKind.Bool, TypeKind.Number, TypeKind.String, TypeKind.Path, TypeKind.Connection,
            TypeKind.Type, TypeKind.Null | TypeKind.String];
        int checkedCases = 0;
        foreach (NativeConstant literal in literals)
        foreach (TypeKind target in targets)
        foreach (bool explicitly in new[] { false, true })
        {
            NativeProgram program = Conversion(literal, target, explicitly, root);
            NativeArtifact.Validate(program);
            Outcome expected = await ExecuteAsync(program with
                { Stage = ArtifactStage.Executable, Context = new(root, root, null) });
            if (literal.Kind == NativeKind.Number && target == TypeKind.String)
                Require(explicitly ? expected.Value?.Kind == NativeKind.String
                    : expected.Error?.Contains("Cannot implicitly convert", StringComparison.Ordinal) == true,
                    "Number-to-String conversion requires an explicit cast.");
            NativeProgram prepared = NativePreparation.Prepare(program, new BindingContext(root, root, null));
            using MemoryStream wire = new();
            NativeArtifact.Write(prepared, wire);
            wire.Position = 0;
            Outcome actual = await ExecuteAsync(NativeArtifact.Read(wire));
            Require(actual == expected,
                $"Preparation changed {(explicitly ? "explicit" : "implicit")} {literal.Kind}->{target}: {expected} vs {actual}.");
            checkedCases++;
        }
        foreach (bool reflected in new[] { false, true })
        {
            NativeProgram program = CallableMetadata(root, reflected);
            NativeArtifact.Validate(program);
            Outcome expected = new(NativeValue.FromBool(!reflected), null);
            Require(await ExecuteAsync(program with
                { Stage = ArtifactStage.Executable, Context = new(root, root, null) }) == expected,
                "Callable metadata uses its public signature, unwrapping physical pointers only without reflection.");
            NativeProgram prepared = NativePreparation.Prepare(program, new BindingContext(root, root, null));
            using MemoryStream wire = new();
            NativeArtifact.Write(prepared, wire);
            wire.Position = 0;
            Require(await ExecuteAsync(NativeArtifact.Read(wire)) == expected,
                "Contextual preparation must not freeze a different callable signature.");
            checkedCases++;
        }
        Console.WriteLine($"PASS: {checkedCases} contextual conversion/diagnostic binary-roundtrip equivalences");
    }

    private static NativeProgram CallableMetadata(string root, bool reflected)
    {
        NativeFunctionCode callable = new("pointer-abi", [new(5, NativeSlotRole.Parameter), new(6)],
            [new(NativeOp.Constant, 1, A: 2, Type: 6), new(NativeOp.Return, A: 1)],
            new([5], 6, []), ParameterCount: 1)
        { ReflectionSignature = reflected ? new([TypeRef.Bool], TypeRef.Number) : null };
        return new([new(NativeKind.Type, Type: TypeRef.Number), new(NativeKind.Path, Text: root),
                new(NativeKind.Number, Number: 7)],
            [new("metadata-freeze", [new(0), new(1), new(2), new(3), new(4)],
                [
                    new(NativeOp.MakeFunction, 0, A: 1, C: 0, Type: 0),
                    new(NativeOp.Constant, 1, A: 0, Type: 1),
                    new(NativeOp.CallNative, 2, Type: 2, Symbol: "meta.is_invocable", Inputs: [0, 1]),
                    new(NativeOp.Constant, 3, A: 1, Type: 3),
                    new(NativeOp.SelectPath, 4, A: 3, Type: 4, Symbol: ".*"),
                    new(NativeOp.Return, A: 2)
                ], new([], 2, [])), callable],
            [new(TypeKind.Function), new(TypeKind.Type), new(TypeKind.Bool), new(TypeKind.Path),
                new(TypeKind.Range, Element: 3), new(TypeKind.Never, Pointee: 6), new(TypeKind.Number)], [])
        { OptimizationLevel = 0 };
    }

    private static NativeProgram Conversion(NativeConstant literal, TypeKind target, bool explicitly, string root)
    {
        TypeKind source = literal.Kind switch
        {
            NativeKind.Null => TypeKind.Null,
            NativeKind.Bool => TypeKind.Bool,
            NativeKind.Number => TypeKind.Number,
            NativeKind.String => TypeKind.String,
            NativeKind.Path => TypeKind.Path,
            NativeKind.Connection => TypeKind.Connection,
            NativeKind.Type => TypeKind.Type,
            _ => throw new InvalidOperationException("Unexpected conversion fixture.")
        };
        return new([literal, new(NativeKind.Path, Text: root)],
            [new("contextual-convert", [new(0), new(1), new(2), new(3)],
                [
                    new(NativeOp.Constant, 0, A: 0, Type: 0),
                    new(NativeOp.Convert, 1, A: 0, Type: 1, Immediate: explicitly ? 1 : 0,
                        Source: new("conversion-control", 2, 4)),
                    new(NativeOp.Constant, 2, A: 1, Type: 2),
                    new(NativeOp.SelectPath, 3, A: 2, Type: 3, Symbol: ".*"),
                    new(NativeOp.Return, A: 1)
                ], new([], 1, []))],
            [new(source), new(target), new(TypeKind.Path), new(TypeKind.Range, Element: 2)], [])
        { OptimizationLevel = 0 };
    }

    private static async Task<Outcome> ExecuteAsync(NativeProgram program)
    {
        try
        {
            await using NativeInterpreter interpreter = new(program,
                new ExecutionOptions { Tiers = new() { Policy = NativeJitPolicy.Off } });
            return new(await interpreter.ExecuteAsync(), null);
        }
        catch (MsyncException error) { return new(null, error.Message, error.Span); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Outcome(NativeValue? Value, string? Error, SourceSpan? Span = null);
}
