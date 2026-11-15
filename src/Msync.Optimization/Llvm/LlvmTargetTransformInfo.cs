// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// TargetTransformInfoImpl.h generic CRTP getGEPCost and its instruction dispatch.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmTargetCostKind { ReciprocalThroughput, Latency, CodeSize, SizeAndLatency }

public sealed partial class LlvmTargetTransformInfo(LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
{
    public LlvmInstructionCost GetInstructionCost(LlvmGetElementPtrInstruction instruction, LlvmTargetCostKind kind)
    {
        Count("tti.instruction.gep_queries");
        LlvmType? access = null;
        if (instruction.HasOneUser)
            access = instruction.Users.First() switch
            {
                LlvmLoadInstruction load => load.Type,
                LlvmStoreInstruction store => store.ValueOperand.Type,
                _ => null
            };
        LlvmInstructionCost cost = GetGepCost(instruction.SourceElementType, instruction.PointerOperand,
            instruction.Indices.ToArray(), kind, access);
        Count(cost.IsFree ? "tti.instruction.free" : "tti.instruction.basic");
        return cost;
    }

    public LlvmInstructionCost GetGepCost(LlvmType source, LlvmValue pointer, IReadOnlyList<LlvmValue> indices,
        LlvmTargetCostKind kind, LlvmType? access = null)
    {
        var global = LlvmValueTracking.StripPointerCasts(pointer) as LlvmGlobalValue;
        if (indices.Count == 0) return global is null ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic;
        uint width = layout.GetPointerTypeSizeInBits(pointer.Type);
        var offset = LlvmAPInt.GetZero(width);
        long scale = 0;
        LlvmType current = source;
        for (int ordinal = 0; ordinal < indices.Count; ++ordinal)
        {
            var constant = indices[ordinal] as LlvmConstantInt;
            if (ordinal > 0 && current is LlvmStructType structure)
            {
                Debug.Assert(constant is not null);
                int field = checked((int)constant!.Value.GetZExtValue());
                offset += new LlvmAPInt(width, layout.GetStructLayout(structure).GetElementOffset((uint)field).GetFixedValue());
                current = structure.Elements[field];
                continue;
            }
            bool vectorElement = ordinal > 0 && current is LlvmVectorType;
            if (ordinal > 0) current = current switch
            {
                LlvmArrayType array => array.ElementType,
                LlvmVectorType vector => vector.ElementType,
                _ => throw new NotSupportedException("Sequential GEP type has no represented element type.")
            };
            if (current.IsScalableTy()) return LlvmInstructionCost.Basic;
            long size = unchecked((long)(vectorElement ? layout.GetTypeStoreSize(current) : layout.GetTypeAllocSize(current)).GetFixedValue());
            if (constant is not null)
                offset += constant.Value.SExtOrTrunc(width) * new LlvmAPInt(width, unchecked((ulong)size));
            else
            {
                if (scale != 0) return LlvmInstructionCost.Basic;
                scale = size;
            }
        }
        access ??= current;
        return IsLegalAddressingMode(access, global, offset.SExtOrTrunc(64).GetSExtValue(),
            global is null, scale, ((LlvmPointerType)pointer.Type.GetScalarType()).AddressSpace)
            ? LlvmInstructionCost.Free : LlvmInstructionCost.Basic;
    }

    public bool IsLegalAddressingMode(LlvmType? type, LlvmGlobalValue? global, long offset,
        bool hasBaseRegister, long scale, uint addressSpace) => global is null && offset == 0 && scale is 0 or 1;

    public bool IsProfitableToHoist(LlvmInstruction instruction) => true;

    private void Count(string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
