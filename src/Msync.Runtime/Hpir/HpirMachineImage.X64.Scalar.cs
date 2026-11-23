using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirMachineImage
{
    private sealed partial class X64
    {
        private void Scalar(HpirFunction function, int pc, HpirInstruction op)
        {
            switch (op.Op)
            {
                case HpirOp.CopyU8: Copy(op, 1); return;
                case HpirOp.CopyU16: Copy(op, 2); return;
                case HpirOp.CopyU32: Copy(op, 4); return;
                case HpirOp.CopyU64: Copy(op, 8); return;
                case HpirOp.ConstU8: Constant(op, 1); return;
                case HpirOp.ConstU16: Constant(op, 2); return;
                case HpirOp.ConstU32: Constant(op, 4); return;
                case HpirOp.ConstU64: Constant(op, 8); return;
                case HpirOp.AddressOf:
                    Memory(0x8d, A, Frame, op.A, wide: true);
                    Store(op.D, 8);
                    return;
                case HpirOp.AddressOfConnection:
                    Memory(0x8b, AddressScratch, Context, State.LiteralOffset, wide: true);
                    Memory(0x8b, A, AddressScratch, checked(HpirConnectionAbi.Text(op.Immediate) * sizeof(ulong)), wide: true);
                    ConnectionKind endpoint = HpirConnectionAbi.Kind(op.Immediate);
                    if (endpoint != ConnectionKind.Local)
                        Emit(0x48, 0x83, 0xc8, (byte)endpoint); // or rax, endpoint tag
                    Store(op.D, 8);
                    return;
                case HpirOp.AddressOfStatic or HpirOp.AddressOfStaticCell or HpirOp.AddressOfTypeCell:
                    int index = checked((int)op.Immediate + (op.Op == HpirOp.AddressOfTypeCell ? program.TextLiterals.Count : 0));
                    Memory(0x8b, AddressScratch, Context, State.LiteralOffset, wide: true);
                    Memory(op.Op == HpirOp.AddressOfStaticCell ? 0x8d : 0x8b, A, AddressScratch, checked(index * 8), wide: true);
                    Store(op.D, 8);
                    return;
                case HpirOp.AddressOfGlobal:
                    Memory(0x8b, AddressScratch, Context, State.GlobalOffset, wide: true);
                    Memory(0x8b, A, AddressScratch, checked((int)op.Immediate * 8), wide: true);
                    Store(op.D, 8);
                    return;
                case HpirOp.OffsetPointer: Offset(op); return;
                case HpirOp.RemF64: Remainder(op); return;
                case HpirOp.NegF64:
                    Load(op.A, 8);
                    Emit(0x48, 0x0f, 0xba, 0xf8, 0x3f); // btc rax, 63
                    Store(op.D, 8);
                    return;
                case HpirOp.EqF64: FloatCompare(op, 0x94, 0x9b); return;
                case HpirOp.NeF64: FloatCompare(op, 0x95, 0x9a); return;
                case HpirOp.LtF64: FloatCompare(op, 0x92, 0x9b); return;
                case HpirOp.LeF64: FloatCompare(op, 0x96, 0x9b); return;
                case HpirOp.GtF64: FloatCompare(op, 0x97); return;
                case HpirOp.GeF64: FloatCompare(op, 0x93); return;
                case HpirOp.AndU8: IntegerBinary(op, 0x22, 1); return;
                case HpirOp.OrU8: IntegerBinary(op, 0x0a, 1); return;
                case HpirOp.XorU8: IntegerBinary(op, 0x32, 1); return;
                case HpirOp.EqU8: IntegerCompare(op, 0x94, width: 1); return;
                case HpirOp.EqU64: IntegerCompare(op, 0x94); return;
                case HpirOp.ShlU64: Shift(op, 0xe0); return;
                case HpirOp.ShrU64: Shift(op, 0xe8); return;
                case HpirOp.SarI64: Shift(op, 0xf8); return;
                case HpirOp.LtU64: IntegerCompare(op, 0x92); return;
                case HpirOp.LeU64: IntegerCompare(op, 0x96); return;
                case HpirOp.LtI64: IntegerCompare(op, 0x9c); return;
                case HpirOp.LeI64: IntegerCompare(op, 0x9e); return;
                case HpirOp.SignExtendI8: SignExtend(op, 0xbe, extended: true); return;
                case HpirOp.SignExtendI16: SignExtend(op, 0xbf, extended: true); return;
                case HpirOp.SignExtendI32: SignExtend(op, 0x63, extended: false); return;
                case HpirOp.OrdinalF64ToU64: RangeOrdinal(op); return;
                case HpirOp.ConvertI64ToF64:
                    Load(op.A, 8);
                    Emit(0xf2, 0x48, 0x0f, 0x2a, 0xc0); // cvtsi2sd xmm0, rax
                    FloatMemory(0x11, 0, op.D);
                    return;
                case HpirOp.ConvertU8ToF64:
                    Right(0xb6, op.A, extended: true); // movzx eax, byte ptr [...]
                    Emit(0xf2, 0x0f, 0x2a, 0xc0); // cvtsi2sd xmm0, eax
                    FloatMemory(0x11, 0, op.D);
                    return;
                case HpirOp.BoolFromF64:
                    FloatMemory(0x10, 0, op.A);
                    Emit(0x66, 0x0f, 0x57, 0xc9, 0x66, 0x0f, 0x2e, 0xc1); // xorpd xmm1,xmm1; ucomisd xmm0,xmm1
                    Emit(0x0f, 0x95, 0xc0); // Unordered also sets ZF: NaN is false.
                    Store(op.D, 1);
                    return;
                default:
                    throw new MsyncException($"HPIR machine selection in '{function.Name}' at {pc}: {op.Op}");
            }
        }

        private void RangeOrdinal(HpirInstruction op)
        {
            FloatMemory(0x10, 0, op.A);
            Emit(0xf2, 0x48, 0x0f, 0x2c, 0xc0); // cvttsd2si rax, xmm0
            Emit(0xf2, 0x48, 0x0f, 0x2a, 0xc8); // cvtsi2sd xmm1, rax
            Emit(0x48, 0xc7, 0xc2, 0xff, 0xff, 0xff, 0xff); // mov rdx, -1
            Emit(0x66, 0x0f, 0x2e, 0xc1); // ucomisd xmm0, xmm1
            Emit(0x48, 0x0f, 0x45, 0xc2); // cmovne rax, rdx
            Emit(0x48, 0x0f, 0x4a, 0xc2); // cmovp rax, rdx
            // Includes the signed-conversion indefinite result (INT64_MIN).
            Emit(0x48, 0x85, 0xc0, 0x48, 0x0f, 0x48, 0xc2); // test rax, rax; cmovs rax, rdx
            Store(op.D, 8);
        }

        private void Load(int operand, int width)
        {
            (int register, int offset) = Operand(operand);
            Memory(width == 1 ? 0x8a : 0x8b, A, register, offset, width == 8, width == 2 ? (byte)0x66 : (byte)0);
        }

        private void Store(int operand, int width)
        {
            (int register, int offset) = Operand(operand);
            Memory(width == 1 ? 0x88 : 0x89, A, register, offset, width == 8, width == 2 ? (byte)0x66 : (byte)0);
        }

        private void Copy(HpirInstruction op, int width)
        {
            Load(op.A, width);
            Store(op.D, width);
        }

        private void Constant(HpirInstruction op, int width)
        {
            if (width == 8)
            {
                Emit(0x48, 0xb8);
                Qword(op.Immediate);
            }
            else
            {
                Emit(0xb8);
                Dword((int)op.Immediate);
            }
            Store(op.D, width);
        }

        private void IntegerBinary(HpirInstruction op, int operation, int width, bool extended = false)
        {
            Load(op.A, width);
            Right(operation, op.B, wide: width == 8, extended: extended);
            Store(op.D, width);
        }

        private void Shift(HpirInstruction op, byte operand)
        {
            Load(op.A, 8);
            (int register, int offset) = Operand(op.B);
            Memory(0x8b, 1, register, offset, wide: true);
            Emit(0x48, 0xd3, operand);
            Store(op.D, 8);
        }

        private void IntegerCompare(HpirInstruction op, byte predicate, int width = 8)
        {
            Load(op.A, width);
            Right(width == 1 ? 0x3a : 0x3b, op.B, wide: width == 8);
            Emit(0x0f, predicate, 0xc0);
            Store(op.D, 1);
        }

        private void SignExtend(HpirInstruction op, int opcode, bool extended)
        {
            Right(opcode, op.A, wide: true, extended: extended);
            Store(op.D, 8);
        }

        private void Offset(HpirInstruction op)
        {
            Load(op.A, 8);
            if (op.Immediate is >= int.MinValue and <= int.MaxValue)
            {
                Emit(0x48, 0x05);
                Dword((int)op.Immediate);
            }
            else
            {
                Emit(0x49, 0xb9);
                Qword(op.Immediate);
                Emit(0x4c, 0x01, 0xc8);
            }
            Store(op.D, 8);
        }

        private void Right(int operation, int operand, bool wide = false, byte prefix = 0, bool extended = false)
        {
            (int register, int offset) = Operand(operand);
            Memory(operation, A, register, offset, wide, prefix, extended);
        }

        private void FloatMemory(int instruction, int xmm, int operand)
        {
            (int register, int offset) = Operand(operand);
            Memory(instruction, xmm, register, offset, prefix: 0xf2, extended: true);
        }

        internal int ForwardedOperands { get; private set; }
        internal int ElidedStores { get; private set; }

        private enum BinaryKind : byte { None, Float, Word, Multiply }
        private readonly record struct Binary(BinaryKind Kind, byte Opcode);

        private static Binary SelectBinary(HpirOp op) => op switch
        {
            HpirOp.AddF64 => new(BinaryKind.Float, 0x58),
            HpirOp.SubF64 => new(BinaryKind.Float, 0x5c),
            HpirOp.MulF64 => new(BinaryKind.Float, 0x59),
            HpirOp.DivF64 => new(BinaryKind.Float, 0x5e),
            HpirOp.AddU64 or HpirOp.AddPointer => new(BinaryKind.Word, 0x03),
            HpirOp.SubU64 => new(BinaryKind.Word, 0x2b),
            HpirOp.MulU64 => new(BinaryKind.Multiply, 0xaf),
            HpirOp.AndU64 => new(BinaryKind.Word, 0x23),
            HpirOp.OrU64 => new(BinaryKind.Word, 0x0b),
            HpirOp.XorU64 => new(BinaryKind.Word, 0x33),
            _ => default
        };

        private static void MarkBranchTargets(HpirInstruction[] instructions, int[] labels)
        {
            foreach (HpirInstruction op in instructions)
            {
                if (op.Op == HpirOp.Jump) labels[op.A] = -1;
                else if (op.Op == HpirOp.Branch) labels[op.B] = labels[op.D] = -1;
            }
        }

        private int ScalarRun(HpirInstruction[] instructions, int[] labels, int pc, Binary binary, int counterStart)
        {
            bool floating = binary.Kind == BinaryKind.Float;
            HpirInstruction op = instructions[pc];
            if (floating) FloatMemory(0x10, 0, op.A);
            else Load(op.A, 8);
            while (true)
            {
                if (observe) Count(checked(counterStart + pc * CountersPerInstruction));
                BinaryRight(binary, op);
                if (op.D < 0 || pc + 1 == instructions.Length || labels[pc + 1] == -1)
                    break;
                HpirInstruction next = instructions[pc + 1];
                binary = SelectBinary(next.Op);
                if (binary.Kind == BinaryKind.None || (binary.Kind == BinaryKind.Float) != floating || next.A != op.D)
                    break;

                // No outside entry or suspend point can see the register.
                // Skip a write only when it is overwritten and the other
                // input cannot observe it through an overlapping address.
                if (next.D == op.D && next.B >= 0 &&
                    (next.B == op.D || Math.Abs((long)next.B - op.D) >= sizeof(long)))
                    ElidedStores++;
                else StoreBinary(floating, op.D);
                ForwardedOperands++;
                labels[++pc] = Length;
                op = next;
            }
            StoreBinary(floating, op.D);
            return pc;
        }

        private void BinaryRight(Binary binary, HpirInstruction op)
        {
            bool same = op.A >= 0 && op.A == op.B;
            if (binary.Kind == BinaryKind.Float)
            {
                if (same) Emit(0xf2, 0x0f, binary.Opcode, 0xc0); // op xmm0,xmm0
                else FloatMemory(binary.Opcode, 0, op.B);
            }
            else if (!same) Right(binary.Opcode, op.B, wide: true, extended: binary.Kind == BinaryKind.Multiply);
            else if (binary.Kind == BinaryKind.Multiply) Emit(0x48, 0x0f, binary.Opcode, 0xc0);
            else Emit(0x48, binary.Opcode, 0xc0); // op rax,rax
        }

        private void StoreBinary(bool floating, int operand)
        {
            if (floating) FloatMemory(0x11, 0, operand);
            else Store(operand, 8);
        }

        private void Remainder(HpirInstruction op)
        {
            nint entry = NativeSystemMath.Remainder;
            if (entry == 0) throw new PlatformNotSupportedException("binary64 remainder requires system fmod");
            FloatMemory(0x10, 0, op.A);
            FloatMemory(0x10, 1, op.B);
            Emit(0x48, 0xb8);
            Qword(entry);
            Emit(0xff, 0xd0);
            FloatMemory(0x11, 0, op.D);
        }

        private void FloatCompare(HpirInstruction op, byte predicate, byte order = 0)
        {
            FloatMemory(0x10, 0, op.A);
            Right(0x2e, op.B, prefix: 0x66, extended: true);
            Emit(0x0f, predicate, 0xc0);
            if (order != 0)
            {
                // Unordered sets ZF/CF/PF. Correct eq/ne/lt/le without a branch.
                Emit(0x41, 0x0f, order, 0xc1);
                Emit(0x44, order == 0x9a ? (byte)0x08 : (byte)0x20, 0xc8);
            }
            Store(op.D, 1);
        }
    }
}
