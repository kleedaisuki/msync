using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeRegisterAllocation
{
    internal static NativeProgram Run(NativeProgram program, NativeOptimizationProfile? profile,
        NativeAddressAnalysis.ProgramFacts? sharedFacts = null)
    {
        NativeAddressAnalysis.ProgramFacts facts = sharedFacts ?? new(program);
        NativeFunctionCode[] functions = new NativeFunctionCode[program.Functions.Count];
        for (int index = 0; index < functions.Length; index++)
        {
            profile?.Function(index);
            var started = profile?.Start(NativeOptimizationProfile.Pass.Slots, program.Functions[index]) ?? default;
            NativeFunctionCode original = program.Functions[index];
            functions[index] = Allocate(program, index, facts);
            profile?.Add(NativeOptimizationProfile.Pass.Slots, started, original, functions[index],
                original.Slots.Count != functions[index].Slots.Count || !original.Instructions.SequenceEqual(functions[index].Instructions));
        }
        return program with { Functions = functions };
    }

    private static NativeFunctionCode Allocate(NativeProgram program, int index, NativeAddressAnalysis.ProgramFacts sharedFacts)
    {
        NativeFunctionCode function = program.Functions[index];
        if (function.IsTemplate || function.Instructions.Count == 0 || ObservesFrame(program, function)) return function;
        for (int slot = 0; slot < function.Slots.Count; slot++)
            if (function.Slots[slot].Escapes) return function;
        HashSet<int> used = function.Instructions.SelectMany(NativeInstructionFacts.Reads)
            .Concat(function.Instructions.SelectMany(NativeInstructionFacts.Definitions)).ToHashSet();
        int prefix = ProtectedPrefix(program, function, index, used);
        used.RemoveWhere(slot => slot < prefix);
        NativeAddressAnalysis facts = sharedFacts.Function(index);
        Dictionary<int, (string? Layouts, string Physical)> contracts = used.ToDictionary(slot => slot, slot =>
        {
            NativeCodeType type = program.Types[function.Slots[slot].Type];
            if (type.Kind != Msync.Core.TypeKind.Scope && type.Pointee < 0) return (null, "");
            HashSet<int>? layouts = facts.Layouts(slot);
            return (layouts is null ? null : string.Join(',', layouts.Order()),
                string.Join(',', facts.PhysicalTypes(slot).Order()));
        });
        Dictionary<int, HashSet<int>> edges = used.ToDictionary(slot => slot, _ => new HashSet<int>());
        NativeLiveness live = new(program, index, new NativeControlFlow(function.Instructions));
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            foreach (int definition in NativeInstructionFacts.Definitions(instruction).Where(used.Contains))
                foreach (int other in live.After[pc].Where(used.Contains)) Edge(definition, other);
            int[] inputs = NativeInstructionFacts.Reads(instruction).Where(used.Contains).Distinct().ToArray();
            for (int first = 0; first < inputs.Length; first++)
                for (int second = first + 1; second < inputs.Length; second++) Edge(inputs[first], inputs[second]);
        }
        List<NativeSlot> slots = function.Slots.Take(prefix).ToList();
        Dictionary<int, int> colors = [];
        Dictionary<int, (string? Layouts, string Physical)> storageContracts = [];
        foreach (int slot in used.OrderByDescending(slot => edges[slot].Count).ThenBy(slot => slot))
        {
            HashSet<int> unavailable = edges[slot].Where(colors.ContainsKey).Select(other => colors[other]).ToHashSet();
            int color = Enumerable.Range(prefix, slots.Count - prefix)
                .FirstOrDefault(candidate => slots[candidate].Type == function.Slots[slot].Type
                    && storageContracts[candidate] == contracts[slot] && !unavailable.Contains(candidate), -1);
            if (color < 0)
            {
                color = slots.Count;
                slots.Add(function.Slots[slot] with { Name = null });
                // Portable pointer proofs union all producers of a register, even across disjoint lifetimes.
                // Sharing storage must preserve those proofs as well as the static value ABI.
                storageContracts.Add(color, contracts[slot]);
            }
            colors.Add(slot, color);
        }
        int Rename(int slot) => slot < prefix ? slot : colors[slot];
        NativeInstruction[] code = function.Instructions.Select(instruction =>
        {
            NativeInstruction result = NativeCodeRelocation.Registers(instruction, Rename);
            return result.Op is NativeOp.Copy or NativeOp.Move && result.Result == result.A
                ? NativeLocalOptimization.Nop(result) : result;
        }).ToArray();
        NativeStoragePlan? storage = function.Storage;
        if (storage is not null)
        {
            NativeSlotStorage[] flags = new NativeSlotStorage[slots.Count];
            for (int slot = 0; slot < prefix; slot++) flags[slot] = storage.Slots[slot];
            for (int slot = prefix; slot < flags.Length; slot++)
                flags[slot] = NativeSlotStorage.PrivateArgument | NativeSlotStorage.ReadOnlyParameter;
            foreach ((int source, int target) in colors)
            {
                NativeSlotStorage merged = flags[target] & storage.Slots[source];
                merged |= (flags[target] | storage.Slots[source]) & NativeSlotStorage.Escapes;
                flags[target] = merged;
            }
            storage = storage with { Slots = flags };
        }
        return function with { Slots = slots.ToArray(), Instructions = code, Storage = storage };

        void Edge(int first, int second)
        {
            if (first == second || function.Slots[first].Type != function.Slots[second].Type) return;
            edges[first].Add(second); edges[second].Add(first);
        }
    }

    private static int ProtectedPrefix(NativeProgram program, NativeFunctionCode function, int index, IReadOnlySet<int> used)
    {
        int prefix = function.CaptureCount + function.ParameterCount;
        for (int slot = prefix; slot < function.Slots.Count; slot++)
            if (function.Slots[slot].Escapes || function.Slots[slot].Role != NativeSlotRole.Temporary
                && (used.Contains(slot) || program.PreserveLocals && index == program.EntryPoint)) prefix = slot + 1;
        foreach (NativeInstruction instruction in function.Instructions)
            if (instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation) prefix = Math.Max(prefix, instruction.A + 1);
        if (index == program.EntryPoint)
            foreach (int root in program.SessionRoots ?? []) prefix = Math.Max(prefix, root + 1);
        return prefix;
    }

    private static bool ObservesFrame(NativeProgram program, NativeFunctionCode function) => function.Instructions.Any(instruction =>
        instruction.Op is NativeOp.IndexAddress or NativeOp.CopyMemory or NativeOp.ClearMemory
        || instruction.Op == NativeOp.Convert && program.Types[function.Slots[instruction.A].Type].Pointee >= 0);
}
