// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/BasicBlock.h order-valid bit/invalidation; lib/IR/BasicBlock.cpp
// renumberInstructions/validateInstrOrdering; SymbolTableListTraitsImpl.h list insertion.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmBasicBlock
{
    private const byte InstructionOrderValidBit = 1 << 1;
    public bool IsInstrOrderValid => (SubclassOptionalData & InstructionOrderValidBit) != 0;

    public void InvalidateOrders()
    {
        ValidateInstructionOrdering();
        SubclassOptionalData = (byte)(SubclassOptionalData & ~InstructionOrderValidBit);
    }

    public void RenumberInstructions(CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) { RenumberInstructionsCore(); return; }
        trace.Run("compiler.llvm-ir.instruction-order.renumber", (Block: this, Trace: trace), static state =>
        {
            state.Trace.Metric("instructions.renumbered", state.Block.RenumberInstructionsCore());
            state.Trace.Metric("instruction_order.renumberings", 1);
        }, new Dictionary<string, string>
        {
            ["function.name"] = Parent?.Name ?? "", ["block.name"] = Name,
            ["block.number"] = Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    private uint RenumberInstructionsCore()
    {
        uint order = 0;
        for (LlvmInstruction? instruction = FirstInstruction; instruction is not null; instruction = instruction.Next)
            instruction.Order = order++;
        SubclassOptionalData |= InstructionOrderValidBit;
        return order;
    }

    [Conditional("DEBUG")]
    public void ValidateInstructionOrdering()
    {
        if (!IsInstrOrderValid) return;
        LlvmInstruction? previous = null;
        for (LlvmInstruction? instruction = FirstInstruction; instruction is not null; instruction = instruction.Next)
        {
            Debug.Assert(previous is null || previous.ComesBefore(instruction));
            previous = instruction;
        }
    }
}
