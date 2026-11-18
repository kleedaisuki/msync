using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private sealed partial class X64
    {
        private void MaterializeValue(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            int demand = GuardTarget(pc, demand: true);
            Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 1); Jump(0x0f, 0x85, demand);
            ref readonly NativeFrameSlot result = ref function.FrameSlot(instruction.Result);
            Bytes(0x4d, 0x8b, 0x4c, 0x24, NativeTierLayout.RawOffset); // r9: byte lane.
            Bytes(0x4d, 0x8b, 0x5c, 0x24, NativeTierLayout.InitializationOffset); // r11: initialization lane.
            if (operation == NativeFastOperation.Integer64Constant)
            {
                ulong bits = constants[instruction.A].ToMachineValue().AsMachineInteger().GetZExtValue();
                StoreIntegerLiteral(result.Offset.Bytes, bits);
            }
            else
            {
                int references = result.Type.Representation.References;
                int edge = result.Offset.References * sizeof(long);
                GuardReplaceGuestRoots(edge, references, demand);
                if (operation == NativeFastOperation.GuestComposeFunction)
                    ComposeFunction(pc, result.Offset.Bytes, demand);
                else
                {
                    Bytes(0x41, 0xc6, 0x81); Int(result.Offset.Bytes); Bytes((byte)NativeKind.Null);
                    Bytes(0x41, 0xc6, 0x83); Int(result.Offset.Bytes); Bytes(1);
                }
                for (int index = 0; index < references; index++)
                {
                    Bytes(0x49, 0xc7, 0x82); Int(edge + index * sizeof(long)); Int(0);
                }
                if (layout.MachineClosures?[instruction.Result] == true)
                {
                    Bytes(0x4d, 0x8b, 0x54, 0x24, 8, 0x49, 0xc7, 0x82);
                    Int(layout.Scalars[instruction.Result] * 32); Int(0);
                }
            }
            Advance(pc, pc + instruction.Span, instruction.Span, layout.Hits[pc]);
        }

        private void StoreIntegerLiteral(int offset, ulong bits)
        {
            if (unchecked((ulong)(long)(int)bits) == bits)
            {
                Bytes(0x49, 0xc7, 0x81); Int(offset); Int(unchecked((int)bits));
            }
            else
            {
                Bytes(0x48, 0xb8); Long(unchecked((nint)bits));
                Bytes(0x49, 0x89, 0x81); Int(offset);
            }
            InitializeFourBytes(offset);
            InitializeFourBytes(offset + 4);
        }

        private void ComposeFunction(int pc, int offset, int demand)
        {
            IReadOnlyList<int> inputs = function.Metadata[pc].Inputs!;
            ref readonly NativeFrameSlot target = ref function.FrameSlot(inputs[0]);
            RequireReadyFrameValue(target.Offset.Bytes, demand);
            for (int index = 1; index < inputs.Count; index++)
            {
                ref readonly NativeFrameSlot part = ref function.FrameSlot(inputs[index]);
                RequireReadyFrameValue(part.Offset.Bytes, demand);
                if (!part.Type.IsUnion) continue;
                Bytes(0x41, 0x80, 0xb9); Int(part.Offset.Bytes); Bytes((byte)NativeKind.Null);
                Jump(0x0f, 0x85, demand);
            }
            // The canonical code word is the signed 32-bit target plus one.
            Bytes(0x41, 0x8b, 0x81); Int(target.Offset.Bytes);
            Bytes(0xff, 0xc8, 0x41, 0x89, 0x81); Int(offset);
            InitializeFourBytes(offset);
        }

        private void RequireReadyFrameValue(int offset, int demand)
        {
            Bytes(0x41, 0x80, 0xbb); Int(offset); Bytes(0);
            Jump(0x0f, 0x84, demand);
        }

        private void InitializeFourBytes(int offset)
        {
            Bytes(0x41, 0xc7, 0x83); Int(offset); Int(0x01010101);
        }

        private void GuardReplaceGuestRoots(int edge, int references, int demand)
        {
            if (references == 0) return;
            Bytes(0x4d, 0x8b, 0x54, 0x24, NativeTierLayout.GuestEdgesOffset, 0x4d, 0x85, 0xd2);
            Jump(0x0f, 0x84, demand);
            Bytes(0x49, 0x8b, 0x82); Int(edge);
            if (references == 2) { Bytes(0x49, 0x0b, 0x82); Int(edge + sizeof(long)); }
            Bytes(0x48, 0x85, 0xc0); Jump(0x0f, 0x88, demand);
        }
    }
}
