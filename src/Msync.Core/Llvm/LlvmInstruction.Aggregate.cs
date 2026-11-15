// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instructions.h
// ExtractValueInst/InsertValueInst; lib/IR/Instructions.cpp getIndexedType/init/cloneImpl.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmExtractValueInstruction : LlvmInstruction
{
    public LlvmExtractValueInstruction(LlvmValue aggregate, IReadOnlyList<uint> indices)
        : base(GetIndexedType(aggregate.Type, indices)!, LlvmOpcode.ExtractValue, [aggregate])
    {
        Debug.Assert(indices.Count != 0);
        Debug.Assert(Type is not null);
        Indices = Array.AsReadOnly(indices.ToArray());
    }

    public LlvmValue AggregateOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public IReadOnlyList<uint> Indices { get; }
    public int IndexCount => Indices.Count;
    public bool HasIndices => true;

    public static LlvmType? GetIndexedType(LlvmType aggregate, IReadOnlyList<uint> indices)
    {
        foreach (uint index in indices)
        {
            switch (aggregate)
            {
                case LlvmArrayType array:
                    if (index >= array.NumElements) return null;
                    aggregate = array.ElementType;
                    break;
                case LlvmStructType structure:
                    if (index >= structure.Elements.Count) return null;
                    aggregate = structure.Elements[(int)index];
                    break;
                default:
                    return null;
            }
        }
        return aggregate;
    }

    protected override LlvmInstruction CloneImpl() => new LlvmExtractValueInstruction(AggregateOperand, Indices);
}

public sealed class LlvmInsertValueInstruction : LlvmInstruction
{
    public LlvmInsertValueInstruction(LlvmValue aggregate, LlvmValue value, IReadOnlyList<uint> indices)
        : base(aggregate.Type, LlvmOpcode.InsertValue, [aggregate, value])
    {
        Debug.Assert(indices.Count != 0);
        Debug.Assert(ReferenceEquals(LlvmExtractValueInstruction.GetIndexedType(aggregate.Type, indices), value.Type));
        Indices = Array.AsReadOnly(indices.ToArray());
    }

    public LlvmValue AggregateOperand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue InsertedValueOperand { get => GetOperand(1)!; set => SetOperand(1, value); }
    public IReadOnlyList<uint> Indices { get; }
    public int IndexCount => Indices.Count;
    public bool HasIndices => true;
    protected override LlvmInstruction CloneImpl() => new LlvmInsertValueInstruction(AggregateOperand, InsertedValueOperand, Indices);
}
