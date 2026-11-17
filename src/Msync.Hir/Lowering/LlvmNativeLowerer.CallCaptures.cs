using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private HashSet<LlvmInstruction>? erasedCapturePreparation;

        // LLVM direct calls pass each capture as a pointer. Native calls receive the
        // evaluated closure instead; keep only producers with another Native use.
        private void PlanCallCapturePreparation()
        {
            Stack<LlvmInstruction>? pending = null;
            int edges = 0, loads = 0, projections = 0, offsets = 0;
            foreach (var block in function.BasicBlocks)
                for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null; instruction = instruction.Next)
                {
                    if (instruction is not LlvmCallInstruction call) continue;
                    int count = CallCaptureCount(call);
                    for (int index = 0; index < count; index++)
                    {
                        edges++;
                        if (call.GetArgument(index) is LlvmInstruction producer && CapturePreparation(producer))
                            (pending ??= new()).Push(producer);
                    }
                }
            if (pending is null)
            {
                trace?.Metric("calls.capture_abi_edges", edges);
                return;
            }
            erasedCapturePreparation = [];
            while (pending.TryPop(out LlvmInstruction? instruction))
            {
                if (erasedCapturePreparation.Contains(instruction) || instruction.Uses.Any(RetainedCaptureUse)) continue;
                erasedCapturePreparation.Add(instruction);
                if (instruction is LlvmLoadInstruction) loads++;
                else if (instruction is LlvmExtractValueInstruction) projections++;
                else offsets++;
                foreach (LlvmUse use in instruction.OperandUses)
                    if (use.Value is LlvmInstruction producer && CapturePreparation(producer)) pending.Push(producer);
            }
            trace?.Metric("calls.capture_abi_edges", edges);
            trace?.Metric("calls.capture_abi_erased_loads", loads);
            trace?.Metric("calls.capture_abi_erased_offsets", offsets);
            trace?.Metric("calls.capture_abi_erased_projections", projections);
        }

        private bool RetainedCaptureUse(LlvmUse use) =>
            use.User is not LlvmInstruction instruction || erasedCapturePreparation?.Contains(instruction) != true
            && (instruction is not LlvmCallInstruction call || use.OperandNo >= CallCaptureCount(call));

        private int CallCaptureCount(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && owner.Input.SourceFunctions.TryGetValue(target, out var source)
            && source.CaptureCount != 0 && call.GetOperandBundle("msync.callee") is not null ? source.CaptureCount : 0;

        private static bool CapturePreparation(LlvmInstruction producer) => producer switch
        {
            LlvmLoadInstruction { IsSimple: true } load => load.Type.IsPointerTy(),
            LlvmGetElementPtrInstruction => true,
            LlvmExtractValueInstruction extract => IsFunctionAggregate(extract.AggregateOperand.Type)
                && extract.Indices.Count == 1 && extract.Indices[0] == 1,
            _ => false
        };
    }
}
