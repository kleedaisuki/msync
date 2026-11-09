namespace Msync.Core;

internal static class LocalSlotCompaction
{
    internal static Module Compact(Module module) => module with
    {
        Functions = module.Functions.Select((function, index) => module.PreserveLocals && index == module.EntryPoint
            ? function : Optimize(function)).ToArray()
    };

    internal static FunctionCode Optimize(FunctionCode function)
    {
        var prefix = function.CaptureCount + function.ParameterCount;
        if (function.LocalCount == prefix) return function;
        var used = new bool[function.LocalCount];
        foreach (var instruction in function.Instructions)
            if (HasSlot(instruction.Op)) used[instruction.A] = true;
        var slots = new int[function.LocalCount];
        var count = prefix;
        for (var slot = 0; slot < prefix; slot++) slots[slot] = slot;
        for (var slot = prefix; slot < slots.Length; slot++)
            if (used[slot]) slots[slot] = count++;
        if (count == function.LocalCount) return function;
        return function with
        {
            LocalCount = count,
            Instructions = function.Instructions.Select(instruction => HasSlot(instruction.Op)
                ? instruction with { A = slots[instruction.A] } : instruction).ToArray()
        };
    }

    private static bool HasSlot(OpCode op) => op is OpCode.LoadLocal or OpCode.StoreLocal or OpCode.InitializeLocal
        or OpCode.LoadCell or OpCode.LoadStorage or OpCode.IteratorNext;
}
