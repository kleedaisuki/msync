using System.Globalization;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeExecutionFunction
{
    private void FuseAddresses(NativeExecutionInstruction[] code, NativeControlFlow flow,
        HashSet<int> exposed, HashSet<int> rebound, CompilationTrace? trace)
    {
        if (!Metadata.Any(instruction => IsAddressProducer(instruction.Op))) return;
        if (trace is null)
        {
            FuseAddressOperands(code, flow, exposed, rebound, null);
            return;
        }
        trace.Run("backend.address_fusion", (Owner: this, code, flow, exposed, rebound, trace),
            static state => state.Owner.FuseAddressOperands(state.code, state.flow, state.exposed, state.rebound, state.trace),
            new Dictionary<string, string>
            {
                ["function.id"] = Index.ToString(CultureInfo.InvariantCulture),
                ["function.name"] = Definition.Name
            });
    }

    private static bool IsAddressProducer(NativeOp op) => op is NativeOp.FrameAddress or NativeOp.AddressOffset or NativeOp.ProjectAddress;
    private static bool IsReadyAddressProducer(NativeOp op) =>
        IsAddressProducer(op) || op is NativeOp.Allocate or NativeOp.StaticAddress or NativeOp.EnvironmentAddress or NativeOp.IndexAddress;

    private void FuseAddressOperands(NativeExecutionInstruction[] code, NativeControlFlow flow,
        HashSet<int> exposed, HashSet<int> rebound, CompilationTrace? trace)
    {
        int[] definitions = new int[Definition.Slots.Count];
        Array.Fill(definitions, -1);
        for (int pc = 0; pc < Metadata.Length; pc++)
            foreach (int slot in NativeInstructionFacts.Definitions(Metadata[pc]))
                definitions[slot] = definitions[slot] == -1 ? pc : -2;
        int[] blockAt = new int[Metadata.Length];
        foreach (NativeControlFlow.Block block in flow.Blocks)
            Array.Fill(blockAt, block.Start, block.Start, block.End - block.Start);

        bool Before(int definition, int use) => definition < use &&
            (blockAt[definition] == 0 || blockAt[definition] == blockAt[use]);
        bool UnobservableTemporary(int slot) => Definition.Slots[slot].Role == NativeSlotRole.Temporary
            && slot >= Definition.CaptureCount + Definition.ParameterCount
            && !Escapes(slot) && !exposed.Contains(slot) && FixedCell(slot, rebound);
        bool StableBase(int pc, int slot) => definitions[slot] is >= 0 and var definition
            && Before(definition, pc) && UnobservableTemporary(slot)
            && IsReadyAddressProducer(Metadata[definition].Op);

        bool[] candidate = new bool[Definition.Slots.Count];
        int producerCount = 0, found = 0, invalidUse = 0, nonDominating = 0;
        for (int pc = 0; pc < Metadata.Length; pc++)
        {
            NativeInstruction address = Metadata[pc];
            if (!IsAddressProducer(address.Op)) continue;
            producerCount++;
            int slot = address.Result;
            if (definitions[slot] != pc || !UnobservableTemporary(slot)) continue;
            bool stable = address.Op switch
            {
                NativeOp.FrameAddress => !rebound.Contains(address.A),
                NativeOp.AddressOffset => StableBase(pc, address.A),
                NativeOp.ProjectAddress => Operands[pc].Projection is { Length: 1 } && StableBase(pc, address.A),
                _ => false
            };
            if (!stable) continue;
            candidate[slot] = true;
            found++;
        }
        if (found == 0)
        {
            trace?.Metric("address.producers", producerCount);
            trace?.Metric("address.candidates", 0);
            return;
        }
        for (int pc = 0; pc < Metadata.Length; pc++)
        {
            NativeInstruction use = Metadata[pc];
            foreach (int slot in NativeInstructionFacts.Reads(use))
            {
                if (!candidate[slot]) continue;
                bool dominated = Before(definitions[slot], pc);
                bool directAccess = use.A == slot && (use.Op == NativeOp.Load || use.Op == NativeOp.Store && use.B != slot);
                if (dominated && directAccess) continue;
                candidate[slot] = false;
                if (dominated) invalidUse++; else nonDominating++;
            }
        }

        int frameAddresses = 0, offsetAddresses = 0, projectAddresses = 0;
        int frameLoads = 0, frameStores = 0, offsetLoads = 0, offsetStores = 0, projectLoads = 0, projectStores = 0;
        for (int pc = 0; pc < Metadata.Length; pc++)
        {
            NativeInstruction original = Metadata[pc];
            if (IsAddressProducer(original.Op) && candidate[original.Result])
            {
                if (original.Op == NativeOp.FrameAddress) frameAddresses++;
                else if (original.Op == NativeOp.AddressOffset) offsetAddresses++;
                else projectAddresses++;
                code[pc] = new(new NativeInstruction(NativeOp.Nop));
                continue;
            }
            if (original.Op is not (NativeOp.Load or NativeOp.Store) || !candidate[original.A]) continue;
            int producer = definitions[original.A];
            NativeInstruction address = Metadata[producer];
            bool load = original.Op == NativeOp.Load;
            if (address.Op == NativeOp.FrameAddress)
            {
                if (load)
                {
                    code[pc] = new(original with { A = address.A }, handler: NativeInterpreter.FrameLoadHandler);
                    frameLoads++;
                }
                else
                {
                    code[pc] = new(original with { Result = address.A, A = original.B }, handler: NativeInterpreter.FrameStoreHandler);
                    frameStores++;
                }
                continue;
            }
            // The original address producer retains the prelinked field or projection;
            // the access itself keeps its source PC for diagnostics and storage facts.
            Operands[pc] = Operands[producer];
            operandSources[pc] = producer;
            if (address.Op == NativeOp.AddressOffset)
            {
                code[pc] = new(original with { A = address.A }, handler: load ? NativeInterpreter.OffsetLoadHandler : NativeInterpreter.OffsetStoreHandler);
                if (load) offsetLoads++; else offsetStores++;
            }
            else
            {
                code[pc] = new(original with { A = address.A }, handler: load ? NativeInterpreter.ProjectLoadHandler : NativeInterpreter.ProjectStoreHandler);
                if (load) projectLoads++; else projectStores++;
            }
        }
        trace?.Metric("address.producers", producerCount);
        trace?.Metric("address.candidates", found);
        trace?.Metric("rejected.other_use", invalidUse);
        trace?.Metric("rejected.control_flow", nonDominating);
        trace?.Metric("eliminated.frame_address", frameAddresses);
        trace?.Metric("eliminated.offset_address", offsetAddresses);
        trace?.Metric("eliminated.project_address", projectAddresses);
        trace?.Metric("handlers.frame_load", frameLoads);
        trace?.Metric("handlers.frame_store", frameStores);
        trace?.Metric("handlers.offset_load", offsetLoads);
        trace?.Metric("handlers.offset_store", offsetStores);
        trace?.Metric("handlers.project_load", projectLoads);
        trace?.Metric("handlers.project_store", projectStores);
    }

    private void GroupNoOps(NativeExecutionInstruction[] code, NativeControlFlow flow, CompilationTrace? trace)
    {
        int saved = 0, absorbed = 0;
        foreach (NativeControlFlow.Block block in flow.Blocks)
        {
            for (int pc = block.Start; pc < block.End; pc += code[pc].Span)
            {
                if (code[pc].Handler != NativeInterpreter.NopHandler) continue;
                int next = pc + code[pc].Span;
                while (next < block.End && code[next].Handler == NativeInterpreter.NopHandler)
                {
                    next += code[next].Span;
                    saved++;
                }
                if (next < block.End && CanAbsorbNoOps(code[next]))
                {
                    // These handlers use their encoded operands, not their
                    // source PC. The span ends on the real operation for errors.
                    code[pc] = code[next].WithSpan(next - pc + code[next].Span);
                    Operands[pc] = Operands[next];
                    operandSources[pc] = operandSources[next];
                    absorbed++;
                }
                else if (next - pc > code[pc].Span) code[pc] = code[pc].WithSpan(next - pc);
            }
        }
        trace?.Metric("dispatch.nops_coalesced", saved);
        trace?.Metric("dispatch.nops_absorbed", absorbed);
    }

    private static bool CanAbsorbNoOps(in NativeExecutionInstruction instruction) =>
        instruction.Op == NativeOp.Constant || instruction.Handler == NativeInterpreter.FrameLoadHandler
        || instruction.Handler == NativeInterpreter.FrameStoreHandler || instruction.Handler == NativeInterpreter.OffsetLoadHandler
        || instruction.Handler == NativeInterpreter.OffsetStoreHandler || instruction.Handler == NativeInterpreter.ProjectLoadHandler
        || instruction.Handler == NativeInterpreter.ProjectStoreHandler;
}
