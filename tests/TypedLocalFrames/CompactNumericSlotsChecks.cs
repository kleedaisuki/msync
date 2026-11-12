using Msync.Core;
using Msync.Runtime;

internal static class CompactNumericSlotsChecks
{
    private const string Mutate = "mutate-borrowed-suffix";
    private const long PositiveNaN = Fixtures.NegativeNaN ^ long.MinValue;

    internal static async Task Run(string root, bool metrics, Func<Module, string, IEnumerable<Module>> routes)
    {
        foreach (bool scope in new[] { false, true })
        {
            foreach (Module image in routes(BorrowedPrefix(scope), root))
            {
                RuntimeProfile profile = new(instructionSites: true);
                Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root) { Profile = profile });
                if (scope)
                {
                    var fields = (ScopeValue)result;
                    Require(Bits(fields["zero"]) == long.MinValue, "Detachment changed a lower borrowed negative zero.");
                    Require(Bits(fields["nan"]) == Fixtures.NegativeNaN, "Detachment changed a lower borrowed NaN payload.");
                }
                else Require(result is NumberValue { Data: 30 }, "Nonzero numeric bias materialized the wrong argument.");

                if (metrics)
                {
                    var stats = profile.Drain().Single(sample =>
                        sample.Name == "vm.function" && sample.Attributes["function.name"] == Mutate).Metrics;
                    Require(stats["raw_arguments"] == (scope ? 3UL : 2UL) && stats["detached_frames"] == 1,
                        "The earlier numeric inputs were not borrowed across a detach.");
                    Require(stats["raw_local_writes"] == 3, "The final borrowed argument did not become a private numeric slot.");
                    Require(stats["raw_scope_storage_loads"] == (scope ? 2UL : 0UL), "Wrong Scope operand route.");
                    Require(stats["number_sidecar_arrays"] == 1 && stats["number_sidecar_slots"] == (scope ? 3UL : 2UL),
                        "The detached sidecar did not include exactly its live raw prefix and writable suffix.");
                }
            }
        }
        Console.WriteLine("PASS compact numeric suffix: borrowed prefix, detachment, generic materialization, raw Scope bits");
    }

    internal static IEnumerable<Module> ExitShapes(int iterations)
    {
        const int prefix = 3;
        Module baseline = Fixtures.NumericExit(iterations);
        yield return baseline;
        FunctionCode body = baseline.Functions[0];
        // A native commit must address logical slots, not the compact array's zero-based suffix.
        yield return baseline with
        {
            Functions = [body with
            {
                LocalCount = body.LocalCount + prefix,
                Instructions = body.Instructions.Select(static instruction => instruction.Op switch
                {
                    OpCode.LoadLocal or OpCode.LoadStorage or OpCode.StoreLocal or OpCode.InitializeLocal =>
                        instruction with { A = instruction.A + prefix },
                    _ => instruction
                }).ToArray()
            }]
        };
    }

    private static Module BorrowedPrefix(bool scope)
    {
        const int zero = 0, one = 1, minusThree = 2, ten = 3, minusTwenty = 4, nan = 5;
        Instruction[] leading = scope
            ? [new(OpCode.Constant, zero), new(OpCode.Negate), new(OpCode.Constant, nan), new(OpCode.Negate)]
            : [new(OpCode.Constant, ten), new(OpCode.Constant, minusTwenty), new(OpCode.Negate)];
        Instruction[] ending = scope
            ? [new(OpCode.LoadStorage, 0), new(OpCode.LoadStorage, 1), new(OpCode.NewScope), new(OpCode.Return)]
            : [new(OpCode.LoadLocal, 0), new(OpCode.LoadStorage, 1), new(OpCode.Add), new(OpCode.Return)];
        TypeRef result = scope ? TypeRef.Scope : TypeRef.Number;
        int resultType = scope ? 1 : 0;
        return new(
            [new NumberValue(0), new NumberValue(1), new NumberValue(-3), new NumberValue(10),
             new NumberValue(-20), new NumberValue(BitConverter.Int64BitsToDouble(PositiveNaN))],
            [
                new FunctionCode("compact-prefix", 0, 0,
                    [new(OpCode.MakeClosure, 1), .. leading, new(OpCode.Constant, minusThree), new(OpCode.Negate),
                     new(OpCode.Call, 3, 2), new(OpCode.Return)], result)
                { CodeSignature = new([], resultType, []) },
                new FunctionCode(Mutate, 3, 3,
                    [new(OpCode.LoadLocal, 2), new(OpCode.Constant, one), new(OpCode.Subtract), new(OpCode.StoreLocal, 2),
                     new(OpCode.LoadLocal, 2), new(OpCode.Constant, zero), new(OpCode.Greater), new(OpCode.JumpIfTrue, 0),
                     .. ending], result, ParameterTypes: [TypeRef.Number, TypeRef.Number, TypeRef.Number])
                { CodeSignature = new([0, 0, 0], resultType, []) }
            ], OptimizationLevel: 0)
        {
            CodeTypes = scope ? [new(TypeKind.Number), new(TypeKind.Scope, ScopeLayouts: [0])] : [new(TypeKind.Number)],
            ScopeLayouts = scope ? [new([new("zero", 0), new("nan", 0)])] : []
        };
    }

    private static long Bits(Value value) => BitConverter.DoubleToInt64Bits(((NumberValue)value).Data);

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
