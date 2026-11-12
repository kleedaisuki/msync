using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class NumberTextCastChecks
{
    private static readonly SourceSpan CastSpan = new("number-text-cast", 41, 12);

    internal static async Task<int> Run(string root)
    {
        int checks = 0;
        const string source = """
            let text? = $std::io::read_line();
            if text is null { 0 } else { text as Number }
            """;
        Module compiled = SourceCompiler.Compile(source, "number-text-source", 2);
        string portablePath = Path.Combine(root, "number-text.mso");
        string executablePath = Path.Combine(root, "number-text.mexe");
        Artifact.Write(compiled, portablePath);
        Module portable = Artifact.Read(portablePath, formBlocks: false);
        Artifact.Write(Binding.Bind(portable, new(root, root)), executablePath);
        Module executable = Artifact.Read(executablePath, formBlocks: false);
        Check(portable.Stage == ArtifactStage.Relocatable && executable.Stage == ArtifactStage.Executable,
            "standalone Number-text artifacts retain their independent stages");

        foreach ((string route, Module image) in new[] { ("source", compiled), ("portable", portable), ("bound", executable) })
        {
            Module bound = Binding.Relocate(image, new(root, root));
            int castPc = FindCast(bound);
            Check(Selected(bound, castPc), $"{route}: immediate exact Number target should bind");
            foreach ((string input, double expected) in new[]
            {
                (" -0.000e20 ", -0d),
                ("+5.25e-1", 0.525d),
                ("9007199254740993", 9007199254740992d),
                ("5e-324", double.Epsilon),
                ("1.7976931348623157e308", double.MaxValue),
                ("1e309", double.PositiveInfinity),
                ("-Infinity", double.NegativeInfinity),
                ("NaN", double.NaN),
                (" +1_2.5e+1 ", 125d),
                (" -0x0 ", -0d),
                ("0b10_101", 21d),
                ("0o7_6", 62d),
                ("0x1_0000_0000_0000_0000", 18446744073709551616d),
            })
                await ExpectNumber(image, expected, fast: true, $"{route}: '{input}'", new StringReader(input + "\n"));

            var span = image.Functions[image.EntryPoint].Instructions[FindCast(image)].Span;
            Check(span is not null, $"{route}: cast has source provenance");
            await ExpectFailure(image, span!.Value, fast: true, $"{route}: invalid invariant decimal",
                new StringReader("1,25\n"));
        }

        foreach ((Value operand, double expected, bool fast, string context) in new (Value, double, bool, string)[]
        {
            (new PathValue("not numeric", root, LocalSpelling: "-0"), -0d, true, "local Path native spelling"),
            (new PathValue("2.5", root, new(ConnectionKind.Ssh, "forge"), LocalSpelling: "not numeric"), 2.5d, true, "remote Path raw spelling"),
            (new StringValue("0x20"), 32d, true, "literal-number grammar fallback"),
            (new NumberValue(-0d), -0d, false, "existing Number remains canonical"),
            (Value.True, 1d, false, "Bool conversion remains canonical"),
        })
        {
            Module image = RoundTrip(Direct(operand, TypeRef.Number, root));
            Check(Selected(image, 2), $"{context}: static Number target remains bound");
            await ExpectNumber(image, expected, fast, context);
        }

        var nullable = RoundTrip(Direct(new StringValue("-0"), TypeRef.Number.WithNull, root));
        Check(!Selected(nullable, 2), "nullable Number target is not an exact Number site");
        await ExpectNumber(nullable, -0d, fast: false, "nullable target");

        foreach (bool selectsNumber in new[] { false, true })
        {
            await Branch(selectsNumber, OpCode.Jump);
            await Branch(selectsNumber, OpCode.JumpIfTrue);
            await Branch(selectsNumber, OpCode.JumpIfFalse);
            await Branch(selectsNumber, OpCode.IteratorNext);
        }

        Module entersLiteral = Make([new StringValue("-0"), new TypeValue(TypeRef.Number)],
            [new(OpCode.Constant, 0), new(OpCode.Jump, 2), new(OpCode.Constant, 1), Cast(), new(OpCode.Return)],
            root);
        Check(Selected(entersLiteral, 3), "a jump into the Number constant does not bypass the target proof");
        await ExpectNumber(entersLiteral, -0d, fast: true, "branch into target load");

        Module cell = RoundTrip(Make([new StringValue("-0"), new TypeValue(TypeRef.Number)],
            [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadCell, 0),
             new(OpCode.Constant, 1), Cast(), new(OpCode.Return)], root, localCount: 1));
        Check(Selected(cell, 4), "Cell test reaches a bound Number site");
        await ExpectNumber(cell, -0d, fast: false, "Cell input is normalized by the canonical call");

        await Deferred(alreadyCompleted: false);
        await Deferred(alreadyCompleted: true);

        Module failedPath = RoundTrip(Direct(new PathValue("not numeric", root), TypeRef.Number, root));
        string fastMessage = await ExpectFailure(failedPath, CastSpan, fast: true, "invalid ready Path");
        Module runtimeTarget = RoundTrip(Make([new PathValue("not numeric", root), new TypeValue(TypeRef.Number)],
            [new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 0),
             new(OpCode.LoadLocal, 0), Cast(), new(OpCode.Return)], root, localCount: 1));
        Check(!Selected(runtimeTarget, 4), "a runtime-loaded Type remains on the canonical path");
        Check(await ExpectFailure(runtimeTarget, CastSpan, fast: false, "same invalid Path through canonical call") == fastMessage,
            "fast parse diagnostics use the same cast failure as the canonical call");

        return checks;

        async Task Branch(bool selectsNumber, OpCode control)
        {
            Value[] constants = [new StringValue("-0"), new TypeValue(TypeRef.String), new TypeValue(TypeRef.Number)];
            Instruction[] code;
            int localCount = 0;
            if (control == OpCode.Jump)
            {
                code =
                [
                    new(OpCode.Constant, 0), new(selectsNumber ? OpCode.False : OpCode.True), new(OpCode.JumpIfFalse, 5),
                    new(OpCode.Constant, 1), new(OpCode.Jump, 6), new(OpCode.Constant, 2), Cast(), new(OpCode.Return)
                ];
            }
            else if (control == OpCode.IteratorNext)
            {
                Value[] elements = selectsNumber ? [new NumberValue(1)] : [];
                constants = [.. constants, new RangeValue(elements, TypeRef.Number)];
                code =
                [
                    new(OpCode.Constant, 3), new(OpCode.GetIterator), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 0),
                    new(OpCode.Constant, 1), new(OpCode.IteratorNext, 0, 9), new(OpCode.Pop), new(OpCode.Pop),
                    new(OpCode.Constant, 2), Cast(), new(OpCode.Return)
                ];
                localCount = 1;
            }
            else
            {
                bool condition = selectsNumber == (control == OpCode.JumpIfFalse);
                code =
                [
                    new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(condition ? OpCode.True : OpCode.False),
                    new(control, 6), new(OpCode.Pop), new(OpCode.Constant, 2), Cast(), new(OpCode.Return)
                ];
            }
            Module image = RoundTrip(Make(constants, code, root, localCount));
            int pc = FindCast(image);
            Check(!Selected(image, pc), $"{control}: an edge can skip the adjacent Number target");
            string name = $"{control}, selected {(selectsNumber ? "fallthrough Number" : "branched String")}";
            if (selectsNumber)
                await ExpectNumber(image, -0d, fast: false, name);
            else
            {
                RuntimeProfile profile = new();
                Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
                Check(result is StringValue { Data: "-0" }, name);
                CheckProfile(profile, fast: false, name);
            }
        }

        async Task Deferred(bool alreadyCompleted)
        {
            TaskCompletionSource<ProcessValue> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ProcessValue result = new(0, "-0", "");
            if (alreadyCompleted) completion.SetResult(result);
            var process = new ProcessValue(0, "", "") { Completion = completion.Task };
            Module image = Make([process, new TypeValue(TypeRef.Number)],
                [new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "process.stdout"),
                 new(OpCode.Constant, 1), Cast(), new(OpCode.Return)], root);
            RuntimeProfile profile = new();
            Check(Selected(image, 3), "deferred input test reaches a bound Number site");
            Task<Value> pending = Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile }).AsTask();
            try
            {
                if (!alreadyCompleted)
                    Check(!pending.IsCompleted, "cast result remains deferred while process output is pending");
            }
            finally { completion.TrySetResult(result); }
            Check(await pending is NumberValue number && Bits(number.Data) == Bits(-0d),
                "generic deferred cast preserves selected output and negative zero");
            CheckProfile(profile, fast: false, alreadyCompleted ? "completed deferred input" : "pending deferred input");
        }

        async Task ExpectNumber(Module image, double expected, bool fast, string context, TextReader? input = null)
        {
            RuntimeProfile profile = new(instructionSites: true);
            Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile, Input = input });
            Check(result is NumberValue number && (double.IsNaN(expected) ? double.IsNaN(number.Data) : Bits(expected) == Bits(number.Data)),
                $"{context}: exact Number result");
            CheckProfile(profile, fast, context);
        }

        async Task<string> ExpectFailure(Module image, SourceSpan expected, bool fast, string context, TextReader? input = null)
        {
            RuntimeProfile profile = new();
            List<RuntimeObservation> observations = [];
            try
            {
                _ = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root, Observe: observations.Add)
                {
                    Profile = profile,
                    Input = input
                });
            }
            catch (MsyncException error)
            {
                Check(error.Span == expected && error.Message.Contains("Cannot cast", StringComparison.Ordinal)
                    && error.Message.Contains("invariant numeric text", StringComparison.Ordinal),
                    $"{context}: selected cast error retains span and original parser guidance");
                Check(observations.Any(observation => observation.Phase == "native" && observation.Outcome == "failure"),
                    $"{context}: selected cast error is reported as a native failure");
                CheckProfile(profile, fast, context);
                return error.Message;
            }
            throw new InvalidOperationException($"{context}: invalid text succeeded");
        }

        void CheckProfile(RuntimeProfile profile, bool fast, string context)
        {
            var cast = profile.Drain().Single(sample => sample.Name == "vm.native" && sample.Attributes["native.name"] == "cast");
            Check(cast.Metrics["calls"] == 1 && cast.Metrics["fast_calls"] == (fast ? 1UL : 0UL),
                $"{context}: runtime cast dispatch attribution");
        }

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }

    private static Module Direct(Value operand, TypeRef target, string root) =>
        Make([operand, new TypeValue(target)], [new(OpCode.Constant, 0), new(OpCode.Constant, 1), Cast(), new(OpCode.Return)], root);

    private static Module Make(Value[] constants, Instruction[] code, string root, int localCount = 0) =>
        new(constants, [new("cast", 0, localCount, code, TypeRef.Any)], Stage: ArtifactStage.Executable,
            Context: new(root, root), OptimizationLevel: 0);

    private static Instruction Cast() => new(OpCode.NativeCall, 2, Text: "cast", Span: CastSpan);
    private static bool Selected(Module image, int pc) => ExecutionTargets.For(image).NumberText(image.EntryPoint)?.Contains(pc) == true;
    private static int FindCast(Module image) => image.Functions[image.EntryPoint].Instructions
        .Select((instruction, pc) => (instruction, pc))
        .Single(item => item.instruction is { Op: OpCode.NativeCall, Text: "cast" }).pc;
    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }
}
