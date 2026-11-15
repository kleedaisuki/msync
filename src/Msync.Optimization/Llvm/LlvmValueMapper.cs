// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Transforms/Utils/ValueMapper.h
// and lib/Transforms/Utils/ValueMapper.cpp Mapper::mapValue/remapInstruction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

[Flags]
public enum LlvmRemapFlags
{
    None = 0, NoModuleLevelChanges = 1, IgnoreMissingLocals = 2,
    ReuseAndMutateDistinctMDs = 4, NullMapMissingGlobalValues = 8,
    DoNotRemapAtoms = 16, Importing = 32
}

public static class LlvmValueMapper
{
    public static LlvmValue? MapValue(LlvmValue value, IDictionary<LlvmValue, LlvmValue> valueMap,
        LlvmRemapFlags flags = LlvmRemapFlags.None) => new Mapper(valueMap, flags).MapValue(value);

    public static void RemapInstruction(LlvmInstruction instruction, IDictionary<LlvmValue, LlvmValue> valueMap,
        LlvmRemapFlags flags = LlvmRemapFlags.None)
    {
        var mapper = new Mapper(valueMap, flags);
        foreach (var operand in instruction.OperandUses)
        {
            var mapped = mapper.MapValue(operand.Value!);
            if (mapped is not null) operand.Set(mapped);
            else Debug.Assert((flags & LlvmRemapFlags.IgnoreMissingLocals) != 0, "Referenced value not in value map.");
        }
        if (instruction is not LlvmPhiInstruction phi) return;
        for (int index = 0; index < phi.IncomingCount; ++index)
        {
            var mapped = mapper.MapValue(phi.GetIncomingBlock(index));
            if (mapped is not null) phi.SetIncomingBlock(index, (LlvmBasicBlock)mapped);
            else Debug.Assert((flags & LlvmRemapFlags.IgnoreMissingLocals) != 0, "Referenced block not in value map.");
        }
    }

    private readonly struct Mapper
    {
        private readonly IDictionary<LlvmValue, LlvmValue> values;
        private readonly LlvmRemapFlags flags;
        internal Mapper(IDictionary<LlvmValue, LlvmValue> values, LlvmRemapFlags flags)
        {
            Debug.Assert((flags & (LlvmRemapFlags.IgnoreMissingLocals | LlvmRemapFlags.NullMapMissingGlobalValues))
                != (LlvmRemapFlags.IgnoreMissingLocals | LlvmRemapFlags.NullMapMissingGlobalValues));
            this.values = values; this.flags = flags;
        }

        internal LlvmValue? MapValue(LlvmValue value)
        {
            if (values.TryGetValue(value, out var existing))
            { Debug.Assert(existing is not null); return existing; }
            if (value is LlvmGlobalValue)
                return (flags & LlvmRemapFlags.NullMapMissingGlobalValues) != 0 ? null : values[value] = value;
            if (value is not LlvmConstant constant) return null;
            int index = 0;
            LlvmValue? mapped = null;
            for (; index < constant.OperandCount; ++index)
            {
                var operand = constant.GetOperand(index)!;
                mapped = MapValue(operand);
                Debug.Assert(mapped is not null || (flags & LlvmRemapFlags.NullMapMissingGlobalValues) != 0);
                if (mapped is null) return null;
                if (!ReferenceEquals(mapped, operand)) break;
            }
            if (index == constant.OperandCount) return values[value] = constant;
            LlvmConstant[] operands = new LlvmConstant[constant.OperandCount];
            for (int previous = 0; previous < index; ++previous) operands[previous] = (LlvmConstant)constant.GetOperand(previous)!;
            operands[index++] = (LlvmConstant)mapped!;
            for (; index < operands.Length; ++index)
            {
                mapped = MapValue(constant.GetOperand(index)!);
                Debug.Assert(mapped is not null || (flags & LlvmRemapFlags.NullMapMissingGlobalValues) != 0);
                if (mapped is null) return null;
                operands[index] = (LlvmConstant)mapped;
            }
            return values[value] = constant switch
            {
                LlvmConstantArray array => LlvmConstantArray.Get(array.ArrayType, operands),
                LlvmConstantStruct structure => LlvmConstantStruct.Get(structure.StructType, operands),
                _ => throw new NotSupportedException("ValueMapper does not represent constant reconstruction for " + constant.GetType().Name)
            };
        }
    }
}
