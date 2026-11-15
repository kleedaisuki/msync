using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeStoragePlanning
{
    internal static NativeProgram Run(NativeProgram program, CompilationTrace? trace,
        NativeAddressAnalysis.ProgramFacts? sharedFacts = null)
    {
        NativeAddressAnalysis.ProgramFacts facts = sharedFacts ?? new(program);
        NativeFunctionCode[] functions = program.Functions.ToArray();
        for (int index = 0; index < functions.Length; index++)
        {
            if (functions[index].Storage is not null) continue;
            NativeEscapeAnalysis escape = trace?.Detail is not { } detail ? new(program, index, facts)
                : detail.MeasureDetailed("compiler.storage.analysis", index,
                    value => new NativeEscapeAnalysis(program, value, facts),
                    attributes: new Dictionary<string, string>
                    {
                        ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["function.name"] = functions[index].Name
                    });
            NativeStoragePlan storage = Capture(program, index, escape, trace);
            functions[index] = functions[index] with { Storage = storage };
        }
        for (int index = 0; index < functions.Length; index++)
        {
            NativeFunctionCode function = functions[index];
            NativeCallStorage[] calls = function.Instructions.Select((instruction, pc) => (instruction, pc))
                .Where(pair => pair.instruction.Op == NativeOp.Call)
                .Select(pair => Call(program, functions, function, pair.instruction, pair.pc)).ToArray();
            NativeSlotStorage[] slots = InitializationUse(function, calls);
            functions[index] = function with { Storage = function.Storage! with { Slots = slots, Calls = calls } };
            if (trace?.Detail is { } detail)
                detail.Report(new("compiler.storage.calls", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true)
                {
                    Attributes = new Dictionary<string, string>
                    {
                        ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["function.name"] = function.Name
                    },
                    Metrics = new Dictionary<string, ulong>
                    {
                        ["calls"] = (ulong)calls.Length,
                        ["snapshots"] = (ulong)calls.Count(call => call.Snapshot),
                        ["borrowed_arguments"] = (ulong)calls.Sum(call => call.BorrowedCount),
                        ["unobserved_initialization_slots"] = (ulong)slots.Count(slot => slot.HasFlag(NativeSlotStorage.UnobservedInitialization))
                    }
                });
        }
        return program with { Functions = functions };
    }

    private static NativeSlotStorage[] InitializationUse(NativeFunctionCode function, IReadOnlyList<NativeCallStorage> calls)
    {
        NativeSlotStorage[] slots = function.Storage!.Slots.Select(slot => slot.HasFlag(NativeSlotStorage.PrivateArgument)
            ? slot | NativeSlotStorage.UnobservedInitialization : slot & ~NativeSlotStorage.UnobservedInitialization).ToArray();
        foreach (NativeCallStorage call in calls)
        {
            IReadOnlyList<int> inputs = function.Instructions[call.Instruction].Inputs!;
            for (int argument = 0; argument < call.Borrowed.Count; argument++)
                if (call.Borrowed[argument]) slots[inputs[argument]] &= ~NativeSlotStorage.UnobservedInitialization;
        }
        return slots;
    }

    internal static NativeStoragePlan Capture(NativeProgram program, int index, NativeEscapeAnalysis escape,
        CompilationTrace? trace) => trace?.Detail is not { } detail ? Plan(program, index, escape, null)
        : detail.MeasureDetailed("compiler.storage.function", index,
            value => Plan(program, value, escape, detail),
            attributes: new Dictionary<string, string>
            {
                ["function.id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["function.name"] = program.Functions[index].Name
            });

    private static NativeCallStorage Call(NativeProgram program, NativeFunctionCode[] functions,
        NativeFunctionCode caller, NativeInstruction call, int pc)
    {
        IReadOnlyList<int> inputs = call.Inputs ?? [];
        bool snapshot = inputs.Any(slot => !caller.Storage!.Slots[slot].HasFlag(NativeSlotStorage.PrivateArgument));
        bool[] borrowed = new bool[inputs.Count];
        if (!snapshot && call.B >= 0 && call.C < 0)
        {
            NativeFunctionCode callee = functions[call.B];
            for (int index = 0; index < inputs.Count; index++)
                borrowed[index] = callee.Storage!.Slots[callee.CaptureCount + index].HasFlag(NativeSlotStorage.ReadOnlyParameter)
                    && StorageKind(program.Types[caller.Slots[inputs[index]].Type])
                        == StorageKind(program.Types[callee.Slots[callee.CaptureCount + index].Type]);
        }
        return new(pc, snapshot, borrowed);
    }

    // Borrowing requires the physical carrier ABI, not equal logical layout domains.
    private static TypeKind StorageKind(NativeCodeType type) => type.Pointee >= 0 ? TypeKind.Scope : type.Kind;

    private static NativeStoragePlan Plan(NativeProgram program, int index,
        NativeEscapeAnalysis escape, CompilationTrace? trace)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeAddressAnalysis addresses = escape.Addresses;
        bool[] parameters = NativeCallFacts.ReadOnlyParameters(function, addresses);
        bool[] arguments = NativeCallFacts.PrivateSlots(program, index, addresses);
        NativeSlotStorage[] slots = new NativeSlotStorage[function.Slots.Count];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (escape.EscapingLocations.Contains(slot) || function.Slots[slot].Escapes) slots[slot] |= NativeSlotStorage.Escapes;
            if (parameters[slot]) slots[slot] |= NativeSlotStorage.ReadOnlyParameter;
            if (arguments[slot]) slots[slot] |= NativeSlotStorage.PrivateArgument;
        }
        NativeInstructionStorage[] instructions = new NativeInstructionStorage[function.Instructions.Count];
        for (int pc = 0; pc < instructions.Length; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            bool aliases = instruction.Op switch
            {
                NativeOp.EnvironmentAddress => addresses.At(pc, instruction.Result).MayAliasCurrentFrame,
                NativeOp.BindLocation => addresses.At(pc, instruction.B).MayAliasCurrentFrame,
                NativeOp.ProjectAddress => addresses.At(pc, instruction.A).MayAliasCurrentFrameAllocation,
                NativeOp.AddressOffset or NativeOp.IndexAddress or NativeOp.ClearMemory or NativeOp.RangeFromMemory
                    => addresses.At(pc, instruction.A).MayAliasCurrentFrame,
                NativeOp.CopyMemory => addresses.At(pc, instruction.A).MayAliasCurrentFrame || addresses.At(pc, instruction.B).MayAliasCurrentFrame,
                _ => false
            };
            if (aliases) instructions[pc] |= NativeInstructionStorage.FrameAlias;
            if (instruction.Op == NativeOp.Allocate && instruction.C < 0
                && !escape.EscapingAllocations.Contains(pc) && !addresses.ControlFlow.Cyclic[pc])
                instructions[pc] |= NativeInstructionStorage.FrameAllocation;
            if (instruction.Op == NativeOp.Narrow && program.Types[instruction.Type].Layouts is { } admitted
                && addresses.ProvenLayoutsAt(pc, instruction.A) is { Count: > 0 } proven && proven.All(admitted.Contains))
                instructions[pc] |= NativeInstructionStorage.NarrowLayout;
        }
        trace?.Metric("storage.slots", slots.Length);
        trace?.Metric("storage.escaping_slots", slots.Count(slot => slot.HasFlag(NativeSlotStorage.Escapes)));
        trace?.Metric("storage.borrowable_parameters", parameters.Count(value => value));
        trace?.Metric("storage.private_arguments", arguments.Count(value => value));
        trace?.Metric("storage.frame_alias_sites", instructions.Count(value => value.HasFlag(NativeInstructionStorage.FrameAlias)));
        trace?.Metric("storage.narrow_layout_sites", instructions.Count(value => value.HasFlag(NativeInstructionStorage.NarrowLayout)));
        trace?.Metric("storage.frame_allocations", instructions.Count(value => value.HasFlag(NativeInstructionStorage.FrameAllocation)));
        return new(slots, instructions);
    }
}
