using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeCallWindowCandidate(int Pc, int Target, int Rank, int[] Coalesced);

internal static class NativeCallWindowPlanning
{
    internal static NativeCallWindowCandidate[]? Discover(NativeImage image, int index, NativeControlFlow flow,
        NativeBackendLiveness live, IReadOnlySet<int> exposed, CompilationTrace? trace)
    {
        NativeProgram program = image.Program;
        NativeFunctionCode caller = program.Functions[index];
        NativeStoragePlan storage = caller.Storage!;
        if (storage.Calls.Count == 0 || (program.PreserveLocals && index == program.EntryPoint)) return null;
        FrameAllocation frame = image.Frame(index);
        if (UsesWideFrameAddressing(caller)) return null;
        List<NativeCallWindowCandidate>? candidates = null;
        int considered = 0, shapeMisses = 0, liveMisses = 0, mixedSites = 0;
        foreach (NativeCallStorage site in storage.Calls)
        {
            int pc = site.Instruction;
            NativeInstruction call = caller.Instructions[pc];
            if (site.BorrowedCount != 0 || call.B < 0 || call.C >= 0
                || call.Inputs is not { Count: > 0 } inputs) continue;
            considered++;
            if (!MatchesParameters(frame, image.Frame(call.B), inputs)) { shapeMisses++; continue; }
            int[] coalesced = inputs.Where(slot => slot != call.A && slot != call.Result && !exposed.Contains(slot)
                && caller.Slots[slot] is { Role: NativeSlotRole.Temporary, Escapes: false }
                && !live.ContainsAfter(pc, slot) && inputs.Count(input => input == slot) == 1).ToArray();
            if (coalesced.Length == 0) { liveMisses++; continue; }
            if (coalesced.Length != inputs.Count) mixedSites++;
            int rank = (flow.Cyclic[pc] ? 65536 : 0) + (site.Snapshot ? inputs.Count : 0) + coalesced.Length;
            (candidates ??= []).Add(new(pc, call.B, rank, coalesced));
        }
        trace?.Metric("call_window.considered", considered);
        trace?.Metric("call_window.shape_misses", shapeMisses);
        trace?.Metric("call_window.live_misses", liveMisses);
        trace?.Metric("call_window.mixed_candidates", mixedSites);
        trace?.Metric("call_window.candidates", candidates?.Count ?? 0);
        return candidates?.ToArray();
    }

    internal static NativeProgram Plan(NativeProgram program, NativeBackendFunction[] backend,
        NativeCallWindowCandidate[]?[] candidates, CompilationTrace? trace)
    {
        NativeFunctionCode[]? updated = null;
        int eligible = 0, selected = 0, arguments = 0, coalesced = 0, captureTargets = 0;
        for (int index = 0; index < backend.Length; index++)
        {
            if (candidates[index] is not { } sites) continue;
            NativeCallWindowCandidate? winner = null;
            foreach (NativeCallWindowCandidate site in sites)
            {
                if (backend[index].Instructions[site.Pc].Handler != NativeBackendHandler.DirectCall
                    || !backend[site.Target].PureSynchronous) continue;
                eligible++;
                if (winner is null || site.Rank > winner.Value.Rank) winner = site;
            }
            if (winner is not { } chosen) continue;

            NativeFunctionCode caller = program.Functions[index];
            NativeStoragePlan storage = caller.Storage!;
            NativeInstructionStorage[] instructions = storage.Instructions.ToArray();
            NativeSlotStorage[] slots = storage.Slots.ToArray();
            instructions[chosen.Pc] |= NativeInstructionStorage.OwnArguments;
            int transferred = caller.Instructions[chosen.Pc].Inputs!.Count;
            // The callee observes initialized formals, even when the caller does not read the temporary.
            foreach (int slot in chosen.Coalesced)
                slots[slot] = (slots[slot] | NativeSlotStorage.WindowProducer) & ~NativeSlotStorage.UnobservedInitialization;
            (updated ??= program.Functions.ToArray())[index] = caller with { Storage = storage with { Instructions = instructions, Slots = slots } };
            selected++;
            arguments += transferred;
            coalesced += chosen.Coalesced.Length;
            bool captured = program.Functions[chosen.Target].CaptureCount != 0;
            if (captured) captureTargets++;
            trace?.Report(new("backend.call_windows.site", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string>
                {
                    ["function.name"] = caller.Name,
                    ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["instruction.pc"] = chosen.Pc.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["callee.name"] = program.Functions[chosen.Target].Name
                },
                Metrics = new Dictionary<string, ulong> { ["arguments"] = (ulong)transferred,
                    ["coalesced"] = (ulong)chosen.Coalesced.Length, ["captures"] = (ulong)program.Functions[chosen.Target].CaptureCount }
            });
        }
        trace?.Metric("sites.eligible", eligible);
        trace?.Metric("sites.selected", selected);
        trace?.Metric("arguments", arguments);
        trace?.Metric("arguments.coalesced", coalesced);
        trace?.Metric("sites.captured_target", captureTargets);
        return updated is null ? program : program with { Functions = updated };
    }

    private static bool MatchesParameters(FrameAllocation caller, FrameAllocation target, IReadOnlyList<int> inputs)
    {
        int captures = target.Code.CaptureCount;
        if (target.Code.ParameterCount != inputs.Count || target.Code.SsaBody is not null
            || captures != 0 && UsesWideFrameAddressing(target.Code))
            return false;
        for (int index = 0; index < inputs.Count; index++)
        {
            int slot = inputs[index];
            ref readonly NativeFrameSlot source = ref caller.Slots[slot];
            ref readonly NativeFrameSlot formal = ref target.Slots[captures + index];
            if (source.Binding >= 0 || formal.Binding >= 0 || source.Type.IsMachine || source.Type.IsNever
                || !source.Type.Equals(formal.Type)) return false;
        }
        return true;
    }

    private static bool UsesWideFrameAddressing(NativeFunctionCode code) => code.Instructions.Any(static op =>
        op.Op is NativeOp.IndexAddress or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory);
}
