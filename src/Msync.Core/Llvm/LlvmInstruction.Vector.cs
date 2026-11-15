// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instructions.h/.cpp.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmExtractElementInstruction : LlvmInstruction
{
    public LlvmExtractElementInstruction(LlvmValue vector, LlvmValue index)
        : base(((LlvmVectorType)vector.Type).ElementType, LlvmOpcode.ExtractElement, [vector, index]) =>
        Debug.Assert(IsValidOperands(vector, index));

    public LlvmValue VectorOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue IndexOperand { get => GetOperand(1)!; set => SetOperand(1, value); }
    public LlvmVectorType VectorOperandType => (LlvmVectorType)VectorOperand.Type;
    public static bool IsValidOperands(LlvmValue vector, LlvmValue index) => vector.Type.IsVectorTy() && index.Type.IsIntegerTy();
    protected override LlvmInstruction CloneImpl() => new LlvmExtractElementInstruction(VectorOperand, IndexOperand);
}

public sealed class LlvmInsertElementInstruction : LlvmInstruction
{
    public LlvmInsertElementInstruction(LlvmValue vector, LlvmValue element, LlvmValue index)
        : base(vector.Type, LlvmOpcode.InsertElement, [vector, element, index]) =>
        Debug.Assert(IsValidOperands(vector, element, index));

    public new LlvmVectorType Type => (LlvmVectorType)base.Type;
    public LlvmValue VectorOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue InsertedElementOperand { get => GetOperand(1)!; set => SetOperand(1, value); }
    public LlvmValue IndexOperand { get => GetOperand(2)!; set => SetOperand(2, value); }
    public static bool IsValidOperands(LlvmValue vector, LlvmValue element, LlvmValue index) =>
        vector.Type is LlvmVectorType type && ReferenceEquals(element.Type, type.ElementType) && index.Type.IsIntegerTy();
    protected override LlvmInstruction CloneImpl() => new LlvmInsertElementInstruction(VectorOperand, InsertedElementOperand, IndexOperand);
}

public sealed partial class LlvmShuffleVectorInstruction : LlvmInstruction
{
    public const int PoisonMaskElem = -1;
    private int[] mask = [];
    private IReadOnlyList<int> maskView = Array.Empty<int>();
    private LlvmConstant bitcodeMask = null!;

    public LlvmShuffleVectorInstruction(LlvmValue vector, IReadOnlyList<int> mask)
        : this(vector, LlvmPoisonValue.Get(vector.Type), mask) { }
    public LlvmShuffleVectorInstruction(LlvmValue vector, LlvmConstant mask)
        : this(vector, LlvmPoisonValue.Get(vector.Type), mask) { }
    public LlvmShuffleVectorInstruction(LlvmValue left, LlvmValue right, LlvmConstant mask)
        : base(LlvmVectorType.Get(((LlvmVectorType)left.Type).ElementType, ((LlvmVectorType)mask.Type).ElementCount),
            LlvmOpcode.ShuffleVector, [left, right])
    {
        Debug.Assert(IsValidOperands(left, right, mask));
        SetShuffleMask(GetShuffleMask(mask));
    }
    public LlvmShuffleVectorInstruction(LlvmValue left, LlvmValue right, IReadOnlyList<int> mask)
        : base(LlvmVectorType.Get(((LlvmVectorType)left.Type).ElementType, (uint)mask.Count,
            left.Type is LlvmScalableVectorType), LlvmOpcode.ShuffleVector, [left, right])
    {
        Debug.Assert(IsValidOperands(left, right, mask));
        SetShuffleMask(mask);
    }

    public new LlvmVectorType Type => (LlvmVectorType)base.Type;
    public LlvmValue Left { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue Right { get => GetOperand(1)!; set => SetOperand(1, value); }
    public IReadOnlyList<int> ShuffleMask => maskView;
    public LlvmConstant ShuffleMaskForBitcode => bitcodeMask;
    public int GetMaskValue(int element) => mask[element];
    public IReadOnlyList<int> GetShuffleMask() => maskView;
    public bool ChangesLength => ((LlvmVectorType)Left.Type).ElementCount.KnownMinValue != (uint)mask.Length;
    public bool IncreasesLength => ((LlvmVectorType)Left.Type).ElementCount.KnownMinValue < (uint)mask.Length;

    public void SetShuffleMask(IReadOnlyList<int> shuffleMask)
    {
        mask = shuffleMask.ToArray();
        maskView = Array.AsReadOnly(mask);
        bitcodeMask = ConvertShuffleMaskForBitcode(mask, Type);
    }

    public void Commute()
    {
        int count = (int)((LlvmFixedVectorType)Left.Type).NumElements;
        int[] replacement = (int[])mask.Clone();
        CommuteShuffleMask(replacement, (uint)count);
        SetShuffleMask(replacement);
        GetOperandUse(0).Swap(GetOperandUse(1));
    }

    public static void CommuteShuffleMask(Span<int> shuffleMask, uint inputElements)
    {
        for (int index = 0; index < shuffleMask.Length; ++index)
        {
            if (shuffleMask[index] == PoisonMaskElem) continue;
            shuffleMask[index] += shuffleMask[index] < inputElements ? (int)inputElements : -(int)inputElements;
            Debug.Assert(shuffleMask[index] >= 0 && shuffleMask[index] < inputElements * 2);
        }
    }

    public static bool IsValidOperands(LlvmValue left, LlvmValue right, IReadOnlyList<int> shuffleMask)
    {
        if (left.Type is not LlvmVectorType || !ReferenceEquals(left.Type, right.Type)) return false;
        if (left.Type is LlvmScalableVectorType)
            return shuffleMask[0] is 0 or PoisonMaskElem && AllEqual(shuffleMask);
        long maximum = ((LlvmFixedVectorType)left.Type).NumElements * 2L;
        foreach (int element in shuffleMask) if (element != PoisonMaskElem && element >= maximum) return false;
        return true;
    }

    public static bool IsValidOperands(LlvmValue left, LlvmValue right, LlvmValue shuffleMask)
    {
        if (!left.Type.IsVectorTy() || !ReferenceEquals(left.Type, right.Type)) return false;
        if (shuffleMask.Type is not LlvmVectorType type || !type.ElementType.IsIntegerTy(32) ||
            (type is LlvmScalableVectorType) != (left.Type is LlvmScalableVectorType)) return false;
        if (shuffleMask is LlvmUndefValue or LlvmConstantAggregateZero) return true;
        if (type is LlvmScalableVectorType || shuffleMask is not LlvmConstant constant) return false;
        ulong maximum = ((LlvmFixedVectorType)left.Type).NumElements * 2UL;
        for (uint index = 0; index < ((LlvmFixedVectorType)type).NumElements; ++index)
        {
            var element = constant.GetAggregateElement(index);
            if (element is LlvmConstantInt integer) { if (integer.Value.Uge(new(integer.Value.BitWidth, maximum))) return false; }
            else if (element is not LlvmUndefValue) return false;
        }
        return true;
    }

    public static int[] GetShuffleMask(LlvmConstant shuffleMask)
    {
        var count = ((LlvmVectorType)shuffleMask.Type).ElementCount;
        int[] result = new int[count.KnownMinValue];
        if (shuffleMask is LlvmConstantAggregateZero) return result;
        if (shuffleMask is LlvmUndefValue) { Array.Fill(result, PoisonMaskElem); return result; }
        Debug.Assert(!count.IsScalable);
        for (uint index = 0; index < count.KnownMinValue; ++index)
        {
            var element = shuffleMask.GetAggregateElement(index)!;
            result[index] = element is LlvmUndefValue ? PoisonMaskElem : unchecked((int)((LlvmConstantInt)element).Value.GetZExtValue());
        }
        return result;
    }

    public static LlvmConstant ConvertShuffleMaskForBitcode(IReadOnlyList<int> shuffleMask, LlvmVectorType resultType)
    {
        var integer = LlvmIntegerType.Get(resultType.Context, 32);
        if (resultType is LlvmScalableVectorType)
        {
            Debug.Assert(AllEqual(shuffleMask));
            var type = LlvmVectorType.Get(integer, (uint)shuffleMask.Count, true);
            return shuffleMask[0] == 0 ? LlvmConstant.GetNullValue(type) : LlvmPoisonValue.Get(type);
        }
        var elements = new LlvmConstant[shuffleMask.Count];
        for (int index = 0; index < elements.Length; ++index)
            elements[index] = shuffleMask[index] == PoisonMaskElem ? LlvmPoisonValue.Get(integer)
                : LlvmConstantInt.Get(integer, unchecked((ulong)shuffleMask[index]));
        return LlvmConstantVector.Get(elements);
    }

    private static bool AllEqual(IReadOnlyList<int> values)
    {
        for (int index = 1; index < values.Count; ++index) if (values[index] != values[0]) return false;
        return true;
    }
    protected override LlvmInstruction CloneImpl() => new LlvmShuffleVectorInstruction(Left, Right, mask);
}
