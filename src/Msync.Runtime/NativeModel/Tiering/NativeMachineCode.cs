using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static partial class NativeMachineCode
{
    internal const bool HasDirectPaths = true;
    internal const bool AutoEnabled = true;
    internal static byte[] Emit(NativeExecutionFunction function, NativeFastOperation[] operations,
        Architecture architecture, bool windows, nint bridge, nint number, nint boolean,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout? layout = null, nint commit = 0)
    {
        layout ??= NativeTierLayout.Create(function, operations);
        return architecture switch
        {
            Architecture.X64 => new X64(function, operations, constants, layout, windows, bridge, number, boolean, commit).Emit(),
            Architecture.Arm64 when !windows => new Arm64(function, operations, constants, layout, bridge, number, boolean, commit).Emit(),
            _ => throw new PlatformNotSupportedException("Native guest code generation is not available on this platform.")
        };
    }

    private sealed partial class X64(NativeExecutionFunction function, NativeFastOperation[] operations,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout layout, bool windows, nint bridge, nint number, nint boolean, nint commit)
    {
        private readonly List<byte> code = [];
        private readonly List<(int Offset, int Target)> jumps = [];
        private readonly List<int> guardPcs = [];
        private readonly Dictionary<int, int> guardStubs = [];
        private readonly int[] labels = Enumerable.Repeat(-1, function.Instructions.Length + 1).ToArray();
        private readonly IReadOnlyDictionary<int, NativeRegisterRegion> regions = layout.RegionsByPc;
        private int tableReference, dispatch, exit, guard;
        internal byte[] Emit()
        {
            Bytes(0x41, 0x54); // r12 owns the opaque context across managed bridges.
            if (windows) { Bytes(0x48, 0x83, 0xec, 0x20, 0x49, 0x89, 0xcc, 0x89, 0xd0); }
            else Bytes(0x49, 0x89, 0xfc, 0x89, 0xf0);
            if (layout.StackBytes != 0) { Bytes(0x48, 0x81, 0xec); Int(layout.StackBytes); }
            dispatch = code.Count;
            Bytes(0x41, 0x89, 0x44, 0x24, 0x10);
            Bytes(0x3d); Int(function.Instructions.Length); Jump(0x0f, 0x83, -2);
            Bytes(0x48, 0x8d, 0x15); tableReference = code.Count; Int(0);
            Bytes(0x48, 0x63, 0x04, 0x82, 0x48, 0x01, 0xd0, 0xff, 0xe0);
            for (int pc = 0, next = 0; pc < function.Instructions.Length; pc = next)
            {
                NativeExecutionInstruction instruction = function.Instructions[pc];
                next = pc + instruction.Span;
                labels[pc] = code.Count;
                if (regions.TryGetValue(pc, out NativeRegisterRegion? region))
                {
                    Region(region);
                    next = region.End;
                    continue;
                }
                if (operations[pc] is NativeFastOperation.Jump or NativeFastOperation.Branch)
                {
                    Control(instruction, pc);
                    continue;
                }
                if (operations[pc] is NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn)
                {
                    ReturnValue(instruction, operations[pc], pc);
                    continue;
                }

                if (operations[pc] == NativeFastOperation.Nop)
                {
                    Advance(pc, next, instruction.Span, layout.Hits[pc]);
                    continue;
                }
                if (operations[pc] == NativeFastOperation.Bridge)
                {
                    Arguments(pc);
                    Bytes(0x48, 0xb8); Long(bridge); Bytes(0xff, 0xd0, 0x85, 0xc0);
                    Jump(0x0f, 0x88, -1);
                    Jump(0xe9, -3);
                    continue;
                }
                StoreScalar(instruction, operations[pc], pc, number, boolean);
            }
            labels[^1] = code.Count;
            Bytes(0xb8); Int(-1); Jump(0xe9, -1);
            int[] stubs = new int[guardPcs.Count];
            for (int index = 0; index < guardPcs.Count; index++)
            {
                stubs[index] = code.Count;
                Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(guardPcs[index]);
                Jump(0xe9, -4);
            }
            guard = code.Count; Bytes(0xb8); Int(-3);
            exit = code.Count;
            if (layout.StackBytes != 0) { Bytes(0x48, 0x81, 0xc4); Int(layout.StackBytes); }
            if (windows) Bytes(0x48, 0x83, 0xc4, 0x20);
            Bytes(0x41, 0x5c, 0xc3);
            int table = code.Count;
            for (int pc = 0; pc < labels.Length - 1; pc++)
            {
                if (labels[pc] < 0) labels[pc] = guard;
                Int(labels[pc] - table);
            }
            byte[] result = code.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(tableReference, 4), table - tableReference - 4);
            foreach ((int offset, int target) in jumps)
            {
                int address = target <= -10 ? stubs[-target - 10]
                    : target switch { -1 => exit, -2 => labels[^1], -3 => dispatch, -4 => guard, _ => labels[target] };
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset, 4), address - offset - 4);
            }
            return result;
        }

        private int GuardTarget(int pc)
        {
            if (!guardStubs.TryGetValue(pc, out int id))
            {
                id = guardPcs.Count;
                guardPcs.Add(pc);
                guardStubs[pc] = id;
            }
            return -(10 + id);
        }

        private void ReturnValue(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            bool isBool = operation == NativeFastOperation.BoolReturn;
            Scalar(instruction.A, isBool ? NativeKind.Bool : NativeKind.Number, false, pc);
            Bytes(0x41, 0xc7, 0x44, 0x24, 0x10); Int(pc);
            Bytes(0x41, 0x83, 0x7c, 0x24, 0x14, 0x00, 0x74); int skipHit = code.Count; Bytes(0);
            Bytes(0x4d, 0x8b, 0x54, 0x24, 0x18);
            Bytes(0x49, 0xff, 0x82); Int(layout.Hits[pc] * 8);
            code[skipHit] = (byte)(code.Count - skipHit - 1);
            Bytes(0xb8); Int(isBool ? -9 : -8);
            Jump(0xe9, -1);
        }

        private void StoreScalar(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc, nint number, nint boolean)
        {
            bool boolResult = operation.IsBoolResult();
            NativeKind kind = boolResult ? NativeKind.Bool : NativeKind.Number;
            Calculate(instruction, operation, pc);
            int pending;
            if (layout.TryFixed(instruction.Result, out FixedTierSlot fixedSlot))
            {
                Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 0x01, 0x0f, 0x85); pending = code.Count; Int(0);
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                if (boolResult) Bytes(0x41, 0x88, 0x82);
                else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x82);
                Int(fixedSlot.DataOffset);
                if (!function.HasKind(instruction.Result, kind))
                {
                    Bytes(0x41, 0xc6, 0x82); Int(fixedSlot.ByteOffset); Bytes((byte)kind);
                }
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x30);
                Bytes(0x41, 0xc6, 0x82); Int(fixedSlot.ByteOffset); Bytes(0x01);
                Advance(pc, pc + instruction.Span, instruction.Span, layout.Hits[pc]);
            }
            else
            {
                int offset = checked(layout.Scalars[instruction.Result] * 32);
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
                Bytes(0x4d, 0x8b, 0x9a); Int(offset);
                Bytes(0x4d, 0x85, 0xdb, 0x0f, 0x84); int noData = code.Count; Int(0);
                Bytes(0x41, 0xf6, 0x82); Int(offset + 24); Bytes(0x02);
                Bytes(0x0f, 0x85); pending = code.Count; Int(0);
                if (boolResult) Bytes(0x41, 0x88, 0x03);
                else Bytes(0xf2, 0x41, 0x0f, 0x11, 0x03);
                Bytes(0x4d, 0x8b, 0x9a); Int(offset + 8); Bytes(0x41, 0xc6, 0x03, 0x01);
                if (!function.HasKind(instruction.Result, kind))
                {
                    Bytes(0x4d, 0x8b, 0x9a); Int(offset + 16);
                    Bytes(0x41, 0xc6, 0x03, (byte)kind);
                }
                Advance(pc, pc + instruction.Span, instruction.Span, layout.Hits[pc]);
                PatchLocal(noData);
            }
            PatchLocal(pending);
            if (boolResult)
            {
                if (windows) Bytes(0x41, 0x89, 0xc0);
                else Bytes(0x89, 0xc2);
            }
            else if (windows) Bytes(0x66, 0x0f, 0x28, 0xd0);
            Arguments(pc);
            Bytes(0x48, 0xb8); Long(boolResult ? boolean : number); Bytes(0xff, 0xd0, 0x85, 0xc0);
            Jump(0x0f, 0x88, -1);
            if (instruction.Span > 1) Jump(0xe9, pc + instruction.Span);
        }

        private void Arguments(int pc)
        {
            if (windows) { Bytes(0x4c, 0x89, 0xe1, 0xba); Int(pc); }
            else { Bytes(0x4c, 0x89, 0xe7, 0xbe); Int(pc); }
        }

        private bool Fast(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            if (operation is NativeFastOperation.Bridge or NativeFastOperation.Nop) return false;
            Calculate(instruction, operation, pc);
            bool boolResult = operation.IsBoolResult();
            if (boolResult)
            {
                if (windows) Bytes(0x41, 0x89, 0xc0);
                else Bytes(0x89, 0xc2);
            }
            else if (windows) Bytes(0x66, 0x0f, 0x28, 0xd0);
            return true;
        }

        private void Calculate(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc,
            NativeRegisterRegion? region = null, NativeRegisterOperation selected = default)
        {
            void Operand(int slot, int value, NativeKind kind, bool second)
            {
                if (region is null) Scalar(slot, kind, second, pc);
                else ReadValue(region.Values[value], kind, second);
            }
            bool boolResult = operation.IsBoolResult();
            if (operation is NativeFastOperation.NumberConstant)
            {
                Bytes(0x48, 0xb8); Long((nint)BitConverter.DoubleToInt64Bits(constants[instruction.A].Number));
                Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc0);
            }
            else if (operation == NativeFastOperation.BoolConstant)
            { Bytes(0xb8); Int(constants[instruction.A].Boolean ? 1 : 0); }
            else
            {
                Operand(instruction.A, selected.A, operation.ExpectsBoolInputs() ? NativeKind.Bool : NativeKind.Number, false);
                if (operation == NativeFastOperation.BoolNot) Bytes(0x83, 0xf0, 0x01);
                else if (operation == NativeFastOperation.NumberNegate)
                    Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0, 0x48, 0x0f, 0xba, 0xf8, 0x3f, 0x66, 0x48, 0x0f, 0x6e, 0xc0);
                else if (operation is NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual)
                {
                    Bytes(0x88, 0xc1);
                    Operand(instruction.B, selected.B, NativeKind.Bool, false);
                    Bytes(0x38, 0xc1, 0x0f, (byte)(operation == NativeFastOperation.BoolEqual ? 0x94 : 0x95), 0xc0, 0x0f, 0xb6, 0xc0);
                }
                else if (operation is >= NativeFastOperation.NumberAdd)
                {
                    Operand(instruction.B, selected.B, NativeKind.Number, true);
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
        }

        private void Scalar(int slot, NativeKind kind, bool second, int pc)
        {
            int guardTarget = GuardTarget(pc);
            if (!function.AdmitsKind(slot, kind)) { Jump(0xe9, guardTarget); return; }
            if (layout.TryFixed(slot, out FixedTierSlot fixedSlot))
            {
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x30);
                Bytes(0x41, 0x80, 0xba); Int(fixedSlot.ByteOffset); Bytes(0x00); Jump(0x0f, 0x84, guardTarget);
                Bytes(0x41, 0xf6, 0x44, 0x24, 0x24, 0x01, 0x74); int noPending = code.Count; Bytes(0);
                int offset = checked(fixedSlot.Scalar * 32);
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x08);
                Bytes(0x41, 0xf7, 0x82); Int(offset + 24); Int(2); Jump(0x0f, 0x85, guardTarget);
                code[noPending] = (byte)(code.Count - noPending - 1);
                Bytes(0x4d, 0x8b, 0x54, 0x24, 0x28);
                if (!function.HasKind(slot, kind))
                {
                    if (function.IsUnion(slot))
                    {
                        Bytes(0x41, 0x80, 0xba); Int(fixedSlot.ByteOffset); Bytes((byte)kind); Jump(0x0f, 0x85, guardTarget);
                    }
                    else { Jump(0xe9, guardTarget); return; }
                }
                if (kind == NativeKind.Bool) { Bytes(0x41, 0x0f, 0xb6, 0x82); Int(fixedSlot.DataOffset); }
                else { Bytes(0xf2, 0x41, 0x0f, 0x10, second ? (byte)0x8a : (byte)0x82); Int(fixedSlot.DataOffset); }
                return;
            }
            int scalarOffset = checked(layout.Scalars[slot] * 32);
            Bytes(0x4d, 0x8b, 0x54, 0x24, (byte)0x08);
            Bytes(0x41, 0xf7, 0x82); Int(scalarOffset + 24); Int(2); Jump(0x0f, 0x85, guardTarget);
            Bytes(0x4d, 0x8b, 0x9a); Int(scalarOffset + 8);
            Bytes(0x41, 0x80, 0x3b, 0x00); Jump(0x0f, 0x84, guardTarget);
            if (!function.HasKind(slot, kind))
            {
                if (function.IsUnion(slot))
                {
                    Bytes(0x4d, 0x8b, 0x9a); Int(scalarOffset + 16);
                    Bytes(0x41, 0x80, 0x3b, (byte)kind); Jump(0x0f, 0x85, guardTarget);
                }
                else Jump(0xe9, guardTarget);
            }
            Bytes(0x4d, 0x8b, 0x9a); Int(scalarOffset);
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

    private sealed partial class Arm64(NativeExecutionFunction function, NativeFastOperation[] operations,
        IReadOnlyList<NativeConstant> constants, NativeTierLayout layout, nint bridge, nint number, nint boolean, nint commit)
    {
        private readonly List<uint> code = [];
        private readonly List<(int At, int Target, int Kind)> jumps = [];
        private readonly List<int> guardPcs = [];
        private readonly Dictionary<int, int> guardStubs = [];
        private readonly int[] labels = Enumerable.Repeat(-1, function.Instructions.Length + 1).ToArray();
        private readonly IReadOnlyDictionary<int, NativeRegisterRegion> regions = layout.RegionsByPc;
        private int tableAdr, exit, guard, dispatch;
        internal byte[] Emit()
        {
            Word(0xa9bf7bf3); Word(0xaa0003f3); Word(0x2a0103e0);
            if (layout.StackBytes != 0) { Load(17, (ulong)layout.StackBytes); Word(0xcb3163ff); }
            dispatch = code.Count;
            Word(0xb9001260);
            Load(17, (ulong)function.Instructions.Length); Word(0x6b11001f); Branch(-2, 2);
            tableAdr = code.Count; Word(0);
            Word(0xb8a05a11); Word(0x8b110210); Word(0xd61f0200);
            for (int pc = 0, next = 0; pc < function.Instructions.Length; pc = next)
            {
                NativeExecutionInstruction instruction = function.Instructions[pc];
                next = pc + instruction.Span;
                labels[pc] = code.Count;
                if (regions.TryGetValue(pc, out NativeRegisterRegion? region))
                {
                    Region(region);
                    next = region.End;
                    continue;
                }
                NativeFastOperation operation = operations[pc];
                if (operation is NativeFastOperation.Jump or NativeFastOperation.Branch)
                {
                    Control(instruction, pc);
                    continue;
                }
                if (operation is NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn)
                {
                    ReturnValue(instruction, operation, pc);
                    continue;
                }

                if (operation == NativeFastOperation.Nop)
                {
                    Advance(next, instruction.Span, layout.Hits[pc]);
                    continue;
                }
                if (operation == NativeFastOperation.Bridge)
                {
                    Word(0xaa1303e0); Load(1, (ulong)pc); Load(16, (ulong)bridge); Word(0xd63f0200); Word(0x7100001f);
                    Branch(-1, 3);
                    Branch(-3, 0);
                    continue;
                }
                StoreScalar(instruction, operation, pc, number, boolean);
            }
            labels[^1] = code.Count; Word(0x12800000); Branch(-1, 0);
            int[] stubs = new int[guardPcs.Count];
            for (int index = 0; index < guardPcs.Count; index++)
            {
                stubs[index] = code.Count;
                Load(17, (ulong)guardPcs[index]); Word(0xb9001271);
                Branch(-4, 0);
            }
            guard = code.Count; Word(0x12800040);
            exit = code.Count;
            if (layout.StackBytes != 0) { Load(17, (ulong)layout.StackBytes); Word(0x8b3163ff); }
            Word(0xa8c17bf3); Word(0xd65f03c0);
            int table = code.Count;
            for (int pc = 0; pc < labels.Length - 1; pc++)
            {
                if (labels[pc] < 0) labels[pc] = guard;
                Word(unchecked((uint)((labels[pc] - table) * 4)));
            }
            int delta = (table - tableAdr) * 4;
            if (delta >= 1 << 20) throw new InvalidOperationException("Guest native function exceeds the branch relocation range.");
            code[tableAdr] = 0x10000010 | ((uint)(delta & 3) << 29) | ((uint)(delta >> 2) << 5);
            foreach ((int at, int target, int kind) in jumps)
            {
                int address = target <= -10 ? stubs[-target - 10]
                    : target switch { -1 => exit, -2 => labels[^1], -3 => dispatch, -4 => guard, _ => labels[target] };
                int offset = address - at;
                code[at] = kind switch
                {
                    0 => 0x14000000 | ((uint)offset & 0x03ffffff),
                    1 => 0x54000000 | (((uint)offset & 0x7ffff) << 5),
                    2 => 0x54000002 | (((uint)offset & 0x7ffff) << 5),
                    3 => 0x5400000b | (((uint)offset & 0x7ffff) << 5),
                    5 => 0x5400000c | (((uint)offset & 0x7ffff) << 5),
                    _ => 0x54000001 | (((uint)offset & 0x7ffff) << 5)
                };
            }
            byte[] result = new byte[code.Count * 4];
            for (int index = 0; index < code.Count; index++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index * 4, 4), code[index]);
            return result;
        }

        private int GuardTarget(int pc)
        {
            if (!guardStubs.TryGetValue(pc, out int id))
            {
                id = guardPcs.Count;
                guardPcs.Add(pc);
                guardStubs[pc] = id;
            }
            return -(10 + id);
        }

        private void ReturnValue(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            bool isBool = operation == NativeFastOperation.BoolReturn;
            Scalar(instruction.A, isBool ? NativeKind.Bool : NativeKind.Number, false, pc);
            Load(17, (ulong)pc); Word(0xb9001271);
            Word(0xb9401670); int skipHit = code.Count; Word(0);
            Word(0xf9400e70);
            Load(17, (ulong)(layout.Hits[pc] * 8));
            Word(0x8b110210);
            Word(0xf9400211);
            Word(0x91000631);
            Word(0xf9000211);
            code[skipHit] = 0x34000000u | 16u | (((uint)(code.Count - skipHit) & 0x7ffffu) << 5);
            Word(isBool ? 0x12800100u : 0x128000e0u);
            Branch(-1, 0);
        }

        private void StoreScalar(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc, nint number, nint boolean)
        {
            bool boolResult = operation.IsBoolResult();
            NativeKind kind = boolResult ? NativeKind.Bool : NativeKind.Number;
            Calculate(instruction, operation, pc);
            if (layout.TryFixed(instruction.Result, out FixedTierSlot fixedSlot))
            {
                Word(0xb940266e); int pending = code.Count; Word(0);
                Word(0xf9401671);
                if (boolResult) StoreFrameByte(2, fixedSlot.DataOffset);
                else StoreFrameDouble(fixedSlot.DataOffset);
                if (!function.HasKind(instruction.Result, kind))
                {
                    if ((uint)fixedSlot.DataOffset >= (boolResult ? 4096u : 32768u) || (!boolResult && (fixedSlot.DataOffset & 7) != 0))
                        Word(0xf9401671);
                    Load(14, (ulong)kind);
                    StoreFrameByte(14, fixedSlot.ByteOffset);
                }
                Word(0xf9401a71); Load(14, 1); StoreFrameByte(14, fixedSlot.ByteOffset);
                Advance(pc + instruction.Span, instruction.Span, layout.Hits[pc]);
                code[pending] = 0x3700000eu | (((uint)(code.Count - pending) & 0x3fffu) << 5);
            }
            else
            {
                Word(0xf9400670); // ldr x16, [x19, #8] (Slots)
                Load(15, (ulong)checked(layout.Scalars[instruction.Result] * 32));
                Word(0x8b0f0210); // add x16, x16, x15
                Word(0xf9400211); // ldr x17, [x16] (Data)
                int noData = code.Count; Word(0); // cbz x17, fallback
                Word(0xb9401a0e); // ldr w14, [x16, #24] (Flags)
                int pending = code.Count; Word(0); // tbnz w14, #1, fallback
                Word(boolResult ? 0x39000222u : 0xfd000220u); // strb w2 / str d0, [x17]
                Word(0xf9400611); Load(14, 1); Word(0x3900022e); // ldr x17, [x16, #8]; mov w14, #1; strb w14, [x17]
                if (!function.HasKind(instruction.Result, kind))
                {
                    Word(0xf9400a11); Load(14, (ulong)kind); Word(0x3900022e);
                }
                Advance(pc + instruction.Span, instruction.Span, layout.Hits[pc]);
                code[noData] = 0xb4000000u | 17u | (((uint)(code.Count - noData) & 0x7ffffu) << 5);
                code[pending] = 0x37080000u | 14u | (((uint)(code.Count - pending) & 0x3fffu) << 5);
            }
            Word(0xaa1303e0); Load(1, (ulong)pc); Load(16, (ulong)(boolResult ? boolean : number)); Word(0xd63f0200); Word(0x7100001f);
            Branch(-1, 3);
        }

        private bool Fast(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc)
        {
            if (operation is NativeFastOperation.Bridge or NativeFastOperation.Nop) return false;
            Calculate(instruction, operation, pc);
            return true;
        }

        private void Calculate(NativeExecutionInstruction instruction, NativeFastOperation operation, int pc,
            NativeRegisterRegion? region = null, NativeRegisterOperation selected = default)
        {
            void Operand(int slot, int value, NativeKind kind, bool second)
            {
                if (region is null) Scalar(slot, kind, second, pc);
                else ReadValue(region.Values[value], kind, second);
            }
            if (operation == NativeFastOperation.NumberConstant)
            { Load(17, BitConverter.DoubleToUInt64Bits(constants[instruction.A].Number)); Word(0x9e670220); }
            else if (operation == NativeFastOperation.BoolConstant) Load(2, constants[instruction.A].Boolean ? 1UL : 0UL);
            else
            {
                Operand(instruction.A, selected.A, operation.ExpectsBoolInputs() ? NativeKind.Bool : NativeKind.Number, false);
                if (operation == NativeFastOperation.BoolNot) Word(0x52000042);
                else if (operation == NativeFastOperation.NumberNegate) Word(0x1e614000);
                else if (operation is NativeFastOperation.BoolEqual or NativeFastOperation.BoolNotEqual)
                {
                    Word(0x2a0203e3u);
                    Operand(instruction.B, selected.B, NativeKind.Bool, false);
                    Word(0x6b02007fu);
                    Word(operation == NativeFastOperation.BoolEqual ? 0x1a9f17e2u : 0x1a9f07e2u);
                }
                else if (operation is >= NativeFastOperation.NumberAdd)
                {
                    Operand(instruction.B, selected.B, NativeKind.Number, true);
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
        }

        private void Scalar(int slot, NativeKind kind, bool second, int pc)
        {
            int guardTarget = GuardTarget(pc);
            if (!function.AdmitsKind(slot, kind)) { Branch(guardTarget, 0); return; }
            if (layout.TryFixed(slot, out FixedTierSlot fixedSlot))
            {
                Word(0xf9401a71); LoadFrameByte(17, fixedSlot.ByteOffset); Word(0x7100023f); Branch(guardTarget, 1);
                Word(0xb940266e); int noPending = code.Count; Word(0);
                Word(0xf9400670u); Load(15, (ulong)checked(fixedSlot.Scalar * 32)); Word(0x8b0f020f);
                Word(0xb94019f1); Word(0x721f023f); Branch(guardTarget, 4);
                code[noPending] = 0x3600000eu | (((uint)(code.Count - noPending) & 0x3fffu) << 5);
                Word(0xf9401671);
                if (!function.HasKind(slot, kind))
                {
                    if (function.IsUnion(slot))
                    {
                        LoadFrameByte(17, fixedSlot.ByteOffset);
                        Word(0x7100023f | ((uint)kind << 10)); Branch(guardTarget, 4);
                        if ((uint)fixedSlot.ByteOffset >= 4096) Word(0xf9401671);
                    }
                    else { Branch(guardTarget, 0); return; }
                }
                if (kind == NativeKind.Bool) LoadFrameByte(2, fixedSlot.DataOffset);
                else LoadFrameDouble(second, fixedSlot.DataOffset);
                return;
            }
            Word(0xf9400670u); Load(15, (ulong)checked(layout.Scalars[slot] * 32)); Word(0x8b0f020f);
            Word(0xb94019f1); Word(0x721f023f); Branch(guardTarget, 4);
            Word(0xf94005f1);
            Word(0x39400231); Word(0x7100023f); Branch(guardTarget, 1);
            if (!function.HasKind(slot, kind))
            {
                if (function.IsUnion(slot))
                {
                    Word(0xf94009f1);
                    Word(0x39400231); Word(0x7100023f | ((uint)kind << 10)); Branch(guardTarget, 4);
                }
                else Branch(guardTarget, 0);
            }
            Word(0xf94001f1);
            if (kind == NativeKind.Bool) Word(0x39400222);
            else Word(second ? 0xfd400221u : 0xfd400220u);
        }

        private void LoadFrameByte(uint register, int offset)
        {
            if ((uint)offset < 4096) Word(0x39400220u | ((uint)offset << 10) | register);
            else { Load(15, (ulong)offset); Word(0x8b0f0231); Word(0x39400220u | register); }
        }

        private void StoreFrameByte(uint register, int offset)
        {
            if ((uint)offset < 4096) Word(0x39000220u | ((uint)offset << 10) | register);
            else { Load(15, (ulong)offset); Word(0x8b0f0231); Word(0x39000220u | register); }
        }

        private void LoadFrameDouble(bool second, int offset)
        {
            uint rt = second ? 1u : 0u;
            if ((uint)offset < 32768 && (offset & 7) == 0) Word(0xfd400220u | (((uint)offset >> 3) << 10) | rt);
            else { Load(15, (ulong)offset); Word(0x8b0f0231); Word(0xfd400220u | rt); }
        }

        private void StoreFrameDouble(int offset)
        {
            if ((uint)offset < 32768 && (offset & 7) == 0) Word(0xfd000220u | (((uint)offset >> 3) << 10));
            else { Load(15, (ulong)offset); Word(0x8b0f0231); Word(0xfd000220u); }
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
