using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.NativeWorkerOp;

namespace Msync.Runtime;

internal static partial class NativeWorker
{
    private sealed class X64Emitter
    {
        private const int Accumulator = 0, Scratch = 10, IndexCache = 11;
        private readonly NativeWorkerPlan plan;
        private readonly bool counted;
        private readonly int rows;
        private readonly int slots;
        private readonly int resume;
        private readonly int fuel;
        private readonly bool usesCl;
        private readonly int[] cache;
        private readonly bool[] written;
        private readonly List<byte> code = [];
        private readonly List<(int Patch, int Label)> fixups = [];
        private readonly List<int> labels;
        private readonly SortedDictionary<int, int> backedges = [];
        private readonly int finish;

        internal X64Emitter(NativeWorkerPlan plan, bool counted, bool windows)
        {
            this.plan = plan;
            this.counted = counted;
            usesCl = plan.Instructions.Any(op => op.ReadsWord32Count);
            rows = windows ? usesCl ? 8 : 1 : 7; // r8 or rcx / rdi
            slots = windows ? 2 : 6;  // rdx / rsi
            resume = windows ? usesCl ? 1 : 8 : 2; // ecx or r8d / edx
            fuel = windows ? 9 : usesCl ? 8 : 1;   // r9d / r8d or ecx
            (cache, written) = AllocateRegisters(plan);
            finish = plan.Instructions.Length;
            labels = new(new int[finish + 1]);
            for (int pc = 0; pc < finish; pc++)
            {
                var op = plan.Instructions[pc];
                if (op.Op is Op.Jump or Op.BranchIfTrue or Op.BranchIfFalse && op.Data <= pc
                    && !backedges.ContainsKey(op.Data))
                {
                    backedges.Add(op.Data, labels.Count);
                    labels.Add(0);
                }
            }
        }

        internal byte[] Emit()
        {
            Bytes(0xf3, 0x0f, 0x1e, 0xfa); // ENDBR64
            if (usesCl)
            {
                // Reserve CL for the baseline x86 variable-shift encoding.
                if (rows == 8) Bytes(0x49, 0x87, 0xc8); // xchg r8,rcx
                else IndexRegister(fuel, 1);
            }
            TransferCache(load: true);
            foreach (int target in backedges.Keys)
            {
                if (target == 0) continue;
                Rex(wide: false, rm: resume);
                Bytes(0x81, Reg(7, resume)); // cmp resume, imm32
                Integer(target);
                Jump(target, 0x84);
            }

            for (int pc = 0; pc < finish; pc++)
            {
                labels[pc] = code.Count;
                if (counted)
                {
                    Rex(wide: true, rm: slots);
                    Bytes(0xff, Disp(0, slots)); // inc qword [slots + counter]
                    Integer(SlotOffset(plan.Slots.Length + pc));
                }
                Instruction(pc, plan.Instructions[pc]);
            }

            foreach (var (target, label) in backedges)
            {
                labels[label] = code.Count;
                Rex(wide: false, rm: fuel);
                Bytes(0xff, Reg(1, fuel)); // dec fuel
                Jump(target, 0x85); // jnz
                Exit(target);
            }

            labels[finish] = code.Count;
            TransferCache(load: false);
            Bytes(0xc3);
            Span<byte> bytes = CollectionsMarshal.AsSpan(code);
            foreach (var (patch, label) in fixups)
                BinaryPrimitives.WriteInt32LittleEndian(bytes[patch..], labels[label] - patch - 4);
            return code.ToArray();
        }

        private static (int[] Registers, bool[] Written) AllocateRegisters(NativeWorkerPlan plan)
        {
            int[] uses = new int[plan.Slots.Length];
            bool[] written = new bool[uses.Length];
            written[plan.Result] = true;
            foreach (var op in plan.Instructions)
            {
                if (op.Op is Op.Jump or Op.Nop) continue;
                bool store = op.Op is Op.StoreIndex or Op.StoreNumber;
                if (op.Op is not (Op.IndexConstant or Op.NumberConstant or Op.BoolConstant or Op.Word32Constant)) uses[op.Left]++;
                if (store || op.ReadsWord32Right || op.Op is >= Op.IndexAdd and <= Op.NumberDivide
                    or Op.NumberEqual or Op.NumberNotEqual or Op.NumberLess or Op.NumberGreaterEqual or Op.NumberLessNaNLast)
                    uses[op.Right]++;
                if (store || op.Op is Op.BranchIfTrue or Op.BranchIfFalse or Op.Return) continue;
                uses[op.Destination]++;
                written[op.Destination] = true;
            }
            int[] result = new int[uses.Length];
            Array.Fill(result, -1);
            int fp = 2; // XMM0 is scratch; XMM2–5 are volatile on both ABIs.
            bool index = false;
            foreach (int slot in Enumerable.Range(0, uses.Length).OrderByDescending(i => uses[i]))
            {
                if (uses[slot] < 2) break;
                if (plan.Slots[slot] == NativeWorkerType.Number)
                {
                    if (fp <= 5) result[slot] = fp++;
                }
                else if (!index)
                {
                    result[slot] = IndexCache;
                    index = true;
                }
            }
            return (result, written);
        }

        private void TransferCache(bool load)
        {
            for (int i = 0; i < cache.Length; i++)
            {
                if (cache[i] < 0) continue;
                if (load ? backedges.Count == 0 && !plan.Parameters.Contains(i) : !written[i]) continue;
                if (IsNumber(i)) NumberMemory(load ? (byte)0x10 : (byte)0x11, cache[i], i);
                else IndexMemory(load ? (byte)0x8b : (byte)0x89, cache[i], i, wide: !load);
            }
        }

        private void Instruction(int pc, in NativeWorkerInstruction op)
        {
            switch (op.Op)
            {
                case Op.Nop:
                    break;
                case Op.Move:
                    Move(op.Destination, op.Left);
                    break;
                case Op.IndexConstant:
                case Op.BoolConstant:
                case Op.Word32Constant:
                    Bytes(0xb8);
                    Integer(op.Data);
                    StoreIndex(op.Destination);
                    break;
                case Op.NumberConstant:
                    Bytes(0x48, 0xb8); // movabs rax, bits
                    long bits = BitConverter.DoubleToInt64Bits(op.Constant);
                    Integer((int)bits);
                    Integer((int)(bits >> 32));
                    MoveBitsToNumber();
                    StoreNumber(op.Destination);
                    break;
                case Op.Word32ToNumber:
                    LoadIndex(op.Left);
                    Bytes(0xf2, 0x48, 0x0f, 0x2a, 0xc0); // cvtsi2sd xmm0,rax (zero-extended)
                    StoreNumber(op.Destination);
                    break;
                case Op.Word32Intrinsic:
                    Word32(op);
                    break;
                case Op.LoadIndex:
                case Op.LoadNumber:
                    Row(op, store: false);
                    break;
                case Op.StoreIndex:
                case Op.StoreNumber:
                    Row(op, store: true);
                    break;
                case Op.NumberNegate:
                    LoadNumber(op.Left);
                    Bytes(0x66, 0x48, 0x0f, 0x7e, 0xc0); // movq rax, xmm0
                    Bytes(0x48, 0x0f, 0xba, 0xf8, 63);   // btc rax, 63
                    MoveBitsToNumber();
                    StoreNumber(op.Destination);
                    break;
                case Op.BoolNot:
                    LoadIndex(op.Left);
                    Bytes(0x83, 0xf0, 1); // xor eax, 1
                    StoreIndex(op.Destination);
                    break;
                case Op.BranchIfFalse:
                case Op.BranchIfTrue:
                    LoadIndex(op.Left);
                    Bytes(0x85, 0xc0); // test eax, eax
                    Jump(Target(pc, op.Data), op.Op == Op.BranchIfTrue ? (byte)0x85 : (byte)0x84);
                    break;
                case Op.Jump:
                    if (op.Data != pc + 1) Jump(Target(pc, op.Data));
                    break;
                case Op.Return:
                    Move(plan.Result, op.Left);
                    Exit(-1);
                    break;
                case >= Op.IndexAdd and <= Op.IndexGreaterEqual:
                    IndexBinary(op);
                    break;
                default:
                    NumberBinary(op);
                    break;
            }
        }

        private void Row(in NativeWorkerInstruction op, bool store)
        {
            LoadIndex(op.Left, Scratch);
            int shift = BitOperations.TrailingZeroCount((uint)plan.RowStride);
            int scale = Math.Min(shift, 3);
            int stride = plan.RowStride >> scale;
            if (stride != 1)
            {
                if (BitOperations.IsPow2((uint)stride))
                {
                    Rex(wide: true, rm: Scratch);
                    Bytes(0xc1, Reg(4, Scratch), (byte)(shift - scale)); // shl
                }
                else
                {
                    Rex(wide: true, reg: Scratch, rm: Scratch);
                    if (stride <= sbyte.MaxValue) Bytes(0x6b, Reg(Scratch, Scratch), (byte)stride);
                    else
                    {
                        Bytes(0x69, Reg(Scratch, Scratch)); // imul scratch, scratch, imm32
                        Integer(stride);
                    }
                }
            }
            bool number = op.Op is Op.LoadNumber or Op.StoreNumber;
            if (store)
            {
                if (number) LoadNumber(op.Right);
                else LoadIndex(op.Right);
            }
            if (number) Bytes(0xf2);
            Rex(wide: false, rm: rows, index: Scratch);
            if (number) Bytes(0x0f, store ? (byte)0x11 : (byte)0x10);
            else Bytes(store ? (byte)0x89 : (byte)0x8b);
            Bytes(0x84, (byte)(scale << 6 | (Scratch & 7) << 3 | rows & 7));
            Integer(op.Data);
            if (store) return;
            if (number) StoreNumber(op.Destination);
            else StoreIndex(op.Destination);
        }

        private void IndexBinary(in NativeWorkerInstruction op)
        {
            LoadIndex(op.Left);
            LoadIndex(op.Right, Scratch);
            byte opcode = op.Op switch
            {
                Op.IndexAdd => 0x03,
                Op.IndexSubtract => 0x2b,
                _ => 0x3b, // cmp eax, scratch
            };
            Rex(wide: false, rm: Scratch);
            Bytes(opcode, Reg(Accumulator, Scratch));
            if (op.Op is not (Op.IndexAdd or Op.IndexSubtract))
                SetResult(op.Op switch
                {
                    Op.IndexEqual => 0x94,
                    Op.IndexNotEqual => 0x95,
                    Op.IndexLess => 0x92,
                    Op.IndexGreaterEqual => 0x93,
                    _ => throw new InvalidOperationException("Invalid index comparison."),
                });
            StoreIndex(op.Destination);
        }

        private void Word32(in NativeWorkerInstruction op)
        {
            LoadIndex(op.Left);
            switch (op.Intrinsic)
            {
                case NumericIntrinsic.And or NumericIntrinsic.Or or NumericIntrinsic.Xor:
                    LoadIndex(op.Right, Scratch);
                    Rex(wide: false, rm: Scratch);
                    Bytes(op.Intrinsic switch
                    {
                        NumericIntrinsic.And => (byte)0x23,
                        NumericIntrinsic.Or => (byte)0x0b,
                        _ => (byte)0x33,
                    }, Reg(Accumulator, Scratch));
                    break;
                case NumericIntrinsic.Not:
                    Bytes(0xf7, 0xd0);
                    break;
                case NumericIntrinsic.ShiftLeft or NumericIntrinsic.ShiftRight or NumericIntrinsic.ShiftRightSigned
                    or NumericIntrinsic.RotateLeft or NumericIntrinsic.RotateRight:
                    LoadIndex(op.Right, 1);
                    int opcode = op.Intrinsic switch
                    {
                        NumericIntrinsic.ShiftLeft => 4,
                        NumericIntrinsic.ShiftRight => 5,
                        NumericIntrinsic.ShiftRightSigned => 7,
                        NumericIntrinsic.RotateLeft => 0,
                        _ => 1,
                    };
                    Bytes(0xd3, Reg(opcode, Accumulator));
                    break;
                case NumericIntrinsic.PopCount:
                    PopCount();
                    break;
                case NumericIntrinsic.LeadingZeroCount or NumericIntrinsic.TrailingZeroCount:
                    ZeroCount(leading: op.Intrinsic == NumericIntrinsic.LeadingZeroCount);
                    break;
                case NumericIntrinsic.ReverseBytes:
                    Bytes(0x0f, 0xc8); // bswap eax
                    break;
                default:
                    throw new InvalidOperationException("Invalid Word32 operation.");
            }
            StoreIndex(op.Destination);
        }

        private void ZeroCount(bool leading)
        {
            Bytes(0x0f, leading ? (byte)0xbd : (byte)0xbc, 0xc0); // bsr or bsf eax,eax
            Bytes(0x41, 0xba); // mov r10d, imm32 (preserves ZF)
            Integer(leading ? 63 : 32);
            Bytes(0x41, 0x0f, 0x44, 0xc2); // cmovz eax,r10d
            if (leading) Bytes(0x83, 0xf0, 31); // 31 xor bit index, or 32 for zero
        }

        private void PopCount()
        {
            // SWAR works on baseline x64, including machines without POPCNT.
            IndexRegister(Scratch, Accumulator);
            Bytes(0x41, 0xd1, 0xea, 0x41, 0x81, 0xe2); // shr r10d,1; and r10d,mask
            Integer(0x55555555);
            Bytes(0x41, 0x2b, 0xc2); // sub eax,r10d
            IndexRegister(Scratch, Accumulator);
            Bytes(0x25); // and eax,mask
            Integer(0x33333333);
            Bytes(0x41, 0xc1, 0xea, 2, 0x41, 0x81, 0xe2);
            Integer(0x33333333);
            Bytes(0x41, 0x03, 0xc2); // add eax,r10d
            IndexRegister(Scratch, Accumulator);
            Bytes(0x41, 0xc1, 0xea, 4, 0x41, 0x03, 0xc2, 0x25);
            Integer(0x0f0f0f0f);
            Bytes(0x69, 0xc0); // imul eax,eax,imm32
            Integer(0x01010101);
            Bytes(0xc1, 0xe8, 24);
        }

        private void NumberBinary(in NativeWorkerInstruction op)
        {
            LoadNumber(op.Left);
            if (op.Op is >= Op.NumberAdd and <= Op.NumberDivide)
            {
                NumberOperand(0xf2, op.Op switch
                {
                    Op.NumberAdd => 0x58,
                    Op.NumberSubtract => 0x5c,
                    Op.NumberMultiply => 0x59,
                    _ => 0x5e,
                }, op.Right);
                StoreNumber(op.Destination);
                return;
            }

            NumberOperand(0x66, 0x2e, op.Right); // ucomisd
            byte condition = op.Op switch
            {
                Op.NumberEqual => 0x94,
                Op.NumberNotEqual => 0x95,
                Op.NumberLess or Op.NumberLessNaNLast => 0x92,
                Op.NumberGreaterEqual => 0x93,
                _ => throw new InvalidOperationException("Invalid Number comparison."),
            };
            Bytes(0x0f, condition, 0xc0);
            // Unordered sets PF, CF and ZF. Above-or-equal already rejects it.
            if (op.Op != Op.NumberGreaterEqual)
            {
                // Preserve CF from the two-operand test, but reject only a
                // NaN on the left when the selected order puts NaN last.
                if (op.Op == Op.NumberLessNaNLast) Bytes(0x66, 0x0f, 0x2e, 0xc0); // ucomisd xmm0,xmm0
                bool notEqual = op.Op == Op.NumberNotEqual;
                Rex(wide: false, rm: Scratch);
                Bytes(0x0f, notEqual ? (byte)0x9a : (byte)0x9b, Reg(0, Scratch));
                Rex(wide: false, rm: Scratch);
                Bytes(notEqual ? (byte)0x0a : (byte)0x22, Reg(Accumulator, Scratch));
            }
            Bytes(0x0f, 0xb6, 0xc0); // movzx eax, al
            StoreIndex(op.Destination);
        }

        private void SetResult(byte condition) => Bytes(0x0f, condition, 0xc0, 0x0f, 0xb6, 0xc0);
        private void MoveBitsToNumber() => Bytes(0x66, 0x48, 0x0f, 0x6e, 0xc0);
        private bool IsNumber(int slot) => plan.Slots[slot] == NativeWorkerType.Number;

        private void Move(int destination, int source)
        {
            if (destination == source) return;
            if (IsNumber(source)) { LoadNumber(source); StoreNumber(destination); }
            else { LoadIndex(source); StoreIndex(destination); }
        }

        private void LoadIndex(int slot, int destination = Accumulator)
        {
            if (cache[slot] >= 0) IndexRegister(destination, cache[slot]);
            else IndexMemory(0x8b, destination, slot, wide: false);
        }

        private void StoreIndex(int slot)
        {
            if (cache[slot] >= 0) IndexRegister(cache[slot], Accumulator);
            else IndexMemory(0x89, Accumulator, slot, wide: true);
        }

        private void IndexRegister(int destination, int source)
        {
            Rex(wide: false, reg: destination, rm: source);
            Bytes(0x8b, Reg(destination, source));
        }

        private void IndexMemory(byte opcode, int reg, int slot, bool wide)
        {
            Rex(wide, reg, slots);
            Bytes(opcode, Disp(reg, slots));
            Integer(SlotOffset(slot));
        }

        private void LoadNumber(int slot) => NumberOperand(0xf2, 0x10, slot);

        private void StoreNumber(int slot)
        {
            if (cache[slot] >= 0) Bytes(0xf2, 0x0f, 0x10, Reg(cache[slot], 0));
            else NumberMemory(0x11, 0, slot);
        }

        private void NumberOperand(byte prefix, byte opcode, int slot)
        {
            if (cache[slot] >= 0) Bytes(prefix, 0x0f, opcode, Reg(0, cache[slot]));
            else
            {
                Bytes(prefix, 0x0f, opcode, Disp(0, slots));
                Integer(SlotOffset(slot));
            }
        }

        private void NumberMemory(byte opcode, int reg, int slot)
        {
            Bytes(0xf2, 0x0f, opcode, Disp(reg, slots));
            Integer(SlotOffset(slot));
        }

        private int Target(int pc, int target) => target <= pc ? backedges[target] : target;

        private void Exit(int result)
        {
            Bytes(0xb8);
            Integer(result);
            Jump(finish);
        }

        private void Jump(int target, byte condition = 0)
        {
            if (condition == 0) Bytes(0xe9);
            else Bytes(0x0f, condition);
            fixups.Add((code.Count, target));
            Integer(0);
        }

        private static int SlotOffset(int slot) => checked(slot * sizeof(ulong));
        private static byte Reg(int reg, int rm) => (byte)(0xc0 | (reg & 7) << 3 | rm & 7);
        private static byte Disp(int reg, int rm) => (byte)(0x80 | (reg & 7) << 3 | rm & 7);

        private void Rex(bool wide, int reg = 0, int rm = 0, int index = 0)
        {
            byte value = (byte)(0x40 | (wide ? 8 : 0) | (reg & 8) >> 1 | (index & 8) >> 2 | rm >> 3);
            if (value != 0x40) Bytes(value);
        }

        private void Integer(int value) => Bytes((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
        private void Bytes(params ReadOnlySpan<byte> bytes) => code.AddRange(bytes);
    }
}
