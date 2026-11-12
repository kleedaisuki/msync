using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class PredicateTargetChecks
{
    internal static async Task Run(string root, Action<bool, string> require)
    {
        var numbers = new[] { double.NegativeInfinity, -1d, -0d, 0d, 1d, double.PositiveInfinity, double.NaN };
        foreach (double left in numbers)
            foreach (double right in numbers)
                await Check(new NumberValue(left), new NumberValue(right), TypeRef.Number.WithNull, TypeRef.Number.WithNull,
                    NaturalOrder, left < right || !double.IsNaN(left) && double.IsNaN(right), expectedFast: true);
        await Check(Value.Null, new NumberValue(0), TypeRef.Number.WithNull, TypeRef.Number.WithNull,
            NaturalOrder, expected: true, expectedFast: false);
        await Check(new NumberValue(0), Value.Null, TypeRef.Number.WithNull, TypeRef.Number.WithNull,
            NaturalOrder, expected: false, expectedFast: false);

        Instruction[] less = [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Less), new(OpCode.Return)];
        await Check(new StringValue("\uE000"), new StringValue("\U00010000"), TypeRef.String, TypeRef.String, less, true, true);
        await Check(new PathValue("\U00010000", root), new StringValue("\uE000"), TypeRef.Path, TypeRef.String, less, false, true);
        await Check(new PathValue("z", root, LocalSpelling: "a"), new PathValue("b", root),
            TypeRef.Path, TypeRef.Path, less, true, true);
        await Check(new NumberValue(1), new NumberValue(2), TypeRef.Number, TypeRef.Number,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Greater), new(OpCode.Return)], false, true);
        await Check(new NumberValue(1), new NumberValue(2), TypeRef.Number, TypeRef.Number,
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add), new(OpCode.LoadLocal, 1),
             new(OpCode.Less), new(OpCode.Return)], false, false, expectedMiss: false);

        await CheckLiveTargets();

        async Task CheckLiveTargets()
        {
            FunctionCode Leaf(string name, OpCode compare) => new(name, 2, 2,
                [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(compare), new(OpCode.Return)],
                TypeRef.Bool, ParameterTypes: [TypeRef.Number, TypeRef.Number]);
            Module portable = new([new NumberValue(1), new NumberValue(2)],
                [new("caller", 0, 0,
                    [new(OpCode.MakeClosure, 3), new(OpCode.MakeClosure, 1), new(OpCode.Call, 1),
                     new(OpCode.JumpIfFalse, 9), new(OpCode.MakeClosure, 3), new(OpCode.MakeClosure, 2),
                     new(OpCode.Call, 1), new(OpCode.Not), new(OpCode.Return),
                     new(OpCode.False), new(OpCode.Return)], TypeRef.Bool),
                 Leaf("ascending", OpCode.Less), Leaf("descending", OpCode.Greater),
                 new("apply", 1, 1,
                    [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Call, 2),
                     new(OpCode.Return)], TypeRef.Bool)]);
            var profile = new RuntimeProfile(instructionSites: true);
            var result = (BoolValue)await Interpreter.ExecuteAsync(RoundTrip(Binding.Bind(RoundTrip(portable), new(root, root))),
                new(WorkingDirectory: root) { Profile = profile });
            require(result.Data, "The same call site observes both actual policy targets");
            var targets = profile.Drain().Where(s => s.Name == "vm.call"
                && s.Attributes["function.name"] == "apply").Select(s => s.Attributes["target.function.name"]).ToArray();
            require(targets.SequenceEqual(["ascending", "descending"]), "Logical call sites retain both selected targets");
        }

        async Task Check(Value left, Value right, TypeRef leftType, TypeRef rightType, Instruction[] code,
            bool expected, bool expectedFast, bool? expectedMiss = null)
        {
            Module portable = new([left, right],
                [new("caller", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Constant, 0), new(OpCode.Constant, 1),
                    new(OpCode.Call, 2), new(OpCode.Return)], TypeRef.Bool),
                 new("predicate", 2, 2, code, TypeRef.Bool, ParameterTypes: [leftType, rightType])]);
            var executable = RoundTrip(Binding.Bind(RoundTrip(portable), new(root, root)));
            var profile = new RuntimeProfile(instructionSites: true);
            var value = (BoolValue)await Interpreter.ExecuteAsync(executable, new(WorkingDirectory: root) { Profile = profile });
            require(value.Data == expected, $"Selected predicate with {left} and {right}");
            var samples = profile.Drain();
            var target = samples.Single(s => s.Name == "vm.function" && s.Attributes["function.name"] == "predicate");
            require(target.Metrics["predicate_calls"] == (expectedFast ? 1UL : 0UL)
                && target.Metrics["calls"] == (expectedFast ? 0UL : 1UL), "Target accounting separates calls from physical frames");
            require(target.Metrics["predicate_guard_misses"] == ((expectedMiss ?? !expectedFast) ? 1UL : 0UL),
                "Target guard failure preserves ordinary activation");
            require(target.Metrics["instructions"] > 0, "Logical predicate instructions survive frame removal");
        }
    }

    private static readonly Instruction[] NaturalOrder =
    [
        new(OpCode.LoadLocal, 0), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 7),
        new(OpCode.LoadLocal, 1), new(OpCode.IsNull), new(OpCode.Not), new(OpCode.Return),
        new(OpCode.LoadLocal, 1), new(OpCode.IsNull), new(OpCode.JumpIfFalse, 12),
        new(OpCode.False), new(OpCode.Return),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Less), new(OpCode.JumpIfFalse, 18),
        new(OpCode.True), new(OpCode.Return),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.GreaterEqual), new(OpCode.JumpIfFalse, 24),
        new(OpCode.False), new(OpCode.Return),
        new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Equal), new(OpCode.Dup),
        new(OpCode.JumpIfFalse, 33), new(OpCode.Pop), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 1),
        new(OpCode.NotEqual), new(OpCode.Return)
    ];

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }
}
