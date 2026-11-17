// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: lib/IR/Instruction.cpp
// Instruction::isIdenticalToWhenDefined / hasSameSpecialState.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public bool IsIdenticalToWhenDefined(LlvmInstruction other, bool intersectAttrs = false)
    {
        if (Opcode != other.Opcode || OperandCount != other.OperandCount || !ReferenceEquals(Type, other.Type)) return false;
        if (OperandCount == 0) return HasSameSpecialState(other, intersectAttrs: intersectAttrs);
        for (int index = 0; index < OperandCount; ++index)
            if (!ReferenceEquals(GetOperand(index), other.GetOperand(index))) return false;
        if (this is LlvmPhiInstruction phi)
        {
            var otherPhi = (LlvmPhiInstruction)other;
            for (int index = 0; index < phi.IncomingCount; ++index)
                if (!ReferenceEquals(phi.GetIncomingBlock(index), otherPhi.GetIncomingBlock(index))) return false;
            return true;
        }
        return HasSameSpecialState(other, intersectAttrs: intersectAttrs);
    }

    public bool HasSameSpecialState(LlvmInstruction other, bool ignoreAlignment = false, bool intersectAttrs = false)
    {
        Debug.Assert(Opcode == other.Opcode);
        return this switch
        {
            LlvmAllocaInstruction left => ReferenceEquals(left.AllocatedType, ((LlvmAllocaInstruction)other).AllocatedType)
                && (ignoreAlignment || left.Alignment == ((LlvmAllocaInstruction)other).Alignment),
            LlvmLoadInstruction left => SameLoadState(left, (LlvmLoadInstruction)other, ignoreAlignment),
            LlvmStoreInstruction left => SameStoreState(left, (LlvmStoreInstruction)other, ignoreAlignment),
            LlvmCmpInstruction left => left.Predicate == ((LlvmCmpInstruction)other).Predicate,
            LlvmCallInstruction left => SameCallState(left, (LlvmCallInstruction)other, intersectAttrs),
            LlvmSwitchInstruction left => SameSwitchCases(left, (LlvmSwitchInstruction)other),
            LlvmInsertValueInstruction left => left.Indices.SequenceEqual(((LlvmInsertValueInstruction)other).Indices),
            LlvmExtractValueInstruction left => left.Indices.SequenceEqual(((LlvmExtractValueInstruction)other).Indices),
            LlvmShuffleVectorInstruction left => left.ShuffleMask.SequenceEqual(((LlvmShuffleVectorInstruction)other).ShuffleMask),
            LlvmGetElementPtrInstruction left => SameGepState(left, (LlvmGetElementPtrInstruction)other),
            LlvmHeapAllocInst left => SameHeapAllocation(left, (LlvmHeapAllocInst)other, ignoreAlignment),
            LlvmFileInInst left => left.Format == ((LlvmFileInInst)other).Format,
            LlvmFileOutInst left => left.Mode == ((LlvmFileOutInst)other).Mode,
            _ => true
        };
    }

    // The byte count does not describe the native reference and initialization lanes.
    private static bool SameHeapAllocation(LlvmHeapAllocInst left, LlvmHeapAllocInst right, bool ignoreAlignment) =>
        left.LogicalLayout == right.LogicalLayout && left.LogicalType == right.LogicalType
        && left.LogicalAllocationSite == right.LogicalAllocationSite
        && (ignoreAlignment || left.Alignment == right.Alignment);

    private static bool SameGepState(LlvmGetElementPtrInstruction left, LlvmGetElementPtrInstruction right)
    {
        if (left.SourceElementType != right.SourceElementType) return false;
        var storage = left.GetTargetStorageType();
        return storage == right.GetTargetStorageType() && (storage is null || left.LogicalType == right.LogicalType);
    }

    private static bool SameLoadState(LlvmLoadInstruction left, LlvmLoadInstruction right, bool ignoreAlignment) =>
        left.IsVolatile == right.IsVolatile && left.IsElementwise == right.IsElementwise && (ignoreAlignment || left.Alignment == right.Alignment)
        && left.Ordering == right.Ordering && left.SyncScopeId == right.SyncScopeId;

    private static bool SameStoreState(LlvmStoreInstruction left, LlvmStoreInstruction right, bool ignoreAlignment) =>
        left.IsVolatile == right.IsVolatile && left.IsElementwise == right.IsElementwise && (ignoreAlignment || left.Alignment == right.Alignment)
        && left.Ordering == right.Ordering && left.SyncScopeId == right.SyncScopeId;

    private static bool SameCallState(LlvmCallInstruction left, LlvmCallInstruction right, bool intersectAttrs)
    {
        bool leftTail = left.TailCallKind is LlvmTailCallKind.Tail or LlvmTailCallKind.MustTail;
        bool rightTail = right.TailCallKind is LlvmTailCallKind.Tail or LlvmTailCallKind.MustTail;
        if (leftTail != rightTail || left.CallingConvention != right.CallingConvention
            || !ReferenceEquals(left.FunctionType, right.FunctionType)
            || left.OperandBundleCount != right.OperandBundleCount
            || !left.HasSameCallSiteAttributes(right, intersectAttrs)) return false;
        for (int index = 0; index < left.OperandBundleCount; ++index)
        {
            var a = left.GetOperandBundleAt(index);
            var b = right.GetOperandBundleAt(index);
            if (a.Tag != b.Tag || a.Begin != b.Begin || a.End != b.End) return false;
        }
        return true;
    }

    private static bool SameSwitchCases(LlvmSwitchInstruction left, LlvmSwitchInstruction right)
    {
        for (int index = 0; index < Math.Min(left.CaseCount, right.CaseCount); ++index)
            if (!ReferenceEquals(left.GetCaseValue(index), right.GetCaseValue(index))) return false;
        return true;
    }
}
