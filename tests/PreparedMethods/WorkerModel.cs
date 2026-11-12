using System.Buffers.Binary;
using System.Numerics;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

internal static class WorkerModel
{
    internal static long Run(NativeWorkerPlan worker, byte[] rows, long[] arguments, int passes = 1,
        ICollection<NativeWorkerPc>? observed = null)
    {
        long[] slots = new long[worker.Slots.Length];
        if (arguments.Length != worker.Parameters.Length) throw new InvalidOperationException("Wrong test arguments.");
        for (int i = 0; i < arguments.Length; i++) slots[worker.Parameters[i]] = arguments[i];
        int remaining = worker.Instructions.Length * (worker.RowStride == 0 ? passes : rows.Length / worker.RowStride + 1);
        for (int pc = 0; --remaining >= 0; pc++)
        {
            var i = worker.Instructions[pc];
            if (observed is not null && !worker.LogicalInstructions.IsDefaultOrEmpty)
                foreach (var logical in worker.LogicalInstructions[pc]) observed.Add(logical);
            long Left() => slots[i.Left];
            long Right() => slots[i.Right];
            double A() => BitConverter.Int64BitsToDouble(Left());
            double B() => BitConverter.Int64BitsToDouble(Right());
            switch (i.Op)
            {
                case NativeWorkerOp.BranchIfFalse: if (Left() == 0) pc = i.Data - 1; continue;
                case NativeWorkerOp.BranchIfTrue: if (Left() != 0) pc = i.Data - 1; continue;
                case NativeWorkerOp.Jump: pc = i.Data - 1; continue;
                case NativeWorkerOp.Nop: continue;
                case NativeWorkerOp.Return: return Left();
                case NativeWorkerOp.StoreIndex:
                    BinaryPrimitives.WriteInt32LittleEndian(Field(Left(), i.Data), (int)Right());
                    continue;
                case NativeWorkerOp.StoreNumber:
                    BinaryPrimitives.WriteInt64LittleEndian(Field(Left(), i.Data), Right());
                    continue;
            }
            slots[i.Destination] = i.Op switch
            {
                NativeWorkerOp.Move => Left(),
                NativeWorkerOp.IndexConstant or NativeWorkerOp.BoolConstant => i.Data,
                NativeWorkerOp.NumberConstant => BitConverter.DoubleToInt64Bits(i.Constant),
                NativeWorkerOp.Word32Constant => unchecked((uint)i.Data),
                NativeWorkerOp.Word32ToNumber => BitConverter.DoubleToInt64Bits((uint)Left()),
                NativeWorkerOp.Word32Intrinsic => Word(i, (uint)Left(), (uint)Right()),
                NativeWorkerOp.LoadIndex => BinaryPrimitives.ReadInt32LittleEndian(Field(Left(), i.Data)),
                NativeWorkerOp.LoadNumber => BinaryPrimitives.ReadInt64LittleEndian(Field(Left(), i.Data)),
                NativeWorkerOp.IndexAdd => checked((int)Left() + (int)Right()),
                NativeWorkerOp.IndexSubtract => checked((int)Left() - (int)Right()),
                NativeWorkerOp.IndexEqual => Bit(Left() == Right()),
                NativeWorkerOp.IndexNotEqual => Bit(Left() != Right()),
                NativeWorkerOp.IndexLess => Bit(Left() < Right()),
                NativeWorkerOp.IndexGreaterEqual => Bit(Left() >= Right()),
                NativeWorkerOp.NumberAdd => BitConverter.DoubleToInt64Bits(A() + B()),
                NativeWorkerOp.NumberSubtract => BitConverter.DoubleToInt64Bits(A() - B()),
                NativeWorkerOp.NumberMultiply => BitConverter.DoubleToInt64Bits(A() * B()),
                NativeWorkerOp.NumberDivide => BitConverter.DoubleToInt64Bits(A() / B()),
                NativeWorkerOp.NumberNegate => BitConverter.DoubleToInt64Bits(-A()),
                NativeWorkerOp.NumberEqual => Bit(A() == B()),
                NativeWorkerOp.NumberNotEqual => Bit(A() != B()),
                NativeWorkerOp.NumberLess => Bit(A() < B()),
                NativeWorkerOp.NumberLessNaNLast => Bit(A() < B() || !double.IsNaN(A()) && double.IsNaN(B())),
                NativeWorkerOp.NumberGreaterEqual => Bit(A() >= B()),
                NativeWorkerOp.BoolNot => Bit(Left() == 0),
                _ => throw new InvalidOperationException($"Unsupported model operation: {i.Op}."),
            };
        }
        throw new InvalidOperationException("The checked worker did not terminate within its input's step bound.");

        Span<byte> Field(long row, int offset) => rows.AsSpan(checked((int)row * worker.RowStride + offset));
        static long Bit(bool value) => value ? 1 : 0;
    }

    private static uint Word(NativeWorkerInstruction instruction, uint a, uint b)
    {
        if (instruction.ReadsWord32Count && b > 31) throw new InvalidOperationException("Unchecked shift count.");
        return instruction.Intrinsic switch
        {
            NumericIntrinsic.And => a & b,
            NumericIntrinsic.Or => a | b,
            NumericIntrinsic.Xor => a ^ b,
            NumericIntrinsic.Not => ~a,
            NumericIntrinsic.ShiftLeft => a << (int)b,
            NumericIntrinsic.ShiftRight => a >> (int)b,
            NumericIntrinsic.ShiftRightSigned => unchecked((uint)((int)a >> (int)b)),
            NumericIntrinsic.RotateLeft => BitOperations.RotateLeft(a, (int)b),
            NumericIntrinsic.RotateRight => BitOperations.RotateRight(a, (int)b),
            NumericIntrinsic.PopCount => (uint)BitOperations.PopCount(a),
            NumericIntrinsic.LeadingZeroCount => (uint)BitOperations.LeadingZeroCount(a),
            NumericIntrinsic.TrailingZeroCount => (uint)BitOperations.TrailingZeroCount(a),
            NumericIntrinsic.ReverseBytes => BinaryPrimitives.ReverseEndianness(a),
            _ => throw new InvalidOperationException("Invalid Word32 instruction.")
        };
    }
}
