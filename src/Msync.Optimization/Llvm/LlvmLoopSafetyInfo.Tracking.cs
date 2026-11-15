// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/InstructionPrecedenceTracking.h/.cpp (release query/update paths).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public abstract class LlvmInstructionPrecedenceTracking
{
    private readonly Dictionary<LlvmBasicBlock, LlvmInstruction?> firstSpecial = new(ReferenceEqualityComparer.Instance);
    protected readonly Dictionary<string, ulong>? Metrics;
    protected LlvmInstructionPrecedenceTracking(Dictionary<string, ulong>? metrics = null) => Metrics = metrics;
    protected abstract bool IsSpecialInstruction(LlvmInstruction instruction);

    protected LlvmInstruction? GetFirstSpecialInstruction(LlvmBasicBlock block)
    {
        Count("loop_execution.precedence.queries");
        if (firstSpecial.TryGetValue(block, out var first))
        { Count("loop_execution.precedence.cache_hit"); return first; }
        Count("loop_execution.precedence.cache_miss");
        foreach (var instruction in block.Instructions)
        {
            Count("loop_execution.precedence.instructions");
            if (!IsSpecialInstruction(instruction)) continue;
            first = instruction;
            break;
        }
        firstSpecial.Add(block, first);
        return first;
    }

    protected bool HasSpecialInstructions(LlvmBasicBlock block) => GetFirstSpecialInstruction(block) is not null;
    protected bool IsPrecededBySpecialInstruction(LlvmInstruction instruction) =>
        GetFirstSpecialInstruction(instruction.Parent!) is { } first && first.ComesBefore(instruction);

    public void InsertInstructionTo(LlvmInstruction instruction, LlvmBasicBlock block)
    {
        if (!IsSpecialInstruction(instruction)) return;
        firstSpecial.Remove(block);
        Count("loop_execution.precedence.insert_invalidation");
    }

    public void RemoveInstruction(LlvmInstruction instruction)
    {
        Debug.Assert(instruction.Parent is not null);
        if (!firstSpecial.TryGetValue(instruction.Parent!, out var first) || first != instruction) return;
        firstSpecial.Remove(instruction.Parent!);
        Count("loop_execution.precedence.remove_invalidation");
    }

    public void RemoveUsersOf(LlvmInstruction instruction)
    {
        foreach (var user in instruction.Users)
            if (user is LlvmInstruction use) RemoveInstruction(use);
    }

    public void Clear() => firstSpecial.Clear();
    protected void Count(string name)
    { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1; }
}

public sealed class LlvmImplicitControlFlowTracking : LlvmInstructionPrecedenceTracking
{
    public LlvmImplicitControlFlowTracking(Dictionary<string, ulong>? metrics = null) : base(metrics) { }
    public LlvmInstruction? GetFirstICFI(LlvmBasicBlock block) => GetFirstSpecialInstruction(block);
    public bool HasICF(LlvmBasicBlock block) => HasSpecialInstructions(block);
    public bool IsDominatedByICFIFromSameBlock(LlvmInstruction instruction) => IsPrecededBySpecialInstruction(instruction);
    protected override bool IsSpecialInstruction(LlvmInstruction instruction) =>
        !LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(instruction, Metrics);
}

public sealed class LlvmMemoryWriteTracking : LlvmInstructionPrecedenceTracking
{
    public LlvmMemoryWriteTracking(Dictionary<string, ulong>? metrics = null) : base(metrics) { }
    public LlvmInstruction? GetFirstMemoryWrite(LlvmBasicBlock block) => GetFirstSpecialInstruction(block);
    public bool MayWriteToMemory(LlvmBasicBlock block) => HasSpecialInstructions(block);
    public bool IsDominatedByMemoryWriteFromSameBlock(LlvmInstruction instruction) => IsPrecededBySpecialInstruction(instruction);
    protected override bool IsSpecialInstruction(LlvmInstruction instruction) => instruction.MayWriteToMemory();
}
