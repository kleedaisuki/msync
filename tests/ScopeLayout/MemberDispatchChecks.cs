using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;

internal static class MemberDispatchChecks
{
    private static readonly SourceSpan LoadSpan = new("member-dispatch", 0, 4);
    private static readonly SourceSpan MemberSpan = new("member-dispatch", 16, 10);
    private static readonly Instruction Load = new(OpCode.LoadLocal, 0, Span: LoadSpan);
    private static readonly Instruction Member = new(OpCode.LoadField, 0, Span: MemberSpan);
    private const int Any = 0, Number = 1, Null = 2, BothReceivers = 3, OpaqueScope = 4;

    internal static async Task Run()
    {
        Require(!Enum.IsDefined(ExecutionTargets.LoadLocalField), "The fused tag must not be a wire opcode.");
        Value[] values = [
            new ScopeValue(new ScopeLayout(["field"], 0), [new NumberValue(9)]),
            new ScopeValue(new ScopeLayout(["other", "field"], 1), [Value.Null, new NumberValue(42)])
        ];
        var fallthrough = RoundTrip(Image([new(OpCode.Constant, 0), new(OpCode.StoreLocal), Load, Member,
            new(OpCode.Return)], values));
        var original = fallthrough.Functions[0].Instructions;
        var dispatch = ExecutionTargets.For(fallthrough).Dispatch(0);
        Require(dispatch is not null && dispatch[2] is { Op: ExecutionTargets.LoadLocalField, A: 0, B: 0 }
            && original[2].Op == OpCode.LoadLocal && original[3].Op == OpCode.LoadField
            && ReferenceEquals(dispatch[3], original[3]),
            "Only the first PC is replaced in the dispatch view.");
        Require(await Interpreter.ExecuteAsync(fallthrough) is NumberValue { Data: 9 },
            "Fallthrough uses the local receiver.");
        RuntimeProfile profile = new(instructionSites: true);
        Require(await Interpreter.ExecuteAsync(fallthrough, new() { Profile = profile }) is NumberValue { Data: 9 },
            "The observed fallthrough produces the same value.");
        var sites = profile.Drain().Where(sample => sample.Name == "vm.instruction").ToArray();
        Require(Saw(sites, "2", "LoadLocal") && Saw(sites, "3", "LoadField"),
            "Observed fallthrough visits both original instructions.");

        var entry = RoundTrip(Image([new(OpCode.Constant, 0), new(OpCode.StoreLocal),
            new(OpCode.Constant, 1), new(OpCode.Jump, 5), Load, Member, new(OpCode.Return)], values));
        Require(await Interpreter.ExecuteAsync(entry) is NumberValue { Data: 42 },
            "An incoming edge to LoadField uses the receiver already on its stack and its layout case.");
        Require(await Interpreter.ExecuteAsync(entry, new() { Profile = profile }) is NumberValue { Data: 42 },
            "The observed route produces the same value.");
        sites = profile.Drain().Where(sample => sample.Name == "vm.instruction").ToArray();
        Require(Saw(sites, "5", "LoadField")
            && !sites.Any(sample => sample.Attributes["instruction.pc"] == "4"),
            "Observed frames retain the selected original PCs.");

        var failed = RoundTrip(Image([
            new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Call, 1, 2), new(OpCode.Return)
        ], values, locals: 0, new FunctionCode("opaque receiver", 1, 1,
            [Load, Member with { A = 1 }, new(OpCode.Return)], TypeRef.Any, ParameterTypes: [TypeRef.Scope])
            { CodeSignature = new([OpaqueScope], Any, []) }));
        try
        {
            _ = await Interpreter.ExecuteAsync(failed);
            throw new InvalidOperationException("A missing field unexpectedly resolved.");
        }
        catch (MsyncException error)
        {
            Require(error.Span == MemberSpan && error.Message == "Object has no member 'missing'",
                "An opaque fused lookup retains the LoadField span and selected error.");
        }

        var capture = RoundTrip(Image([
            new(OpCode.Constant, 0), new(OpCode.StoreLocal), new(OpCode.LoadCell, 0, BothReceivers + 1),
            new(OpCode.MakeClosure, 1, 1), new(OpCode.StoreLocal, 1),
            new(OpCode.Constant, 1), new(OpCode.StoreLocal), new(OpCode.LoadLocal, 1),
            new(OpCode.Call, B: 2), new(OpCode.Return)
        ], values, locals: 2, new FunctionCode("captured receiver", 0, 2,
            [Load, new(OpCode.AdmitScope, BothReceivers), new(OpCode.StoreLocal, 1),
                new(OpCode.LoadLocal, 1), Member, new(OpCode.Return)], TypeRef.Number, CaptureCount: 1)
            { CodeSignature = new([], Number, [new(BothReceivers, true)]) }));
        Require(ExecutionTargets.For(capture).Dispatch(1)?[3] is { Op: ExecutionTargets.LoadLocalField, A: 1, B: 0 },
            "A copied, admitted value from the current capture still uses the fused read.");
        Require(await Interpreter.ExecuteAsync(capture) is NumberValue { Data: 42 },
            "The fused lookup reads the current capture and uses its admitted layout case.");

        await DeferredMember();
        Console.WriteLine("PASS bound member dispatch, canonical entry/observations, selected span, live capture and deferred Process member");
    }

    private static bool Saw(RuntimeProfileSample[] sites, string pc, string opcode) =>
        sites.Any(sample => sample.Attributes["instruction.pc"] == pc
            && sample.Attributes["opcode"] == opcode && sample.Metrics["hits"] == 1);

    private static async Task DeferredMember()
    {
        var lowered = RoundTrip(Compiler.Compile(
            "let child = ./msync-never-executed(); child::stdout", "process-field.msync", 0));
        Require(lowered.Functions.SelectMany(function => function.Instructions)
            .Any(instruction => instruction is { Op: OpCode.NativeCall, A: 1, Text: "process.stdout" }),
            "Source Process member access must lower to the named native, not an Object field projection.");

        TaskCompletionSource<ProcessValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Value[] values = [new ProcessValue(0, "", "") { Completion = completion.Task }, new StringValue("ready")];
        Instruction[] prefix = [new(OpCode.Constant, 0), new(OpCode.StoreLocal), Load,
            new(OpCode.NativeCall, 1, Text: "process.stdout", Span: MemberSpan)];
        try
        {
            RuntimeProfile profile = new(instructionSites: true);
            var discard = Interpreter.ExecuteAsync(Image([.. prefix, new(OpCode.Pop),
                new(OpCode.Constant, 1), new(OpCode.Return)], values), new() { Profile = profile });
            Require(discard.IsCompletedSuccessfully && discard.Result is StringValue { Data: "ready" },
                "Discarding a deferred member does not await its Process.");
            Require(Saw(profile.Drain().Where(sample => sample.Name == "vm.instruction").ToArray(), "3", "NativeCall"),
                "The deferred Process member records the selected native instruction.");
            var consume = Interpreter.ExecuteAsync(Image([.. prefix, new(OpCode.Return)], values));
            Require(!consume.IsCompleted, "A deferred result waits at its ordinary consumer.");
            completion.SetResult(new(0, "released", ""));
            Require(await consume is StringValue { Data: "released" }, "The returned member uses the completed Process.");
        }
        finally { completion.TrySetResult(new(0, "released", "")); }
    }

    private static Module Image(Instruction[] code, Value[] values, int locals = 1, params FunctionCode[] functions)
    {
        string cwd = Environment.CurrentDirectory;
        return new(values, [
            new("entry", 0, locals, code, TypeRef.Any) { CodeSignature = new([], Any, []) }, .. functions
        ], Stage: ArtifactStage.Executable, Context: new(cwd, cwd))
        {
            CodeTypes = [
                new(TypeKind.Any), new(TypeKind.Number), new(TypeKind.Null),
                new(TypeKind.Scope, ScopeLayouts: [0, 1]), new(TypeKind.Scope)
            ],
            ScopeLayouts = [new([new("field", Number)]), new([new("other", Null), new("field", Number)])],
            FieldProjections = [new("field", [new(0, 0), new(1, 1)]), new("missing", [])]
        };
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
