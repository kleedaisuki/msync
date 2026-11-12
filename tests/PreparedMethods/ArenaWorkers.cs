using System.Buffers.Binary;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

internal static class ArenaWorkers
{
    private static readonly TypeRef Owner = new(TypeKind.Storage);
    private static readonly TypeRef Number = TypeRef.Number;

    internal static void Run()
    {
        var child = Prepare(Method("dynamic-child-parent", [Owner, Number, TypeRef.Bool], 3, Number,
        [
            new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2),
            Arena("child", 3), Arena("parent", 2), new(OpCode.Return)
        ]));
        Require(!child.WritesArenaRows && child.Exits.IsEmpty
            && child.Worker.RowStride == ArenaNumberRowLayout.Stride
            && child.Worker.Slots[child.Worker.Result] == NativeWorkerType.Index,
            "Arena reads must select original lanes, without a reconstructed Scope shape.");
        Require(child.Worker.Instructions.Any(op => op.Op == NativeWorkerOp.BranchIfFalse),
            "A dynamic direction requires a selected raw child load.");
        byte[] rows = new byte[4 * ArenaNumberRowLayout.Stride];
        Write(1, ArenaNumberRowLayout.Left, 2);
        Write(1, ArenaNumberRowLayout.Right, 3);
        Write(2, ArenaNumberRowLayout.Parent, 1);
        Write(3, ArenaNumberRowLayout.Parent, 2);
        Require(WorkerModel.Run(child.Worker, rows, Inputs(child, 0, 1, 1)) == 1
            && WorkerModel.Run(child.Worker, rows, Inputs(child, 0, 1, 0)) == 2,
            "The primitive worker must select exactly the requested child, then its actual parent.");

        var writer = Prepare(Method("link-and-publish", [Owner, Number, TypeRef.Bool, Number, TypeRef.Bool], 6, TypeRef.Null,
        [
            new(OpCode.LoadLocal, 0), new(OpCode.InitializeLocal, 5),
            new(OpCode.LoadLocal, 5), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3),
            Arena("set_child", 4), new(OpCode.Pop),
            new(OpCode.LoadLocal, 5), new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 1),
            Arena("set_parent", 3), new(OpCode.Pop),
            new(OpCode.LoadLocal, 4), new(OpCode.JumpIfFalse, 20),
            new(OpCode.LoadLocal, 5), new(OpCode.LoadLocal, 1), Arena("set_root", 2),
            new(OpCode.Pop), new(OpCode.Jump, 20),
            new(OpCode.Null), new(OpCode.Return)
        ]));
        Require(writer.WritesArenaRows && writer.ReturnsNull && writer.Exits is [{ Pc: 15 } exit]
            && exit.Locals is [{ Local: 5, Register: -1, Access: >= 0 }],
            "A reached root publication must retain the original actual owner, not restart the row writes.");
        Require(writer.Worker.LogicalInstructions.SelectMany(step => step).All(pc => pc.Pc != 17),
            "The canonical root effect is not a native logical instruction.");
        foreach (bool publish in new[] { false, true })
        {
            Write(1, ArenaNumberRowLayout.Left, 2);
            Write(3, ArenaNumberRowLayout.Parent, 0);
            long disposition = WorkerModel.Run(writer.Worker, rows, Inputs(writer, 0, 1, 1, 3, publish ? 1 : 0));
            Require(disposition == (publish ? 1 : 0)
                && Read(1, ArenaNumberRowLayout.Left) == 3 && Read(3, ArenaNumberRowLayout.Parent) == 1,
                "Prior committed link writes survive an exact selected effect exit.");
        }
        var suffix = Prepare(Method("dynamic-child-suffix", [Owner, Number, TypeRef.Bool, Number, TypeRef.Bool], 5, TypeRef.Null,
        [
            new(OpCode.LoadLocal, 4), new(OpCode.JumpIfTrue, 8),
            new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.LoadLocal, 2), new(OpCode.LoadLocal, 3),
            Arena("set_child", 4), new(OpCode.Pop), new(OpCode.Null), new(OpCode.Return)
        ]));
        foreach (bool left in new[] { false, true })
        foreach (bool skip in new[] { false, true })
        {
            List<NativeWorkerPc> observed = [];
            _ = WorkerModel.Run(suffix.Worker, rows, Inputs(suffix, 0, 1, left ? 1 : 0, 3, skip ? 1 : 0), observed: observed);
            Require(observed.Count(pc => pc.Pc == 7) == (skip ? 0 : 1),
                "The original Pop after a primitive's internal branch belongs to both selected field paths, once.");
        }
        Console.WriteLine("PASS arena raw dynamic direction, exact Pop replay, same-owner stores, and retained owner at publication");

        void Write(int row, int offset, int value) =>
            BinaryPrimitives.WriteInt32LittleEndian(rows.AsSpan(row * ArenaNumberRowLayout.Stride + offset), value);
        int Read(int row, int offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(rows.AsSpan(row * ArenaNumberRowLayout.Stride + offset));
    }

    private static long[] Inputs(IndexedMethodPlan plan, params long[] source) =>
        plan.Inputs.Select(input => source[plan.Accesses[input.Access].Operand]).ToArray();

    private static Instruction Arena(string name, int arity) => new(OpCode.NativeCall, arity, Text: "arena." + name);

    private static Module Method(string name, TypeRef[] inputs, int locals, TypeRef result, Instruction[] code) =>
        PortableMethodImage.Bind(PortableMethodImage.Portable(name, inputs, locals, result, [], code));

    private static IndexedMethodPlan Prepare(Module image) =>
        ExecutionTargets.For(image).TryExecutionWorker(image, 1, out var plan) && plan is { UsesArenaRows: true }
            ? plan : throw new InvalidOperationException($"{image.Functions[1].Name} did not lower as an arena method.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
