using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeExecutionFunction
{
    private void FuseScopeCopies()
    {
        if (!instructions.Any(IsFieldLoad)) return;
        // A linked peephole over the verified frame plan. No changes to portable
        // PCs, call/escape information, or the ownership of the final cell.
        int slots = Definition.Slots.Count;
        Span<int> reads = slots <= 256 ? stackalloc int[slots] : new int[slots];
        Span<int> writes = slots <= 256 ? stackalloc int[slots] : new int[slots];
        Span<bool> entries = instructions.Length < 1024 ? stackalloc bool[instructions.Length + 1] : new bool[instructions.Length + 1];
        reads.Clear();
        writes.Clear();
        entries.Clear();
        foreach (NativeInstruction code in Metadata)
        {
            if (code.Op == NativeOp.Jump) entries[code.A] = true;
            else if (code.Op == NativeOp.Branch) entries[code.B] = entries[code.C] = true;
            else if (code.Op == NativeOp.NextIterator) entries[code.B] = true;
            foreach (int slot in NativeInstructionFacts.Reads(code)) reads[slot]++;
            foreach (int slot in NativeInstructionFacts.Definitions(code)) writes[slot]++;
        }

        int arguments = Definition.CaptureCount + Definition.ParameterCount;
        for (int pc = 0; pc < instructions.Length; pc += instructions[pc].Span)
        {
            ref NativeExecutionInstruction producer = ref instructions[pc];
            if (!IsFieldLoad(producer) || !IsReferenceCarrier(FrameSlot(producer.Result).Type)) continue;

            while (producer.ForwardedCopies < ushort.MaxValue)
            {
                int next = pc + producer.Span;
                int temp = producer.Result;
                if (next >= instructions.Length || entries[next] || temp < arguments
                    || !RawWritable[temp] || reads[temp] != 1 || writes[temp] != 1) break;
                ref readonly NativeExecutionInstruction copy = ref instructions[next];
                if (Metadata[next].Op != NativeOp.Copy || copy.Op != NativeOp.Copy || copy.Span != 1
                    || copy.A != temp || copy.Result == temp || copy.Result == producer.A || !RawWritable[copy.Result]
                    || copy.Handler == NativeInterpreter.NopHandler
                    || !FrameSlot(temp).Type.Equals(FrameSlot(copy.Result).Type)) break;
                producer = producer.ForwardTo(copy.Result);
            }
        }
    }

    private static bool IsFieldLoad(NativeExecutionInstruction instruction) =>
        instruction.Op == NativeOp.Load
        && (instruction.Handler == NativeInterpreter.OffsetLoadHandler || instruction.Handler == NativeInterpreter.ProjectLoadHandler);
}
