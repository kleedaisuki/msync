using System.Numerics;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

internal static class ScalarWorkers
{
    private static readonly TypeRef Number = TypeRef.Number;

    internal static void Run()
    {
        var chain = Method("mix", [Number, Number, TypeRef.Bool, Number], 6,
            [new NumberValue(0), new NumberValue(0x80000000), new NumberValue(31), new NumberValue(1)],
            [
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), Bits("bit_or", 2), new(OpCode.InitializeLocal, 4),
                new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 5),
                new(OpCode.LoadLocal, 5), new(OpCode.LoadLocal, 3), new(OpCode.Less), new(OpCode.JumpIfFalse, 30),
                new(OpCode.LoadLocal, 4), new(OpCode.LoadLocal, 1), Bits("rotate_left", 2),
                new(OpCode.LoadLocal, 2), new(OpCode.JumpIfFalse, 18),
                new(OpCode.Constant, 1), Bits("bit_xor", 2), new(OpCode.Jump, 20),
                Bits("bit_not", 1), new(OpCode.Jump, 20),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2), Bits("bit_and", 2), Bits("shift_right_signed", 2),
                new(OpCode.StoreLocal, 4), new(OpCode.LoadLocal, 5), new(OpCode.Constant, 3), new(OpCode.Add),
                new(OpCode.StoreLocal, 5), new(OpCode.Jump, 6), new(OpCode.LoadLocal, 4), new(OpCode.Return)
            ]);
        var plan = Prepare(chain);
        Require(plan.Worker.Instructions.Count(op => op.Op == NativeWorkerOp.Word32ToNumber) == 1,
            "The selected raw Word32 branch/loop must convert only on return.");
        Require(plan.Worker.Instructions.Count(op => op.Op == NativeWorkerOp.Word32Intrinsic) == 6,
            "The original intrinsics must consume and produce raw U32 slots.");
        var count = plan.Inputs.Single(input => plan.Accesses[input.Access].Operand == 1);
        Require(count is { Type: NativeWorkerType.Word32, ShiftCount: true },
            "The external rotate count must be guarded once in 0..31.");
        Require(plan.Worker.LogicalInstructions.SelectMany(step => step).Distinct().Count()
            == chain.Functions[1].Instructions.Count, "All original branch PC sites need exact observation provenance.");
        int checks = 0;
        foreach (uint word in new uint[] { 0, 1, 31, 255, 0x80000000, uint.MaxValue })
        foreach (int shift in new[] { 0, 1, 31 })
        foreach (bool choose in new[] { false, true })
        foreach (int repetitions in new[] { 0, 1, 32, 130 })
        {
            uint expected = word;
            for (int i = 0; i < repetitions; i++)
            {
                uint rotated = BitOperations.RotateLeft(expected, shift);
                uint mixed = choose ? rotated ^ 0x80000000u : ~rotated;
                expected = unchecked((uint)((int)mixed >> (int)(word & 31)));
            }
            long[] supplied = plan.Inputs.Select(input => plan.Accesses[input.Access].Operand switch
            {
                0 => (long)word,
                1 => shift,
                2 => choose ? 1L : 0L,
                3 => BitConverter.DoubleToInt64Bits(repetitions),
                _ => throw new InvalidOperationException("Unexpected guarded input.")
            }).ToArray();
            Require(WorkerModel.Run(plan.Worker, [], supplied, repetitions + 1) == BitConverter.DoubleToInt64Bits(expected),
                "The raw loop must agree with the unsigned/signed arithmetic oracle.");
            checks++;
        }

        var sign = Prepare(Method("signed-view", [Number], 1, [],
            [new(OpCode.LoadLocal, 0), Bits("bit_not", 1), new(OpCode.Pop), new(OpCode.LoadLocal, 0), new(OpCode.Return)]));
        Require(sign.Inputs.Length == 2 && sign.Inputs[0].Access == sign.Inputs[1].Access
            && sign.Inputs[0].Type != sign.Inputs[1].Type, "One live argument can have independent Number and Word32 views.");
        Require(WorkerModel.Run(sign.Worker, [], [.. sign.Inputs.Select(input =>
            input.Type == NativeWorkerType.Number ? long.MinValue : 0L)]) == long.MinValue,
            "Admitting -0 as an exact Word32 operand cannot change the original Number view.");

        var highBits = Prepare(Method("count-from-high-bits", [Number], 1, [new NumberValue(27)],
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0),
                Bits("shift_right", 2), Bits("rotate_right", 2), new(OpCode.Return)]));
        Require(highBits.Inputs is [{ Type: NativeWorkerType.Word32, ShiftCount: false }],
            "A shifted top-five-bit count is proven from the word, not imposed as a new entry condition.");
        foreach (uint word in new uint[] { 0, 1, 0x80000000, uint.MaxValue })
            Require(WorkerModel.Run(highBits.Worker, [], [word])
                == BitConverter.DoubleToInt64Bits(BitOperations.RotateRight(word, (int)(word >> 27))),
                "The statically bounded high-bit count preserves all original input words.");

        var uncheckedCount = Method("unchecked-count", [Number], 1, [],
            [new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), Bits("pop_count", 1), Bits("rotate_right", 2), new(OpCode.Return)]);
        Reject(uncheckedCount, "PopCount can produce 32 and cannot authorize a checked rotate.");

        var changingCount = Method("changing-count", [Number, TypeRef.Bool], 3, [new NumberValue(31)],
            [
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), Bits("bit_and", 2), new(OpCode.InitializeLocal, 2),
                new(OpCode.LoadLocal, 1), new(OpCode.JumpIfFalse, 14),
                new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2), Bits("shift_right", 2), new(OpCode.Pop),
                new(OpCode.LoadLocal, 2), Bits("bit_not", 1), new(OpCode.StoreLocal, 2), new(OpCode.Jump, 4),
                new(OpCode.LoadLocal, 2), new(OpCode.Return)
            ]);
        Reject(changingCount, "A first-iteration count proof must not survive a backedge that invalidates it.");

        var effect = Method("ordered-effect", [Number], 1, [new StringValue("selected")],
            [
                new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "io.println"), new(OpCode.Pop),
                new(OpCode.LoadLocal, 0), Bits("bit_not", 1), new(OpCode.Return)
            ]);
        Require(!ExecutionScalarWorker.TryPrepare(effect, 1, out _, out var rejected)
            && rejected.Pc == 1 && (rejected.Effects & InstructionEffect.Write) != 0,
            "An entry guard cannot bypass a selected earlier effect.");

        Console.WriteLine($"PASS {checks} Word32 loop oracles, raw phi, exact signed view, checked count and effect boundaries");
    }

    private static Instruction Bits(string name, int arity) => new(OpCode.NativeCall, arity, Text: "bits." + name);

    private static Module Method(string name, TypeRef[] inputs, int locals, Value[] constants, Instruction[] code)
    {
        var portable = PortableMethodImage.Portable(name, inputs, locals, Number, constants, code);
        Require(!ExecutionScalarWorker.TryPrepare(portable, 1, out _, out _),
            "The portable bytecode itself is not an executable native plan.");
        return PortableMethodImage.Bind(portable);
    }

    private static IndexedMethodPlan Prepare(Module image) =>
        ExecutionScalarWorker.TryPrepare(image, 1, out var plan, out var rejected)
            ? plan : throw new InvalidOperationException($"{image.Functions[1].Name}: {rejected}.");

    private static void Reject(Module image, string message) =>
        Require(!ExecutionScalarWorker.TryPrepare(image, 1, out _, out _), message);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
