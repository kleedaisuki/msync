using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static class NativeMachineCode
{
    internal const bool HasDirectPaths = true;
    internal const bool AutoEnabled = false;
    internal static byte[] Emit(NativeExecutionFunction function, NativeFastOperation[] operations,
        Architecture architecture, bool windows, nint bridge, nint number, nint boolean,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout? layout = null)
    {
        layout ??= NativeTierLayout.Create(function, operations);
        return architecture switch
        {
            Architecture.X64 => new X64(function, operations, constants, layout, windows, bridge, number, boolean).Emit(),
            Architecture.Arm64 when !windows => new Arm64(function, operations, constants, layout, bridge, number, boolean).Emit(),
            _ => throw new PlatformNotSupportedException("Native guest code generation is not available on this platform.")
        };
    }

    private sealed class X64(NativeExecutionFunction function, NativeFastOperation[] operations,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout layout, bool windows, nint bridge, nint number, nint boolean)
    {
        private readonly List<byte> code = [];
        private readonly List<(int Offset, int Target)> jumps = [];
        private readonly int[] labels = new int[function.Instructions.Length + 1];
        private int tableReference, dispatch, exit, guard;
        internal byte[] Emit()
        {
            Bytes(0x41, 0x54); // r12 owns the opaque context across managed bridges.
            if (windows) { Bytes(0x48, 0x83, 0xec, 0x20, 0x49, 0x89, 0xcc, 0x89, 0xd0); }
            else Bytes(0x49, 0x89, 0xfc, 0x89, 0xf0);
            dispatch = code.Count;
            Bytes(0x3d); Int(function.Instructions.Length); Jump(0x0f, 0x83, -2);
            Bytes(0x48, 0x8d, 0x15); tableReference = code.Count; Int(0);
            Bytes(0x48, 0x63, 0x04, 0x82, 0x48, 0x01, 0xd0, 0xff, 0xe0);
            for (int pc = 0; pc < function.Instructions.Length; pc++)
            {
                labels[pc] = code.Count;
                Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(pc);
                NativeExecutionInstruction instruction = function.Instructions[pc];

                nint target = Fast(instruction, operations[pc], pc) ? operations[pc] is >= NativeFastOperation.NumberEqual
                    or NativeFastOperation.BoolConstant or NativeFastOperation.BoolCopy or NativeFastOperation.BoolNot ? boolean : number : bridge;
                Arguments(pc);
                Bytes(0x48, 0xb8); Long(target); Bytes(0xff, 0xd0, 0x85, 0xc0);
                Jump(0x0f, 0x88, -1);
                if (operations[pc] == NativeFastOperation.Bridge) { Jump(0xe9, -3); continue; }
                if (instruction.Op == NativeOp.Jump) Jump(0xe9, instruction.A);
                else if (instruction.Op is NativeOp.Branch or NativeOp.NextIterator)
                {
                    Bytes(0x3d); Int(instruction.B); Jump(0x0f, 0x84, instruction.B);
                    Jump(0xe9, instruction.Op == NativeOp.Branch ? instruction.C : pc + 1);
                }
            }
            labels[^1] = code.Count;
            Bytes(0xb8); Int(-1); Jump(0xe9, -1);
            guard = code.Count; Bytes(0xb8); Int(-3);
            exit = code.Count;
            if (windows) Bytes(0x48, 0x83, 0xc4, 0x20);
            Bytes(0x41, 0x5c, 0xc3);
            int table = code.Count;
            foreach (int label in labels.AsSpan(0, labels.Length - 1)) Int(label - table);
            byte[] result = code.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tableReference, 4), table - tableReference - 4);
            foreach ((int offset, int target) in jumps)
            {
                int address = target switch { -1 => exit, -2 => labels[^1], -3 => dispatch, -4 => guard, _ => labels[target] };
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), address - offset - 4);
            }
            return result;
        }

        private void Arguments(int pc)
        {
            if (windows) { Bytes(0x4c, 0x89, 0xe1, 0xba); Int(pc); }
            else { Bytes(0x4c, 0x89, 0xe7, 0xbe); Int(pc); }
        }

        private bool Fast(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            if (operation == NativeFastOperation.Bridge) return false;
            bool boolResult = operation is NativeFastOperation.BoolConstant or NativeFastOperation.BoolCopy or NativeFastOperation.BoolNot
                or >= NativeFastOperation.NumberEqual;
            if (operation is NativeFastOperation.NumberConstant)
            {
                Bytes(0x48, 0xb8); Long((nint)BitConverter.DoubleToInt64Bits(constants[instruction.A].Number));
                Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc0);
            }
            else if (operation == NativeFastOperation.BoolConstant)
            { Bytes(0xb8); Int(constants[instruction.A].Boolean ? 1 : 0); }
            else
            {
                Scalar(instruction.A, operation is NativeFastOperation.BoolCopy or NativeFastOperation.BoolNot ? NativeKind.Bool : NativeKind.Number, false);
                if (operation == NativeFastOperation.BoolNot) Bytes(0x83, 0xf0, 0x01);
                else if (operation == NativeFastOperation.NumberNegate)
                    Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0, 0x48, 0x0f, 0xba, 0xf8, 0x3f, 0x66, 0x48, 0x0f, 0x6e, 0xc0);
                else if (operation is >= NativeFastOperation.NumberAdd)
                {
                    Scalar(instruction.B, NativeKind.Number, true);
                    if (!boolResult)
                        Bytes(0xf2, 0x0f, operation switch
                        {
                            NativeFastOperation.NumberAdd => (byte)0x58,
                            NativeFastOperation.NumberSubtract => (byte)0x5c,
                            NativeFastOperation.NumberMultiply => (byte)0x59,
                            _ => (byte)0x5e
                        }, 0xc1);
                    else Compare(operation);
                }
            }
            RawWrite(instruction.Result, boolResult ? NativeKind.Bool : NativeKind.Number, pc);
            if (boolResult)
            {
                if (windows) Bytes(0x41, 0x89, 0xc0);
                else Bytes(0x89, 0xc2);
            }
            else if (windows) Bytes(0x66, 0x0f, 0x28, 0xd0);
            return true;
        }

        private void RawWrite(int slot, NativeKind kind, int pc)
        {
            int offset = checked(layout.Scalars[slot] * 32);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
            Bytes(0x41, 0xf7, 0x82); Int(offset + 28); Int(1);
            Bytes(0x0f, 0x84); int notPrivate = code.Count; Int(0);
            Bytes(0x41, 0xf7, 0x82); Int(offset + 28); Int(2);
            Bytes(0x0f, 0x85); int pending = code.Count; Int(0);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset);
            if (kind == NativeKind.Bool) Bytes(0x41, 0x88, 0x03);
            else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x03);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset + 8); Bytes(0x41, 0xc6, 0x03, 0x01);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset + 16); Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84);
            int noTag = code.Count; Int(0);
            Bytes(0x41, 0xc6, 0x03, (byte)kind); PatchLocal(noTag);
            Advance(pc);
            PatchLocal(notPrivate); PatchLocal(pending);
        }



        private void Advance(int pc)
        {
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x18, 0x49, 0xff, 0x82); Int(checked(layout.Hits[pc] * 8));
            Bytes(0x41, 0xff, 0x4c, 0x24, 0x20); Jump(0x0f, 0x85, pc + function.Instructions[pc].Span);
            Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(pc + function.Instructions[pc].Span); Bytes(0xb8); Int(-5); Jump(0xe9, -1);
        }

        private void Scalar(int slot, NativeKind kind, bool second)
        {
            int offset = checked(layout.Scalars[slot] * 32);
            Bytes(0x4d, 0x8b, 0x54, 0x24, (byte)0x08);
            Bytes(0x41, 0xf7, 0x82); Int(offset + 28); Int(2); Jump(0x0f, 0x85, -4);
            Bytes(0x4d, 0x8b, 0x9a); Int(offset + 8);
            Bytes(0x4d, 0x85, 0xdb); Jump(0x0f, 0x84, -4);
            Bytes(0x41, 0x80, 0x3b, 0x00); Jump(0x0f, 0x84, -4);
            if (!function.HasKind(slot, kind))
            {
                Bytes(0x4d, 0x8b, 0x9a); Int(offset + 16);
                Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84); int mono = code.Count; Int(0);
                Bytes(0x41, 0x80, 0x3b, (byte)kind); Jump(0x0f, 0x85, -4);
                Bytes(0xe9); int ready = code.Count; Int(0);
                PatchLocal(mono);
                Bytes(0x41, 0x83, 0xba); Int(offset + 24); Bytes((byte)kind); Jump(0x0f, 0x85, -4);
                PatchLocal(ready);
            }
            Bytes(0x4d, 0x8b, 0x9a); Int(offset);
            if (kind == NativeKind.Bool) Bytes(0x41, 0x0f, 0xb6, 0x03);
            else Bytes(0xf2, 0x41, 0x0f, 0x10, second ? (byte)0x0b : (byte)0x03);
        }

        private void Compare(NativeFastOperation operation)
        {
            Bytes(0x66, 0x0f, 0x2e, 0xc1);
            byte condition = operation switch
            {
                NativeFastOperation.NumberEqual => 0x94,
                NativeFastOperation.NumberNotEqual => 0x95,
                NativeFastOperation.NumberLess => 0x92,
                NativeFastOperation.NumberLessEqual => 0x96,
                NativeFastOperation.NumberGreater => 0x97,
                _ => 0x93
            };
            Bytes(0x0f, condition, 0xc0);
            if (operation is NativeFastOperation.NumberEqual or NativeFastOperation.NumberLess or NativeFastOperation.NumberLessEqual)
                Bytes(0x0f, 0x9b, 0xc2, 0x20, 0xd0);
            else if (operation == NativeFastOperation.NumberNotEqual) Bytes(0x0f, 0x9a, 0xc2, 0x08, 0xd0);
            Bytes(0x0f, 0xb6, 0xc0);
        }

        private void PatchLocal(int at)
        {
            int value = code.Count - at - 4;
            byte[] bytes = BitConverter.GetBytes(value);
            for (int i = 0; i < 4; i++) code[at + i] = bytes[i];
        }
        private void Jump(byte op, int target) { Bytes(op); jumps.Add((code.Count, target)); Int(0); }
        private void Jump(byte prefix, byte op, int target) { Bytes(prefix, op); jumps.Add((code.Count, target)); Int(0); }
        private void Int(int value) => code.AddRange(BitConverter.GetBytes(value));
        private void Long(nint value) => code.AddRange(BitConverter.GetBytes((long)value));
        private void Bytes(params byte[] bytes) => code.AddRange(bytes);
    }

    private sealed class Arm64(NativeExecutionFunction function, NativeFastOperation[] operations,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout layout, nint bridge, nint number, nint boolean)
    {
        private readonly List<uint> code = [];
        private readonly List<(int At, int Target, int Kind)> jumps = [];
        private readonly int[] labels = new int[function.Instructions.Length + 1];
        private int tableAdr, exit, guard, dispatch;
        internal byte[] Emit()
        {
            Word(0xa9bf7bf3); Word(0xaa0003f3); Word(0x2a0103e0);
            dispatch = code.Count;
            Load(17, (ulong)function.Instructions.Length); Word(0x6b11001f); Branch(-2, 2);
            tableAdr = code.Count; Word(0);
            Word(0xb8a05a11); Word(0x8b110210); Word(0xd61f0200);
            for (int pc = 0; pc < function.Instructions.Length; pc++)
            {
                labels[pc] = code.Count;
                Load(17, (ulong)pc); Word(0xb9001271);
                NativeExecutionInstruction instruction = function.Instructions[pc];
                NativeFastOperation operation = operations[pc];

                nint target = Fast(instruction, operation, pc) ? operation is >= NativeFastOperation.NumberEqual
                    or NativeFastOperation.BoolConstant or NativeFastOperation.BoolCopy or NativeFastOperation.BoolNot ? boolean : number : bridge;
                Word(0xaa1303e0); Load(1, (ulong)pc); Load(16, (ulong)target); Word(0xd63f0200); Word(0x7100001f);
                Branch(-1, 3);
                if (operations[pc] == NativeFastOperation.Bridge) { Branch(-3, 0); continue; }
                if (instruction.Op == NativeOp.Jump) Branch(instruction.A, 0);
                else if (instruction.Op is NativeOp.Branch or NativeOp.NextIterator)
                {
                    Load(17, (ulong)instruction.B); Word(0x6b11001f); Branch(instruction.B, 1);
                    Branch(instruction.Op == NativeOp.Branch ? instruction.C : pc + 1, 0);
                }
            }
            labels[^1] = code.Count; Word(0x12800000); Branch(-1, 0);
            guard = code.Count; Word(0x12800040);
            exit = code.Count; Word(0xa8c17bf3); Word(0xd65f03c0);
            int table = code.Count;
            foreach (int label in labels.AsSpan(0, labels.Length - 1)) Word(unchecked((uint)((label - table) * 4)));
            int delta = (table - tableAdr) * 4;
            if (delta >= 1 << 20) throw new InvalidOperationException("Guest native function exceeds the branch relocation range.");
            code[tableAdr] = 0x10000010 | ((uint)(delta & 3) << 29) | ((uint)(delta >> 2) << 5);
            foreach ((int at, int target, int kind) in jumps)
            {
                int address = target switch { -1 => exit, -2 => labels[^1], -3 => dispatch, -4 => guard, _ => labels[target] };
                int offset = address - at;
                code[at] = kind switch
                {
                    0 => 0x14000000 | ((uint)offset & 0x03ffffff),
                    1 => 0x54000000 | (((uint)offset & 0x7ffff) << 5),
                    2 => 0x54000002 | (((uint)offset & 0x7ffff) << 5),
                    3 => 0x5400000b | (((uint)offset & 0x7ffff) << 5),
                    _ => 0x54000001 | (((uint)offset & 0x7ffff) << 5)
                };
            }
            byte[] result = new byte[code.Count * 4];
            for (int index = 0; index < code.Count; index++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index * 4, 4), code[index]);
            return result;
        }

        private bool Fast(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            if (operation == NativeFastOperation.Bridge) return false;
            if (operation == NativeFastOperation.NumberConstant)
            { Load(17, BitConverter.DoubleToUInt64Bits(constants[instruction.A].Number)); Word(0x9e670220); }
            else if (operation == NativeFastOperation.BoolConstant) Load(2, constants[instruction.A].Boolean ? 1UL : 0UL);
            else
            {
                Scalar(instruction.A, operation is NativeFastOperation.BoolCopy or NativeFastOperation.BoolNot ? NativeKind.Bool : NativeKind.Number, false);
                if (operation == NativeFastOperation.BoolNot) Word(0x52000042);
                else if (operation == NativeFastOperation.NumberNegate) Word(0x1e614000);
                else if (operation is >= NativeFastOperation.NumberAdd)
                {
                    Scalar(instruction.B, NativeKind.Number, true);
                    if (operation < NativeFastOperation.NumberEqual)
                        Word(operation switch
                        {
                            NativeFastOperation.NumberAdd => 0x1e612800u,
                            NativeFastOperation.NumberSubtract => 0x1e613800u,
                            NativeFastOperation.NumberMultiply => 0x1e610800u,
                            _ => 0x1e611800u
                        });
                    else
                    {
                        Word(0x1e612000);
                        Word(operation switch
                        {
                            NativeFastOperation.NumberEqual => 0x1a9f17e2u,
                            NativeFastOperation.NumberNotEqual => 0x1a9f07e2u,
                            NativeFastOperation.NumberLess => 0x1a9f57e2u,
                            NativeFastOperation.NumberLessEqual => 0x1a9f87e2u,
                            NativeFastOperation.NumberGreater => 0x1a9fd7e2u,
                            _ => 0x1a9fb7e2u
                        });
                    }
                }
            }
            RawWrite(instruction.Result, operation is NativeFastOperation.BoolConstant or NativeFastOperation.BoolCopy
                or NativeFastOperation.BoolNot or >= NativeFastOperation.NumberEqual ? NativeKind.Bool : NativeKind.Number, pc);
            return true;
        }

        private void RawWrite(int slot, NativeKind kind, int pc)
        {
            Word(0xf9400670); Load(15, (ulong)checked(layout.Scalars[slot] * 32)); Word(0x8b0f020f);
            Word(0xb9401df1);
            int notPrivate = code.Count; Word(0);
            int pending = code.Count; Word(0);
            Word(0xf94001f1); Word(kind == NativeKind.Bool ? 0x39000222u : 0xfd000220u);
            Word(0xf94005f1); Load(14, 1); Word(0x3900022e);
            Word(0xf94009f1); int noTag = code.Count; Word(0);
            Load(14, (ulong)kind); Word(0x3900022e);
            code[noTag] = 0xb4000011 | (((uint)(code.Count - noTag) & 0x7ffff) << 5);
            Advance(pc);
            code[notPrivate] = 0x36000011 | (((uint)(code.Count - notPrivate) & 0x3fff) << 5);
            code[pending] = 0x37080011 | (((uint)(code.Count - pending) & 0x3fff) << 5);
        }



        private void Advance(int pc)
        {
            Word(0xf9400e70); Load(15, (ulong)checked(layout.Hits[pc] * 8)); Word(0x8b0f0210);
            Word(0xf9400211); Word(0x91000631); Word(0xf9000211);
            Word(0xb9402271); Word(0x51000631); Word(0xb9002271); Word(0x7100023f); Branch(pc + function.Instructions[pc].Span, 4);
            Load(17, (ulong)(pc + function.Instructions[pc].Span)); Word(0xb9001271); Word(0x12800080); Branch(-1, 0);
        }

        private void Scalar(int slot, NativeKind kind, bool second)
        {
            Word(0xf9400670u); Load(15, (ulong)checked(layout.Scalars[slot] * 32)); Word(0x8b0f020f);
            Word(0xb9401df1); Word(0x721f023f); Branch(-4, 4);
            Word(0xf94005f1); Word(0xf100023f); Branch(-4, 1);
            Word(0x39400231); Word(0x7100023f); Branch(-4, 1);
            if (!function.HasKind(slot, kind))
            {
                Word(0xf94009f1);
                int noTag = code.Count; Word(0);
                Word(0x39400231); Word(0x7100023f | ((uint)kind << 10)); Branch(-4, 4);
                int ready = code.Count; Word(0);
                code[noTag] = 0xb4000011 | (((uint)(code.Count - noTag) & 0x7ffff) << 5);
                Word(0xb94019f1); Word(0x7100023f | ((uint)kind << 10)); Branch(-4, 4);
                code[ready] = 0x14000000 | ((uint)(code.Count - ready) & 0x03ffffff);
            }
            Word(0xf94001f1);
            if (kind == NativeKind.Bool) Word(0x39400222);
            else Word(second ? 0xfd400221u : 0xfd400220u);
        }
        private void Branch(int target, int kind) { jumps.Add((code.Count, target, kind)); Word(0); }
        private void Load(int register, ulong value)
        {
            Word(0xd2800000 | ((uint)(value & 0xffff) << 5) | (uint)register);
            for (int half = 1; half < 4; half++)
                if (((value >> (half * 16)) & 0xffff) != 0)
                    Word(0xf2800000 | ((uint)half << 21) | ((uint)((value >> (half * 16)) & 0xffff) << 5) | (uint)register);
        }
        private void Word(uint word) => code.Add(word);
    }
}
