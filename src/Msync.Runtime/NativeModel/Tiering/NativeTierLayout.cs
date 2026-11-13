using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct FixedTierSlot(int Slot, int Scalar, int ByteOffset, int DataOffset, bool IsUnion, bool ScalarCapable);
internal readonly record struct FlushEntry(int Pc, int HitIndex, int TakenIndex, int Span, NativeOp Op, int TargetA, int TargetB, int TargetC);

internal sealed class NativeTierLayout(int[] scalars, int[] nativePcs, NativeRegisterRegion[]? regions = null, int[]? branches = null,
    FixedTierSlot[]? fixedSlots = null, int[]? fixedBySlot = null, FixedTierSlot[]? dynamicLocalSlots = null,
    int[]? dynamicExternalSlots = null, FlushEntry[]? flushEntries = null)
{
    private readonly int[]? fixedBySlot = fixedBySlot;
    internal NativeRegisterRegion[] Regions { get; } = regions ?? [];
    internal IReadOnlyDictionary<int, NativeRegisterRegion> RegionsByPc { get; } = (regions ?? []).ToDictionary(region => region.Start);
    internal int StackBytes => (Regions.Select(region => region.Spills * 8).DefaultIfEmpty().Max() + 15) & ~15;
    internal int[] Scalars { get; } = scalars;
    internal int[] NativePcs { get; } = nativePcs;
    internal int ScalarCount { get; } = scalars.Count(index => index >= 0);
    internal int[] RefreshSlots { get; } = Enumerable.Range(0, scalars.Length).Where(slot => scalars[slot] >= 0).ToArray();
    internal FixedTierSlot[] FixedSlots { get; } = fixedSlots ?? [];
    internal FixedTierSlot[] DynamicLocalSlots { get; } = dynamicLocalSlots ?? [];
    internal int[] DynamicExternalSlots { get; } = dynamicExternalSlots ?? Enumerable.Range(0, scalars.Length).Where(slot => scalars[slot] >= 0).ToArray();
    internal FlushEntry[] FlushEntries { get; } = flushEntries ?? [];
    internal int[] Hits { get; } = HitMap(nativePcs);
    internal IReadOnlyDictionary<int, int> TrueHits { get; } = (branches ?? [])
        .Select((pc, index) => (pc, index: nativePcs.Length + index)).ToDictionary(item => item.pc, item => item.index);
    internal nuint Bytes => checked((nuint)(56 + ScalarCount * 32 + (NativePcs.Length + TrueHits.Count) * 8));

    internal bool TryFixed(int slot, out FixedTierSlot entry)
    {
        if (fixedBySlot is not null && (uint)slot < (uint)fixedBySlot.Length && fixedBySlot[slot] >= 0)
        {
            entry = FixedSlots[fixedBySlot[slot]];
            return true;
        }
        entry = default;
        return false;
    }

    internal static NativeTierLayout Create(NativeExecutionFunction function, NativeFastOperation[] operations, NativeImage? image = null)
    {
        NativeRegisterRegion[] regions = NativeRegisterPlan.Create(function, operations);
        Dictionary<int, NativeRegisterRegion> starts = regions.ToDictionary(region => region.Start);
        HashSet<int> slots = [];
        List<int> pcs = [];
        List<int> branches = [];
        for (int pc = 0; pc < operations.Length; pc++)
        {
            if (starts.TryGetValue(pc, out NativeRegisterRegion? region))
            {
                pcs.Add(pc);
                foreach (int value in region.Inputs.Concat(region.Outputs)) slots.Add(region.Values[value].Slot);
                pc = region.End - 1;
                continue;
            }
            if (operations[pc] == NativeFastOperation.Bridge) continue;
            pcs.Add(pc);
            NativeExecutionInstruction instruction = function.Instructions[pc];
            if (operations[pc] is NativeFastOperation.Jump or NativeFastOperation.Nop) continue;
            if (operations[pc] is NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn)
            {
                slots.Add(instruction.A);
                continue;
            }
            if (operations[pc] == NativeFastOperation.Branch)
            {
                slots.Add(instruction.A);
                branches.Add(pc);
                continue;
            }
            slots.Add(instruction.Result);
            if (operations[pc] is not (NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant)) slots.Add(instruction.A);
            if (instruction.Op == NativeOp.Binary) slots.Add(instruction.B);
        }
        int[] map = Enumerable.Repeat(-1, function.Definition.Slots.Count).ToArray();
        int index = 0;
        foreach (int slot in slots.Order()) map[slot] = index++;
        NativeFrameSlot[]? frameSlots = image?.Frame(function.Index).Slots;
        List<FixedTierSlot> fixedSlots = [];
        int[] fixedBySlot = Enumerable.Repeat(-1, map.Length).ToArray();
        List<FixedTierSlot> dynamicLocalSlots = [];
        List<int> dynamicExternalSlots = [];
        for (int slot = 0; slot < map.Length; slot++)
        {
            int scalar = map[slot];
            if (scalar < 0) continue;
            if (frameSlots is not null)
            {
                ref readonly NativeFrameSlot frameSlot = ref frameSlots[slot];
                bool scalarCapable = frameSlot.Type.Accepts(NativeKind.Number) || frameSlot.Type.Accepts(NativeKind.Bool);
                FixedTierSlot entry = new(slot, scalar, frameSlot.Offset.Bytes,
                    frameSlot.Offset.Bytes + frameSlot.Type.Representation.PayloadOffset, frameSlot.Type.IsUnion, scalarCapable);
                if (frameSlot.Binding < 0)
                {
                    fixedBySlot[slot] = fixedSlots.Count;
                    fixedSlots.Add(entry);
                }
                else if (!frameSlot.Persistent && !frameSlot.Borrowable && (slot >= function.Definition.CaptureCount
                    || function.Definition.Signature.Captures[slot].Mode != NativeCaptureMode.Location))
                    dynamicLocalSlots.Add(entry);
                else dynamicExternalSlots.Add(slot);
            }
            else dynamicExternalSlots.Add(slot);
        }
        FixedTierSlot[] fixedArray = fixedSlots.ToArray();
        FixedTierSlot[] localArray = dynamicLocalSlots.ToArray();
        int[] externalArray = dynamicExternalSlots.ToArray();
        NativeTierLayout provisional = new(map, pcs.ToArray(), regions, branches.ToArray(),
            fixedArray, fixedBySlot, localArray, externalArray);
        FlushEntry[] flushEntries = new FlushEntry[provisional.NativePcs.Length];
        for (int i = 0; i < provisional.NativePcs.Length; i++)
        {
            int pc = provisional.NativePcs[i];
            NativeExecutionInstruction instr = function.Instructions[pc];
            int takenIndex = provisional.TrueHits.TryGetValue(pc, out int taken) ? taken : -1;
            int span = provisional.RegionsByPc.TryGetValue(pc, out NativeRegisterRegion? region) ? region.End - pc : instr.Span;
            flushEntries[i] = new(pc, provisional.Hits[pc], takenIndex, span, instr.Op, instr.A, instr.B, instr.C);
        }
        return new(map, provisional.NativePcs, regions, branches.ToArray(),
            fixedArray, fixedBySlot, localArray, externalArray, flushEntries);
    }

    internal static NativeTierLayout Identity(NativeExecutionFunction function) => new(
        Enumerable.Range(0, function.Definition.Slots.Count).ToArray(), Enumerable.Range(0, function.Metadata.Length).ToArray());

    private static int[] HitMap(int[] pcs)
    {
        int[] map = Enumerable.Repeat(-1, pcs.Length == 0 ? 0 : pcs[^1] + 1).ToArray();
        for (int index = 0; index < pcs.Length; index++) map[pcs[index]] = index;
        return map;
    }
}
