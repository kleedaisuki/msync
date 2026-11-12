using System.Buffers.Binary;
using Msync.Core;

namespace Msync.Runtime;

internal sealed partial class NumericLoop
{
    // AAPCS64 and Windows ARM64 share the leaf integer ABI and v16-v31 are volatile on both.
    private sealed class Arm64Emitter(NumericLoop loop, bool counted)
    {
        private readonly int[] cache = AllocateRegisters(loop);
        private readonly List<uint> words = [];
        private readonly List<(int Offset, int Label, bool Conditional)> branches = [];
        private readonly List<int> labels = new(new int[loop.code.Length + 1]);
        private readonly Dictionary<int, int> exits = [];
        private readonly SortedDictionary<int, int> backedges = [];
        private const uint Eq = 0, Ne = 1, Mi = 4, Ls = 9, Ge = 10, Gt = 12;
        private int Finish => loop.code.Length;

        internal byte[] Emit()
        {
            for (int pc = 0; pc < loop.code.Length; pc++)
            {
                var op = loop.code[pc];
                if (op.Kind is Kind.Branch or Kind.Jump && op.Target >= 0 && op.Target <= pc
                    && !backedges.ContainsKey(op.Target)) backedges.Add(op.Target, NewLabel());
            }

            Word(0xd503245f); // BTI c / HINT #34
            for (int i = 0; i < cache.Length; i++)
                if (cache[i] >= 0) LoadDouble(cache[i], i);

            // x0: scratch, w1: fuel, x2: counters, w3: native resume.
            foreach (int target in backedges.Keys)
            {
                if (target == 0) continue;
                Immediate(6, target);
                Word(0x6b06007f); // cmp w3,w6
                Jump(target, Eq);
            }

            for (int pc = 0; pc < loop.code.Length; pc++)
            {
                Mark(pc);
                if (counted) Count(pc);
                Instruction(pc, loop.code[pc]);
            }
            foreach (var (target, label) in backedges)
            {
                Mark(label);
                Word(0x71000421); // subs w1,w1,#1
                Jump(target, Ne);
                Result(target);
            }
            foreach (var (value, label) in exits)
            {
                Mark(label);
                Result(value);
            }

            Mark(Finish);
            for (int i = 0; i < cache.Length; i++)
                if (cache[i] >= 0 && (i >= loop.StackBase || i < loop.locals.Length && (loop.written & (1UL << i)) != 0))
                    StoreDouble(cache[i], i);
            Word(0x2a0403e0); // mov w0,w4, preserving x0 until the stores
            Word(0xd65f03c0); // ret
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
            int next = 16;
            foreach (int slot in Enumerable.Range(0, uses.Length).OrderByDescending(i => uses[i]).Take(16))
                if (uses[slot] != 0) mapping[slot] = next++;
            return mapping;
        }

        private void Instruction(int pc, in Operation op)
        {
            if (op.Kind == Kind.Nop) return;
            if (op.Kind == Kind.Jump)
            {
                Jump(Target(pc, op.Target));
                return;
            }

            int left = Source(op.Left, 0);
            switch (op.Kind)
            {
                case Kind.Move:
                    Save(op.Output, left);
                    break;
                case Kind.Negate:
                    int negated = Destination(op.Output);
                    Word(0x1e614000 | (uint)(left << 5 | negated));
                    Save(op.Output, negated);
                    break;
                case Kind.Binary:
                    int right = Source(op.Right, 1), output = Destination(op.Output);
                    Word((op.Code switch
                    {
                        OpCode.Add => 0x1e602800u, OpCode.Subtract => 0x1e603800u,
                        OpCode.Multiply => 0x1e600800u, OpCode.Divide => 0x1e601800u,
                        _ => throw new InvalidOperationException("Unencodable ARM64 arithmetic."),
                    }) | (uint)(right << 16 | left << 5 | output));
                    Save(op.Output, output);
                    break;
                case Kind.Branch:
                    Word(0x1e602000 | (uint)(Source(op.Right, 1) << 16 | left << 5)); // fcmp
                    // FCMP unordered sets C=V=1. These conditions implement IEEE
                    // unordered != only, unlike signed LT/LE.
                    uint condition = op.Code switch
                    {
                        OpCode.Equal => Eq, OpCode.NotEqual => Ne, OpCode.Less => Mi,
                        OpCode.LessEqual => Ls, OpCode.Greater => Gt, OpCode.GreaterEqual => Ge,
                        _ => throw new InvalidOperationException("Unencodable ARM64 comparison."),
                    };
                    Jump(Target(pc, op.Target), op.Output != 0 ? condition : condition ^ 1);
                    break;
            }
        }

        private int Source(int slot, int scratch)
        {
            int fp = cache[slot];
            if (fp >= 0) return fp;
            LoadDouble(scratch, slot);
            return scratch;
        }

        private int Destination(int slot) => Math.Max(cache[slot], 0);

        private void Save(int slot, int source)
        {
            int destination = cache[slot];
            if (destination < 0) StoreDouble(source, slot);
            else if (destination != source) Word(0x1e604000 | (uint)(source << 5 | destination));
        }

        private void LoadDouble(int fp, int slot) => Word(0xfd400000 | (uint)(slot << 10 | fp));
        private void StoreDouble(int fp, int slot) => Word(0xfd000000 | (uint)(slot << 10 | fp));

        private void Count(int pc)
        {
            uint address = (uint)(pc << 10 | 2 << 5 | 5); // [x2,#pc*8], x5
            Word(0xf9400000 | address);
            Word(0x910004a5);
            Word(0xf9000000 | address);
        }

        private void Immediate(uint target, int value)
        {
            Word(0x52800000 | (((uint)value & 0xffff) << 5) | target); // movz w
            if ((uint)value > ushort.MaxValue)
                Word(0x72a00000 | (((uint)value >> 16) << 5) | target); // movk w,#hi,lsl #16
        }

        private int Target(int from, int value)
        {
            if (value >= 0) return value <= from ? backedges[value] : value;
            if (exits.TryGetValue(value, out int label)) return label;
            label = NewLabel();
            exits.Add(value, label);
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
            Immediate(4, value);
            Jump(Finish);
        }

        private void Jump(int label, uint? condition = null)
        {
            branches.Add((words.Count, label, condition is not null));
            Word(condition is { } value ? 0x54000000 | value : 0x14000000);
        }

        private byte[] Link()
        {
            foreach (var (offset, label, conditional) in branches)
            {
                int delta = labels[label] - offset;
                words[offset] |= conditional ? ((uint)delta & 0x7ffff) << 5 : (uint)delta & 0x3ffffff;
            }
            byte[] result = new byte[words.Count * sizeof(uint)];
            for (int i = 0; i < words.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * sizeof(uint)), words[i]);
            return result;
        }

        private void Mark(int label) => labels[label] = words.Count;
        private void Word(uint word) => words.Add(word);
    }
}
