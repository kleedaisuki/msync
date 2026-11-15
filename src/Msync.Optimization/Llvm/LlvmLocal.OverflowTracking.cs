// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Local.h OverflowTracking,
// Local.cpp::OverflowTracking::mergeFlags/applyFlags.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public sealed class LlvmOverflowTracking
{
    public bool HasNUW { get; set; } = true;
    public bool HasNSW { get; set; } = true;
    public bool IsDisjoint { get; set; } = true;
    public bool AllKnownNonNegative { get; set; } = true;
    public bool AllKnownNonZero { get; set; } = true;
#if DEBUG
    private LlvmOpcode? opcode;
#endif
    public void MergeFlags(LlvmInstruction instruction)
    {
#if DEBUG
        Debug.Assert(opcode is null || opcode == instruction.Opcode);
        opcode ??= instruction.Opcode;
#endif
        if (instruction is LlvmBinaryOperator binary && binary.Opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul or LlvmOpcode.Shl)
        { HasNUW &= binary.HasNoUnsignedWrap; HasNSW &= binary.HasNoSignedWrap; }
        if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } disjoint) IsDisjoint &= disjoint.IsDisjoint;
    }
    public void ApplyFlags(LlvmInstruction instruction)
    {
        instruction.DropPoisonGeneratingFlags();
        if (instruction is LlvmBinaryOperator binary)
        {
            if (binary.Opcode == LlvmOpcode.Add || binary.Opcode == LlvmOpcode.Mul && AllKnownNonZero)
            {
                if (HasNUW) binary.HasNoUnsignedWrap = true;
                if (HasNSW && (AllKnownNonNegative || HasNUW)) binary.HasNoSignedWrap = true;
            }
            if (binary.Opcode == LlvmOpcode.Or) binary.IsDisjoint = IsDisjoint;
        }
    }
}
