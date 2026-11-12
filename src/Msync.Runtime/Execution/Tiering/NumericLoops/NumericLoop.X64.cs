using System.Buffers.Binary;
using Msync.Core;

namespace Msync.Runtime;

internal sealed partial class NumericLoop
{
    private bool CanEmitNative()
    {
        foreach (var op in code)
            if (op is { Kind: Kind.Binary, Code: OpCode.Remainder }) return false;
        return true;
    }

    private sealed class X64Emitter
    {
        private readonly NumericLoop loop;
        private readonly bool counted;
        private readonly int registers;
        private readonly int fuel;
        private readonly int counts;
        private readonly int resume;
        private readonly List<byte> bytes = [];
        private readonly List<(int Offset, int Label)> fixups = [];
        private readonly int[] cache;
        private readonly List<int> labels;
        private readonly Dictionary<int, int> exits = [];
        private readonly SortedDictionary<int, int> backedges = [];
        private readonly int finish;

        internal X64Emitter(NumericLoop loop, bool counted, bool windows)
        {
            this.loop = loop;
            this.counted = counted;
            registers = windows ? 1 : 7; // rcx / rdi
            fuel = windows ? 2 : 6; // edx / esi
            counts = windows ? 8 : 2; // r8 / rdx
            resume = windows ? 9 : 1; // r9d / ecx
            cache = AllocateRegisters(loop);
            finish = loop.code.Length;
            labels = new(new int[finish + 1]);
            for (int pc = 0; pc < loop.code.Length; pc++)
            {
                var op = loop.code[pc];
                if (op.Kind is Kind.Branch or Kind.Jump && op.Target >= 0 && op.Target <= pc
                    && !backedges.ContainsKey(op.Target))
                    backedges.Add(op.Target, NewLabel());
            }
        }

        internal byte[] Emit()
        {
            Bytes(0xF3, 0x0F, 0x1E, 0xFA); // ENDBR64, a NOP when IBT is disabled.
            for (int i = 0; i < loop.registerCount; i++)
                if (cache[i] >= 0) Memory(0x10, cache[i], i);

            // Entry zero falls through. A resume index can only come from a
            // fuel edge in this same native version.
            foreach (int target in backedges.Keys)
                if (target != 0) ResumeAt(target);

            for (int i = 0; i < loop.code.Length; i++)
            {
                Mark(i);
                if (counted) Count(i);
                Instruction(i, loop.code[i]);
            }

            foreach (var (target, label) in backedges)
            {
                Mark(label);
                Bytes(0xFF, Register(1, fuel)); // dec fuel
                Jump(0x85, target); // jnz target
                Result(target);
            }
            foreach (var (result, label) in exits)
            {
                Mark(label);
                Result(result);
            }

            Mark(finish);
            for (int i = 0; i < loop.registerCount; i++)
                if (cache[i] >= 0 && (i >= loop.StackBase || i < loop.locals.Length && (loop.written & (1UL << i)) != 0))
                    Memory(0x11, cache[i], i);
            Bytes(0xC3);
            return Link();
        }

        private static int[] AllocateRegisters(NumericLoop loop)
        {
            int[] uses = new int[loop.registerCount];
            foreach (var op in loop.code)
            {
                if (op.Kind is Kind.Nop or Kind.Jump) continue;
                uses[op.Left]++;
                if (op.Kind is Kind.Binary or Kind.Branch) uses[op.Right]++;
                if (op.Kind != Kind.Branch) uses[op.Output]++;
            }
            int[] mapping = new int[uses.Length];
            Array.Fill(mapping, -1);
            int next = 2; // XMM0–1 are scratch; XMM2–5 are volatile on both ABIs.
            foreach (int slot in Enumerable.Range(0, uses.Length).OrderByDescending(i => uses[i]).Take(4))
                if (uses[slot] != 0) mapping[slot] = next++;
            return mapping;
        }

        private void Instruction(int index, in Operation op)
        {
            if (op.Kind == Kind.Nop) return;
            if (op.Kind == Kind.Jump)
            {
                Jump(0, Target(index, op.Target));
                return;
            }
            Load(op.Left);
            switch (op.Kind)
            {
                case Kind.Move: Store(op.Output); break;
                case Kind.Negate:
                    Bytes(0x48, 0xB8); // movabs rax, sign mask
                    Int32(0);
                    Int32(int.MinValue);
                    Bytes(0x66, 0x48, 0x0F, 0x6E, 0xC8); // movq xmm1, rax
                    Bytes(0x66, 0x0F, 0x57, 0xC1); // xorpd xmm0, xmm1
                    Store(op.Output);
                    break;
                case Kind.Binary:
                    Operand(0xF2, op.Code switch
                    {
                        OpCode.Add => 0x58, OpCode.Multiply => 0x59,
                        OpCode.Subtract => 0x5C, _ => 0x5E
                    }, op.Right);
                    Store(op.Output);
                    break;
                case Kind.Branch: Branch(index, op); break;
            }
        }

        private void Branch(int index, in Operation op)
        {
            Operand(0x66, 0x2E, op.Right); // ucomisd
            bool whenTrue = op.Output != 0;
            int target = Target(index, op.Target);
            byte condition = op.Code switch
            {
                OpCode.Equal => 0x84, OpCode.NotEqual => 0x85,
                OpCode.Less => 0x82, OpCode.LessEqual => 0x86,
                OpCode.Greater => 0x87, _ => 0x83,
            };
            // Unordered sets CF, ZF and PF. JA/JAE already reject it.
            if (op.Code is not (OpCode.Greater or OpCode.GreaterEqual))
                Jump(0x8A, whenTrue == (op.Code == OpCode.NotEqual) ? target : index + 1);
            Jump(whenTrue ? condition : (byte)(condition ^ 1), target);
        }

        private void Load(int slot)
        {
            int xmm = cache[slot];
            if (xmm >= 0) Bytes(0x66, 0x0F, 0x28, Register(0, xmm));
            else Memory(0x10, 0, slot);
        }

        private void Store(int slot)
        {
            int xmm = cache[slot];
            if (xmm >= 0) Bytes(0x66, 0x0F, 0x28, Register(xmm, 0));
            else Memory(0x11, 0, slot);
        }

        private void Operand(byte prefix, byte opcode, int slot)
        {
            int xmm = cache[slot];
            if (xmm >= 0) Bytes(prefix, 0x0F, opcode, Register(0, xmm));
            else
            {
                Bytes(prefix, 0x0F, opcode, Displaced(0, registers));
                Int32(slot * sizeof(double));
            }
        }

        private void Memory(byte opcode, int xmm, int slot)
        {
            Bytes(0xF2, 0x0F, opcode, Displaced(xmm, registers));
            Int32(slot * sizeof(double));
        }

        private void Count(int index)
        {
            Bytes((byte)(0x48 | (counts >> 3)), 0xFF, Displaced(0, counts & 7)); // inc qword [counts+disp]
            Int32(index * sizeof(ulong));
        }

        private void ResumeAt(int target)
        {
            if (resume >= 8) Bytes(0x41);
            Bytes(0x81, Register(7, resume & 7)); // cmp resume, target
            Int32(target);
            Jump(0x84, target);
        }

        private int Target(int source, int result)
        {
            if (result >= 0) return result <= source ? backedges[result] : result;
            if (exits.TryGetValue(result, out int label)) return label;
            label = NewLabel();
            exits.Add(result, label);
            return label;
        }

        private int NewLabel()
        {
            int label = labels.Count;
            labels.Add(0);
            return label;
        }

        private void Result(int value)
        {
            Bytes(0xB8);
            Int32(value);
            Jump(0, finish);
        }

        private void Jump(byte condition, int label)
        {
            if (condition == 0) Bytes(0xE9);
            else Bytes(0x0F, condition);
            fixups.Add((bytes.Count, label));
            Int32(0);
        }

        private byte[] Link()
        {
            byte[] result = bytes.ToArray();
            foreach (var (offset, label) in fixups)
                BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), labels[label] - offset - sizeof(int));
            return result;
        }

        private void Mark(int label) => labels[label] = bytes.Count;
        private void Bytes(params ReadOnlySpan<byte> values) => bytes.AddRange(values);
        private void Int32(int value)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)(value >> 16));
            bytes.Add((byte)(value >> 24));
        }

        private static byte Register(int field, int operand) => (byte)(0xC0 | field << 3 | operand);
        private static byte Displaced(int field, int operand) => (byte)(0x80 | field << 3 | operand);
    }
}
