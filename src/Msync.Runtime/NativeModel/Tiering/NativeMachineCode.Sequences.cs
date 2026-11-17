using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private void SequenceNext(NativeExecutionInstruction instruction, int pc)
        {
            int cursor = function.SequenceOffset(instruction.A), state = cursor + 24;
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28); // r10: native frame base.
            Bytes(0x41, 0x8b, 0x82); Int(state);
            Bytes(0xa8, NativeSequenceCursor.Open, 0x0f, 0x84);
            int opaque = code.Count; Int(0);
            Bytes(0xa8, NativeSequenceCursor.Stalled | NativeSequenceCursor.Finished, 0x0f, 0x85);
            int stalled = code.Count; Int(0);
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 1, 0x0f, 0x85);
            int pending = code.Count; Int(0);

            Bytes(0xf2, 0x41, 0x0f, 0x10, 0x82); Int(cursor);
            Bytes(0xa8, NativeSequenceCursor.Descending, 0x0f, 0x85);
            int descending = code.Count; Int(0);
            Bytes(0x66, 0x41, 0x0f, 0x2e, 0x82); Int(cursor + 8);
            Bytes(0x0f, 0x83); int upperLimit = code.Count; Int(0);
            Bytes(0xe9); int ascending = code.Count; Int(0);
            PatchLocal(descending);
            Bytes(0x66, 0x41, 0x0f, 0x2e, 0x82); Int(cursor + 8);
            Bytes(0x0f, 0x86); int lowerLimit = code.Count; Int(0);
            PatchLocal(ascending);

            Bytes(0x66, 0x0f, 0x28, 0xc8);
            Bytes(0xf2, 0x41, 0x0f, 0x58, 0x8a); Int(cursor + 16);
            Bytes(0x66, 0x0f, 0x2e, 0xc8, 0x0f, 0x85);
            int advanced = code.Count; Int(0);
            // A stalled increment fails on the next request, not this yield.
            Bytes(0x41, 0x83, 0x8a); Int(state); Bytes(NativeSequenceCursor.Stalled);
            PatchLocal(advanced);
            Bytes(0xf2, 0x41, 0x0f, 0x11, 0x8a); Int(cursor);
            int yield = code.Count;
            layout.TryFixed(instruction.Result, out FixedTierSlot result);
            Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82); Int(result.DataOffset);
            StoreInitialized(instruction.Result, result);
            Advance(pc, pc + instruction.Span, instruction.Span, layout.TrueHits[pc]);

            PatchLocal(upperLimit);
            Bytes(0x0f, 0x85); int above = code.Count; Int(0);
            Bytes(0xa8, NativeSequenceCursor.Inclusive, 0x0f, 0x84); int exclusive = code.Count; Int(0);
            Bytes(0x41, 0x83, 0x8a); Int(state); Bytes(NativeSequenceCursor.Finished);
            Bytes(0xe9); Int(yield - code.Count - 4);
            PatchLocal(above);
            PatchLocal(exclusive);
            PatchLocal(lowerLimit);
            Advance(pc, instruction.B, instruction.Span, layout.Hits[pc]);
            PatchLocal(opaque);
            PatchLocal(stalled);
            PatchLocal(pending);
            EmitBridge(pc);
        }
    }
}
