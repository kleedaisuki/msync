using System.Collections.Immutable;

namespace Msync.Optimization.Execution;

public enum NativeWorkerType : byte { Index, Number, Bool, Word32 }

public enum NativeWorkerOp : byte
{
    Move, IndexConstant, NumberConstant, BoolConstant,
    LoadIndex, LoadNumber,
    IndexAdd, IndexSubtract, IndexEqual, IndexNotEqual, IndexLess, IndexGreaterEqual,
    NumberAdd, NumberSubtract, NumberMultiply, NumberDivide, NumberNegate,
    NumberEqual, NumberNotEqual, NumberLess, NumberGreaterEqual, NumberLessNaNLast, BoolNot,
    BranchIfFalse, BranchIfTrue, Jump, Return, Nop, StoreIndex, StoreNumber,
    Word32Constant, Word32Intrinsic, Word32ToNumber
}

/// <summary>
/// Typed primitive operands are slot indices. Data is a branch target, a row
/// field byte offset, or the literal bits for an Index, Bool or Word32
/// constant. Word32 operands and results occupy the low 32 bits of a slot
/// with the upper bits zero. Shift and rotate counts are proven in 0..31.
/// </summary>
public readonly record struct NativeWorkerInstruction(NativeWorkerOp Op, int Destination = 0,
    int Left = 0, int Right = 0, int Data = 0, double Constant = 0, NumericIntrinsic Intrinsic = NumericIntrinsic.None)
{
    public bool ReadsWord32Count => Op == NativeWorkerOp.Word32Intrinsic && Intrinsic is
        NumericIntrinsic.ShiftLeft or NumericIntrinsic.ShiftRight or NumericIntrinsic.ShiftRightSigned
        or NumericIntrinsic.RotateLeft or NumericIntrinsic.RotateRight;

    public bool ReadsWord32Right => ReadsWord32Count || Op == NativeWorkerOp.Word32Intrinsic && Intrinsic is
        NumericIntrinsic.And or NumericIntrinsic.Or or NumericIntrinsic.Xor;
}

public readonly record struct NativeWorkerPc(int Function, int Pc);
public readonly record struct NativeWorkerPredicate(int Operation, int Function, ExecutionTargets.Target Target);

/// <summary>
/// A non-suspending function version. Its producer proves that all row indices
/// originate in the admitted storage, not in unchecked guest Numbers.
/// Instructions do not observe live object references.
/// Stores require authoritative writable storage, never a copied read projection.
/// </summary>
public sealed record NativeWorkerPlan(ImmutableArray<NativeWorkerInstruction> Instructions,
    ImmutableArray<NativeWorkerType> Slots, ImmutableArray<int> Parameters, int Result, int RowStride = 0)
{
    public ImmutableArray<ImmutableArray<NativeWorkerPc>> LogicalInstructions { get; init; }
    public ImmutableArray<NativeWorkerPredicate> Predicates { get; init; }
}
