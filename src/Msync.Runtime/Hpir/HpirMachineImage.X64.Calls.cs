using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirMachineImage
{
    private sealed partial class X64
    {
        private const int StackTop = State.StackOffset + HpirStack.MachineCursor.TopOffset;
        private const int StackLimit = State.StackOffset + HpirStack.MachineCursor.LimitOffset;
        private const int StackCurrent = State.StackOffset + HpirStack.MachineCursor.CurrentOffset;
        private const int StackReturnBoundary = State.StackOffset + HpirStack.MachineCursor.ReturnBoundaryOffset;
        private const int Header = 8, Scratch = 9, Parent = 10, Resume = 11;
        private readonly List<(int Patch, int Target)> guestCalls = [];

        internal int CachedCallSites { get; private set; }
        internal int LateCallSites { get; private set; }
        internal int DirectCallSites => guestCalls.Count + CachedCallSites + LateCallSites;

        private void GuestCall(int pc, HpirInstruction op, HpirCall call, int counter)
        {
            // A frozen direct target has no type/admission check on this edge.
            Memory(0x83, 7, Context, State.CancelOffset);
            Emit(0);
            boundaries.Add((Conditional(0x85), pc));
            bool local = Local(call.Target);
            nint cached = local ? 0 : image.Target(call.Target);
            if (!local && cached == 0)
            {
                // Read the target before committing the frame. A miss leaves
                // the original Call for the executor; later calls see the slot.
                Memory(0x8b, Parent, Context, State.EntryTableOffset, wide: true);
                Memory(0x8b, Parent, Parent, checked(call.Target * sizeof(long)), wide: true);
                Emit(0x4d, 0x85, 0xd2); // test r10,r10
                boundaries.Add((Conditional(0x84), pc));
            }
            HpirFunction target = program.Functions[call.Target];
            int extent = HpirAbi.FrameHeaderBytes + target.FrameBytes;
            Memory(0x8b, Header, Context, StackTop, wide: true);
            Memory(0x8d, Scratch, Header, extent, wide: true);
            Memory(0x3b, Scratch, Context, StackLimit, wide: true);
            boundaries.Add((Conditional(0x87), pc));
            Count(counter);

            Memory(0x8b, A, Context, StackCurrent, wide: true);
            Memory(0x89, A, Header, HpirStack.Frame.ParentOffset, wide: true);
            if (target.Result == HpirWidth.Void) Emit(0x31, 0xc0);
            else
            {
                (int register, int offset) = Operand(op.D);
                Memory(0x8d, A, register, offset, wide: true);
            }
            Memory(0x89, A, Header, HpirStack.Frame.ResultOffset, wide: true);
            HeaderInt(HpirStack.Frame.FunctionOffset, call.Target);
            HeaderInt(HpirStack.Frame.ResumeOffset, pc + 1);

            for (int index = 0; index < target.Parameters.Count; index++)
            {
                HpirParameter parameter = target.Parameters[index];
                int width = (int)parameter.Width;
                Load(call.Arguments[index], width);
                StoreAt(Header, HpirAbi.FrameHeaderBytes + parameter.Offset, width);
            }
            Memory(0x89, Scratch, Context, StackTop, wide: true);
            Memory(0x89, Header, Context, StackCurrent, wide: true);
            Memory(0x8d, Frame, Header, HpirAbi.FrameHeaderBytes, wide: true);
            if (local) guestCalls.Add((Jump(), call.Target));
            else if (cached != 0)
            {
                CachedCallSites++;
                Emit(0x48, 0xb8); // mov rax,imm64
                Qword(cached);
                Emit(0xff, 0xe0); // jmp rax
            }
            else
            {
                LateCallSites++;
                Emit(0x41, 0xff, 0xe2); // jmp r10
            }
        }

        private void GuestReturn(int pc, HpirInstruction op, HpirWidth result, int counter)
        {
            Memory(0x8d, Header, Frame, -HpirAbi.FrameHeaderBytes, wide: true);
            // Segment exits and frames with pending work share the same
            // boundary, independent of how the caller is running.
            Memory(0x3b, Header, Context, StackReturnBoundary, wide: true);
            boundaries.Add((Conditional(0x84), pc));
            Memory(0x8b, Parent, Header, HpirStack.Frame.ParentOffset, wide: true);
            Memory(0x8b, A, Parent, HpirStack.Frame.FunctionOffset);
            Memory(0x8b, Scratch, Context, State.EntryTableOffset, wide: true);
            Emit(0x4d, 0x8b, 0x8c, 0xc1); // mov r9,[r9+rax*8+disp32] (parent's PC row)
            Dword(checked(program.Functions.Count * sizeof(long)));
            if (functionLabels.Length != program.Functions.Count)
            {
                Emit(0x4d, 0x85, 0xc9); // test r9,r9
                boundaries.Add((Conditional(0x84), pc));
            }
            Count(counter);
            if (result != HpirWidth.Void)
            {
                int width = (int)result;
                Load(op.A, width);
                Memory(0x8b, Resume, Header, HpirStack.Frame.ResultOffset, wide: true);
                StoreAt(Resume, 0, width);
            }
            Memory(0x8b, Resume, Header, HpirStack.Frame.ResumeOffset);
            Memory(0x89, Header, Context, StackTop, wide: true);
            Memory(0x89, Parent, Context, StackCurrent, wide: true);
            Memory(0x8d, Frame, Parent, HpirAbi.FrameHeaderBytes, wide: true);

            Emit(0x4b, 0x63, 0x04, 0x99); // movsxd rax,[r9+r11*4] (resume PC)
            Emit(0x4c, 0x01, 0xc8, 0xff, 0xe0); // add rax,r9; jmp rax
        }

        private void StoreAt(int address, int offset, int width) =>
            Memory(width == 1 ? 0x88 : 0x89, A, address, offset, width == 8, width == 2 ? (byte)0x66 : (byte)0);

        private void HeaderInt(int offset, int value)
        {
            Memory(0xc7, 0, Header, offset);
            Dword(value);
        }
    }
}
