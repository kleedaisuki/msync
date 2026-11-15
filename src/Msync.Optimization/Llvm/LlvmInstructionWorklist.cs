// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Transforms/Utils/InstructionWorklist.h.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmInstructionWorklist(Dictionary<string, ulong>? metrics = null)
{
    private readonly List<LlvmInstruction?> instructions = [];
    private readonly Dictionary<LlvmInstruction, int> indices = [];
    private readonly List<LlvmInstruction> deferred = [];
    private readonly HashSet<LlvmInstruction> deferredSet = [];
    public bool IsEmpty => instructions.Count == 0 && deferred.Count == 0;

    public void Add(LlvmInstruction instruction)
    {
        if (!deferredSet.Add(instruction)) { Count("worklist.add.duplicate"); return; }
        deferred.Add(instruction);
        Count("worklist.deferred.added");
    }
    public void AddValue(LlvmValue value) { if (value is LlvmInstruction instruction) Add(instruction); }
    public void Push(LlvmInstruction instruction)
    {
        Debug.Assert(instruction.Parent is not null);
        if (!indices.TryAdd(instruction, instructions.Count)) { Count("worklist.push.duplicate"); return; }
        instructions.Add(instruction);
        Count("worklist.pushed");
    }
    public void PushValue(LlvmValue value) { if (value is LlvmInstruction instruction) Push(instruction); }
    public LlvmInstruction? PopDeferred()
    {
        if (deferred.Count == 0) return null;
        LlvmInstruction instruction = deferred[^1];
        deferred.RemoveAt(deferred.Count - 1);
        deferredSet.Remove(instruction);
        Count("worklist.deferred.popped");
        return instruction;
    }
    public void Reserve(int size)
    {
        instructions.EnsureCapacity(size + 16);
        indices.EnsureCapacity(size);
    }
    public void Remove(LlvmInstruction instruction)
    {
        if (indices.Remove(instruction, out int index)) instructions[index] = null;
        if (deferredSet.Remove(instruction)) deferred.Remove(instruction);
        Count("worklist.removed");
    }
    public LlvmInstruction? RemoveOne()
    {
        if (instructions.Count == 0) return null;
        LlvmInstruction? instruction = instructions[^1];
        instructions.RemoveAt(instructions.Count - 1);
        if (instruction is not null) indices.Remove(instruction);
        Count(instruction is null ? "worklist.tombstones.popped" : "worklist.popped");
        return instruction;
    }
    public void PushUsersToWorkList(LlvmInstruction instruction)
    {
        foreach (LlvmUser user in instruction.Users) Push((LlvmInstruction)user);
    }
    public void HandleUseCountDecrement(LlvmValue value)
    {
        if (value is not LlvmInstruction instruction) return;
        Add(instruction);
        if (instruction.HasOneUse) Add((LlvmInstruction)instruction.FirstUse!.User);
    }
    public void Zap()
    {
        Debug.Assert(indices.Count == 0 && deferred.Count == 0);
        indices.Clear();
    }
    private void Count(string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
