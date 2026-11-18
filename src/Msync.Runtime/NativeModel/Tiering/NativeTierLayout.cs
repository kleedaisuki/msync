using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct FixedTierSlot(int Slot, int Scalar, int ByteOffset, int DataOffset, bool IsUnion, bool ScalarCapable);
internal enum NativeCallProducer : byte { None, Field, FieldWithArgument, Compose }
internal readonly record struct FlushEntry(int Pc, int HitIndex, int TakenIndex, int Span, NativeOp Op, int TargetA, int TargetB, int TargetC,
    bool MachineCall, bool MachineClosureCall, NativeCallProducer MachineSource, bool GuestAccess, bool MachinePointerCall,
    bool LayoutBranch, bool GuestCopy, bool GuestView, bool GuestReferenceLoad, bool GuestReferenceStore,
    bool MachineAddressResult, bool MachineArgumentWindow, NativeFastOperation Materialization);

internal sealed partial class NativeTierLayout(int[] scalars, int[] nativePcs, NativeRegisterRegion[]? regions = null, int[]? branches = null,
    FixedTierSlot[]? fixedSlots = null, int[]? fixedBySlot = null, FixedTierSlot[]? dynamicLocalSlots = null,
    int[]? dynamicExternalSlots = null, FlushEntry[]? flushEntries = null, bool[]? machineClosures = null, bool[]? machineBases = null,
    bool guestAccess = false, bool[]? borrowedGuestBases = null, HashSet<int>? layoutBranches = null,
    IReadOnlyDictionary<int, NativeGuestView>? guestViews = null)
{
    internal const int RawOffset = 40, InitializationOffset = 48;
    internal const int GuestEdgesOffset = 64;
    internal const int ChildContextOffset = 72, ChildEntryOffset = 80;
    internal const int FixedResultBitsOffset = 88;
    internal const int StackCursorOffset = 96;
    internal const int FixedReturnTopOffset = 104, FixedReturnStateOffset = 112;
    internal const int FixedCallStartOffset = 116;
    internal const int FixedPendingOffset = 128, FixedCallableInitOffset = 136;
    internal const int FixedCallableEdgesOffset = 144, FixedEnvironmentOffset = 152;
    internal const int FixedRepeatPcOffset = 160, FixedRepeatsOffset = 164, FixedRepeatFuelOffset = 168;
    internal const int HeaderBytes = 176;
    internal bool GuestAccess { get; } = guestAccess;
    internal bool CanRunFixedRaw => DynamicLocalSlots.Length == 0 && DynamicExternalSlots.Length == 0
        && MachineClosures is null && MachineBases is null;
    internal bool[]? BorrowedGuestBases { get; } = borrowedGuestBases;
    internal HashSet<int>? LayoutBranches { get; } = layoutBranches;
    internal IReadOnlyDictionary<int, NativeGuestView>? GuestViews { get; } = guestViews;
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
    internal bool[]? MachineClosures { get; } = machineClosures;
    internal bool[]? MachineBases { get; } = machineBases;
    internal int[] Hits { get; } = HitMap(nativePcs);
    internal IReadOnlyDictionary<int, int> TrueHits { get; } = (branches ?? [])
        .Select((pc, index) => (pc, index: nativePcs.Length + index)).ToDictionary(item => item.pc, item => item.index);
    internal nuint Bytes => checked((nuint)(HeaderBytes + ScalarCount * 32 + (NativePcs.Length + TrueHits.Count) * 8));

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

    internal static NativeTierLayout Create(NativeExecutionFunction function, NativeFastOperation[] operations, NativeImage? image = null,
        NativeRegisterPlanMetrics? registerMetrics = null, IReadOnlyDictionary<int, NativeMachineCall>? calls = null,
        IReadOnlyDictionary<int, NativeGuestView>? views = null)
    {
        NativeRegisterRegion[] regions = NativeRegisterPlan.Create(function, operations, registerMetrics);
        Dictionary<int, NativeMachineCallableSource>? sources = calls?.Values.Where(call => call.Source.HasValue)
            .ToDictionary(call => call.Source!.Value.Pc, call => call.Source!.Value);
        Dictionary<int, NativeRegisterRegion> starts = regions.ToDictionary(region => region.Start);
        HashSet<int> slots = [];
        List<int> pcs = [];
        List<int> branches = [];
        bool hasGuestAccess = calls?.Values.Any(call => call.Leaf.GuestReferences != 0) == true;
        bool[]? borrowedGuestBases = null;
        NativeFrameSlot[]? frameSlots = image?.Frame(function.Index).Slots;
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
            if (operations[pc].IsValueMaterialization())
            {
                hasGuestAccess |= operations[pc] != NativeFastOperation.Integer64Constant;
                slots.Add(instruction.Result);
                if (operations[pc] == NativeFastOperation.GuestComposeFunction)
                    foreach (int part in function.Metadata[pc].Inputs!) slots.Add(part);
                continue;
            }
            if (operations[pc] == NativeFastOperation.AddressReturn)
            {
                hasGuestAccess = true;
                continue;
            }
            if (operations[pc].IsGuestAccess())
            {
                hasGuestAccess = true;
                if (operations[pc] is NativeFastOperation.GuestCopy or NativeFastOperation.GuestView
                    or NativeFastOperation.GuestReferenceLoad or NativeFastOperation.GuestReferenceStore)
                    continue;
                if (frameSlots?[instruction.A].Binding >= 0)
                {
                    (borrowedGuestBases ??= new bool[function.Definition.Slots.Count])[instruction.A] = true;
                    slots.Add(instruction.A);
                }
                slots.Add(operations[pc] == NativeFastOperation.GuestStore ? instruction.B : instruction.Result);
                continue;
            }
            if (operations[pc] == NativeFastOperation.MachineFusedCall)
            {
                NativeMachineCallableSource source = sources![pc];
                slots.Add(source.Base);
                if (source.InlineArgument >= 0)
                {
                    NativeExecutionInstruction argument = function.Instructions[source.InlineArgument];
                    slots.Add(argument.A);
                    slots.Add(argument.B);
                    slots.Add(argument.Result);
                }
                continue;
            }
            if (operations[pc] == NativeFastOperation.MachineCall)
            {
                slots.Add(instruction.A);
                slots.Add(instruction.Result);
                foreach (int input in function.Metadata[pc].Inputs ?? []) slots.Add(input);
                continue;
            }
            if (operations[pc] is NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn or NativeFastOperation.NullReturn)
            {
                if (instruction.A >= 0) slots.Add(instruction.A);
                continue;
            }
            if (operations[pc] == NativeFastOperation.Branch)
            {
                slots.Add(instruction.A);
                branches.Add(pc);
                continue;
            }
            if (operations[pc] == NativeFastOperation.SequenceNext)
            {
                slots.Add(instruction.Result);
                branches.Add(pc);
                continue;
            }
            slots.Add(instruction.Result);
            if (operations[pc] is not (NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant or NativeFastOperation.NullConstant))
                slots.Add(operations[pc].FirstOperand(function, pc));
            if (operations[pc].IsScalarSelect())
            {
                slots.Add(instruction.B);
                slots.Add(instruction.C);
            }
            else if (operations[pc].TakesSecondOperand()) slots.Add(instruction.B);
        }
        int[] map = Enumerable.Repeat(-1, function.Definition.Slots.Count).ToArray();
        int index = 0;
        foreach (int slot in slots.Order())
        {
            if ((uint)slot >= (uint)map.Length)
                throw new InvalidOperationException($"Invalid tier slot {slot} of {map.Length} in '{function.Definition.Name}'.");
            map[slot] = index++;
        }
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
        HashSet<int>? layoutBranches = FindLayoutBranches(function, operations);
        FlushEntry[] flushEntries = new FlushEntry[provisional.NativePcs.Length];
        for (int i = 0; i < provisional.NativePcs.Length; i++)
        {
            int pc = provisional.NativePcs[i];
            NativeExecutionInstruction instr = function.Instructions[pc];
            int takenIndex = provisional.TrueHits.TryGetValue(pc, out int taken) ? taken : -1;
            int span = provisional.RegionsByPc.TryGetValue(pc, out NativeRegisterRegion? region) ? region.End - pc : instr.Span;
            NativeMachineCall? linkedCall = calls?.GetValueOrDefault(pc);
            NativeMachineLeaf? target = linkedCall?.Leaf;
            flushEntries[i] = new(pc, provisional.Hits[pc], takenIndex, span, instr.Op, instr.A, instr.B, instr.C,
                target is not null, target?.Captures.Length > 0,
                operations[pc] != NativeFastOperation.MachineFusedCall ? NativeCallProducer.None
                    : sources![pc].InlineArgument >= 0 ? NativeCallProducer.FieldWithArgument
                    : sources[pc].Field.HasValue ? NativeCallProducer.Field : NativeCallProducer.Compose,
                operations[pc].IsGuestAccess(), target?.GuestReferences > 0, layoutBranches?.Contains(pc) == true,
                operations[pc] == NativeFastOperation.GuestCopy, operations[pc] == NativeFastOperation.GuestView,
                operations[pc] == NativeFastOperation.GuestReferenceLoad,
                operations[pc] == NativeFastOperation.GuestReferenceStore, target?.ResultKind == NativeKind.Address,
                linkedCall?.ArgumentWindow.HasValue == true, operations[pc].IsValueMaterialization() ? operations[pc] : default);
        }
        bool[]? closures = null, bases = null;
        if (calls is not null)
            foreach ((int pc, NativeMachineCall call) in calls)
            {
                if (call.Leaf.Captures.Length != 0)
                    (closures ??= new bool[map.Length])[function.Instructions[pc].A] = true;
                if (call.Source is { } source) (bases ??= new bool[map.Length])[source.Base] = true;
            }
        return new(map, provisional.NativePcs, regions, branches.ToArray(),
            fixedArray, fixedBySlot, localArray, externalArray, flushEntries, closures, bases, hasGuestAccess, borrowedGuestBases,
            layoutBranches, views);
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
