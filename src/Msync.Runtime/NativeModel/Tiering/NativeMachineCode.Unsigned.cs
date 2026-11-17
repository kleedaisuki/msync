using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    private static bool UnsignedOperation(NativeFastOperation operation, NativeRegisterRegion? region, NativeRegisterOperation selected) =>
        operation == NativeFastOperation.NumberRemainder || region is not null
            && !operation.ExpectsBoolInputs() && selected.Form != NativeNumberForm.Float64;

    private sealed partial class X64
    {
        private void Unsigned(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc,
            NativeRegisterRegion? region, NativeRegisterOperation selected)
        {
            if (operation == NativeFastOperation.NumberConstant)
            { Bytes(0x48, 0xb8); Long((nint)(ulong)constants[instruction.A].Number); return; }
            UnsignedOperand(instruction.A, selected.A, false, pc, region);
            if (!operation.TakesSecondOperand()) return;
            UnsignedOperand(instruction.B, selected.B, true, pc, region);
            switch (operation)
            {
                case NativeFastOperation.NumberAdd: Bytes(0x48, 0x01, 0xc8); break;
                case NativeFastOperation.NumberSubtract: Bytes(0x48, 0x29, 0xc8); break;
                case NativeFastOperation.NumberMultiply: Bytes(0x48, 0x0f, 0xaf, 0xc1); break;
                case NativeFastOperation.NumberRemainder: Bytes(0x48, 0x31, 0xd2, 0x48, 0xf7, 0xf1, 0x48, 0x89, 0xd0); break;
                default:
                    byte condition = operation switch
                    {
                        NativeFastOperation.NumberEqual => 0x94, NativeFastOperation.NumberNotEqual => 0x95,
                        NativeFastOperation.NumberLess => 0x92, NativeFastOperation.NumberLessEqual => 0x96,
                        NativeFastOperation.NumberGreater => 0x97, NativeFastOperation.NumberGreaterEqual => 0x93,
                        _ => throw new InvalidOperationException($"Unsupported unsigned operation {operation}")
                    };
                    Bytes(0x48, 0x39, 0xc8, 0x0f, condition, 0xc0, 0x0f, 0xb6, 0xc0);
                    break;
            }
            if (region is null && !operation.IsBoolResult()) Bytes(0xf2, 0x48, 0x0f, 0x2a, 0xc0);
        }

        private void UnsignedOperand(int slot, int value, bool second, int pc, NativeRegisterRegion? region)
        {
            bool bits = false;
            if (region is null) Scalar(slot, NativeKind.Number, second, pc);
            else
            {
                NativeRegisterValue source = region.Values[value];
                bits = source.Unsigned;
                int target = second ? 1 : 0;
                if (source.Register >= 0) Bytes(0x66, 0x0f, 0x28, (byte)(0xc0 | target << 3 | source.Register));
                else { Bytes(0xf2, 0x0f, 0x10, (byte)(0x84 | target << 3), 0x24); Int(ShadowBytes + source.Spill * 8); }
            }
            Bytes(bits ? (byte)0x66 : (byte)0xf2, 0x48, 0x0f, bits ? (byte)0x7e : (byte)0x2c, second ? (byte)0xc9 : (byte)0xc0);
        }
    }

    private sealed partial class Arm64
    {
        private void Unsigned(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc,
            NativeRegisterRegion? region, NativeRegisterOperation selected)
        {
            if (operation == NativeFastOperation.NumberConstant)
            { Load(2, (ulong)constants[instruction.A].Number); return; }
            UnsignedOperand(instruction.A, selected.A, false, pc, region);
            if (!operation.TakesSecondOperand()) return;
            UnsignedOperand(instruction.B, selected.B, true, pc, region);
            switch (operation)
            {
                case NativeFastOperation.NumberAdd: Word(0x8b030042); break;
                case NativeFastOperation.NumberSubtract: Word(0xcb030042); break;
                case NativeFastOperation.NumberMultiply: Word(0x9b037c42); break;
                case NativeFastOperation.NumberRemainder: Word(0x9ac3084e); Word(0x9b0389c2); break;
                default:
                    uint condition = operation switch
                    {
                        NativeFastOperation.NumberEqual => 0, NativeFastOperation.NumberNotEqual => 1,
                        NativeFastOperation.NumberLess => 3, NativeFastOperation.NumberLessEqual => 9,
                        NativeFastOperation.NumberGreater => 8, NativeFastOperation.NumberGreaterEqual => 2,
                        _ => throw new InvalidOperationException($"Unsupported unsigned operation {operation}")
                    };
                    Word(0xeb03005f); Word(0x1a9f07e2 | ((condition ^ 1) << 12));
                    break;
            }
            if (region is null && !operation.IsBoolResult()) Word(0x9e630040);
        }

        private void UnsignedOperand(int slot, int value, bool second, int pc, NativeRegisterRegion? region)
        {
            bool bits = false;
            if (region is null) Scalar(slot, NativeKind.Number, second, pc);
            else
            {
                NativeRegisterValue source = region.Values[value];
                bits = source.Unsigned;
                uint target = second ? 1u : 0u;
                if (source.Register >= 0) Word(0x1e604000 | ((uint)source.Register << 5) | target);
                else { Load(17, (ulong)(source.Spill * 8)); Word(0x8b3163f1); Word(0xfd400220 | target); }
            }
            Word((bits ? 0x9e660000u : 0x9e790000u) | (second ? 0x23u : 0x2u));
        }
    }
}
