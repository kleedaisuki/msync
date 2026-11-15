// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp ParseMemoryInst,
// getOrCreateResult and overridingStores; generic TargetTransformInfo defaults.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private readonly struct MemoryInst
    {
        internal MemoryInst(LlvmInstruction instruction, Dictionary<string, ulong>? metrics = null)
        {
            Instruction = instruction;
            IsMemSet = LlvmMemIntrinsic.TryGet(instruction, out var memory) && memory.IsMemSet;
            MemSet = memory;
            PointerOperand = instruction switch
            {
                LlvmLoadInstruction load => load.PointerOperand,
                LlvmStoreInstruction store => store.PointerOperand,
                _ => IsMemSet ? LlvmValueTracking.StripPointerCasts(memory.RawDest, metrics) : null
            };
        }
        internal LlvmInstruction Instruction { get; }
        internal LlvmValue? PointerOperand { get; }
        internal LlvmMemIntrinsic MemSet { get; }
        internal bool IsMemSet { get; }
        internal bool IsValid => PointerOperand is not null;
        internal bool IsLoad => Instruction is LlvmLoadInstruction;
        internal bool IsStore => Instruction is LlvmStoreInstruction || IsMemSet;
        internal int MatchingId => IsMemSet ? 0 : -1;
        internal LlvmType? ValueType => Instruction switch
        {
            LlvmLoadInstruction load => load.Type,
            LlvmStoreInstruction store => store.ValueOperand.Type,
            _ => null
        };
        internal bool IsAtomic => Instruction switch
        {
            LlvmLoadInstruction load => load.IsAtomic,
            LlvmStoreInstruction store => store.IsAtomic,
            _ => false
        };
        internal bool IsVolatile => Instruction switch
        {
            LlvmLoadInstruction load => load.IsVolatile,
            LlvmStoreInstruction store => store.IsVolatile,
            _ => !IsMemSet || MemSet.IsVolatile
        };
        internal bool IsUnordered => Instruction switch
        {
            LlvmLoadInstruction load => load.IsUnordered,
            LlvmStoreInstruction store => store.IsUnordered,
            _ => !IsVolatile
        };
        internal bool MayReadFromMemory => !IsMemSet && Instruction.MayReadFromMemory();
    }

    private static bool OverridingStores(MemoryInst earlier, MemoryInst later) =>
        earlier.PointerOperand == later.PointerOperand && earlier.ValueType is { } type && type == later.ValueType &&
        earlier.MatchingId == later.MatchingId && earlier.IsUnordered && later.IsUnordered;
}
