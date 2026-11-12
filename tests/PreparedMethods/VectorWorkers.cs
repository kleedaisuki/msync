using System.Buffers.Binary;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

internal static class VectorWorkers
{
    internal static void Run()
    {
        var code = new Instruction[]
        {
            new(OpCode.LoadLocal, 1), new(OpCode.InitializeLocal, 2),
            new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2),
            new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2), Vector("get", 2),
            new(OpCode.Constant, 1), new(OpCode.Add), Vector("set", 3), new(OpCode.Pop),
            new(OpCode.LoadLocal, 2), new(OpCode.Constant, 0), new(OpCode.Multiply), new(OpCode.Constant, 1),
            new(OpCode.Add), new(OpCode.InitializeLocal, 3),
            new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 0), Vector("size", 1),
            new(OpCode.GreaterEqual), new(OpCode.JumpIfFalse, 24), new(OpCode.Null), new(OpCode.Return),
            new(OpCode.LoadLocal, 3), new(OpCode.StoreLocal, 2), new(OpCode.Jump, 2)
        };
        Module portable = PortableMethodImage.Portable("vector-walk", [TypeRef.Storage, TypeRef.Number], 4,
            TypeRef.Null, [new NumberValue(2), new NumberValue(1)], code);
        var bound = PortableMethodImage.Bind(portable);
        if (!ExecutionTargets.For(bound).TryExecutionWorker(bound, 1, out var plan)
            || plan is not { WritesScalarStorage: true, ReturnsNull: true, StorageCountAccess: >= 0, Worker.RowStride: 8 })
            throw new InvalidOperationException("The selected ordinal induction must admit direct vector storage writes.");
        foreach (int start in new[] { 0, 1, 5, 30 })
        {
            double[] expected = new double[31];
            for (int i = start; i < expected.Length; i = 2 * i + 1) expected[i]++;
            byte[] data = new byte[expected.Length * sizeof(double)];
            long[] arguments = plan.Inputs.Select(input =>
                input.Access == plan.StorageCountAccess ? (long)expected.Length : start).ToArray();
            _ = WorkerModel.Run(plan.Worker, data, arguments);
            for (int i = 0; i < expected.Length; i++)
                if (BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(i * sizeof(double))) != expected[i])
                    throw new InvalidOperationException("Native vector steps changed the selected write addresses.");
        }

        var badCode = (Instruction[])code.Clone();
        badCode[20] = new(OpCode.Less);
        var unsafeBound = bound with { Functions = [bound.Functions[0], bound.Functions[1] with { Instructions = badCode }] };
        if (ExecutionTargets.For(unsafeBound).TryExecutionWorker(unsafeBound, 1, out var unsafePlan)
            && unsafePlan is { WritesScalarStorage: true })
            throw new InvalidOperationException("The opposite branch cannot prove its next selected address.");
        Console.WriteLine("PASS authoritative vector store induction and canonical fallback for an unproved backedge");
    }

    private static Instruction Vector(string name, int arity) => new(OpCode.NativeCall, arity, Text: "storage.vector_" + name);
}
