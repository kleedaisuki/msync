using System.Buffers.Binary;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirMachineImage
{
    private sealed partial class X64
    {
        private const int A = 0, AddressScratch = 11, Frame = 12, Context = 13, Counters = 14;
        private readonly HpirMachineImage image;
        private readonly HpirProgram program;
        private readonly int first;
        private readonly int[][] functionLabels;
        private readonly int[] tableOffsets;
        private readonly bool observe;
        private readonly bool windows = OperatingSystem.IsWindows();
        private readonly List<byte> code = [];
        private readonly List<int> exits = [];
        private readonly List<(int Patch, int Result)> boundaries = [];

        internal X64(HpirMachineImage image, int first, int count)
        {
            this.image = image;
            program = image.Program;
            this.first = first;
            functionLabels = new int[count][];
            tableOffsets = new int[count];
            observe = image.CounterCount != 0;
            // Callee-saved on both host ABIs across the binary64 remainder helper.
            Emit(0x41, 0x54, 0x41, 0x55, 0x41, 0x56);
            if (windows)
                Emit(0x49, 0x89, 0xcc, 0x49, 0x89, 0xd5, 0x48, 0x83, 0xec, 0x20);
            else Emit(0x49, 0x89, 0xfc, 0x49, 0x89, 0xf5);
            if (observe) Memory(0x8b, Counters, Context, State.CounterOffset, wide: true);
            if (windows) Emit(0x41, 0xff, 0xe0);
            else Emit(0xff, 0xe2);
        }

        internal int Length => code.Count;

        internal int TableOffset(int id) => tableOffsets[id - first];
        private bool Local(int id) => (uint)(id - first) < (uint)functionLabels.Length;

        internal void Function(int id, HpirFunction function, int firstCounter)
        {
            HpirInstruction[] instructions = function.Instructions;
            var labels = new int[instructions.Length];
            functionLabels[id - first] = labels;
            MarkBranchTargets(instructions, labels);
            var branches = new List<(int Patch, int To)>();
            for (int pc = 0; pc < labels.Length; pc++)
            {
                labels[pc] = Length;
                HpirInstruction op = instructions[pc];
                int counter = observe ? checked(firstCounter + pc * CountersPerInstruction) : 0;
                if (op.Op == HpirOp.Call)
                {
                    HpirCall call = function.Calls[op.A];
                    if (call.Target >= 0) GuestCall(pc, op, call, counter);
                    else if (InlineService(program.Imports[~call.Target], call.Arguments, op.D))
                    {
                        ++InlineServiceSites;
                        Count(counter);
                    }
                    else Exit(pc);
                    continue;
                }
                if (op.Op == HpirOp.Return)
                {
                    GuestReturn(pc, op, function.Result, counter);
                    continue;
                }
                Binary binary = SelectBinary(op.Op);
                if (binary.Kind != BinaryKind.None)
                {
                    pc = ScalarRun(instructions, labels, pc, binary, firstCounter);
                    continue;
                }
                Count(counter);
                switch (op.Op)
                {
                    case HpirOp.Jump: Edge(pc, op.A, counter + 1, branches, fallthrough: true); break;
                    case HpirOp.Branch: Branch(pc, op, counter, branches); break;
                    default: Scalar(function, pc, op); break;
                }
            }
            foreach ((int patch, int target) in branches) Patch(patch, labels[target]);
        }

        internal byte[] Finish()
        {
            foreach ((int patch, int result) in boundaries)
            {
                Patch(patch, Length);
                Exit(result);
            }
            int epilogue = Length;
            if (windows) Emit(0x48, 0x83, 0xc4, 0x20);
            Emit(0x41, 0x5e, 0x41, 0x5d, 0x41, 0x5c, 0xc3);
            foreach (int patch in exits) Patch(patch, epilogue);
            foreach ((int patch, int target) in guestCalls) Patch(patch, functionLabels[target - first][0]);
            while ((Length & 3) != 0) Emit(0x90);
            for (int i = 0; i < functionLabels.Length; i++)
            {
                int offset = Length;
                tableOffsets[i] = offset;
                foreach (int label in functionLabels[i]) Dword(label - offset);
            }
            return code.ToArray();
        }

        private void Branch(int pc, HpirInstruction op, int counter, List<(int Patch, int To)> branches)
        {
            (int register, int offset) = Operand(op.A);
            Memory(0x80, 7, register, offset);
            Emit(0);
            int whenFalse = Conditional(0x84);
            Edge(pc, op.B, counter + 1, branches, fallthrough: false);
            Patch(whenFalse, Length);
            Edge(pc, op.D, counter + 2, branches, fallthrough: true);
        }

        private void Edge(int pc, int target, int counter, List<(int Patch, int To)> branches, bool fallthrough)
        {
            if (target <= pc)
            {
                Memory(0x83, 7, Context, State.CancelOffset);
                Emit(0);
                boundaries.Add((Conditional(0x85), ~pc));
            }
            Count(counter);
            if (!fallthrough || target != pc + 1) branches.Add((Jump(), target));
        }

        private void Count(int counter)
        {
            if (observe) Memory(0xff, 0, Counters, checked(counter * sizeof(ulong)), wide: true);
        }

        private void Exit(int result)
        {
            Emit(0xb8);
            Dword(result);
            exits.Add(Jump());
        }

        private (int Register, int Offset) Operand(int operand)
        {
            if (operand >= 0) return (Frame, operand);
            Memory(0x8b, AddressScratch, Frame, ~operand, wide: true);
            return (AddressScratch, 0);
        }

        private void Memory(int opcode, int value, int address, int offset, bool wide = false,
            byte prefix = 0, bool extended = false)
        {
            if (prefix != 0) Emit(prefix);
            int rex = 0x40 | (wide ? 8 : 0) | ((value & 8) >> 1) | (address >> 3);
            if (rex != 0x40) Emit((byte)rex);
            if (extended) Emit(0x0f);
            int baseRegister = address & 7;
            int mode = offset == 0 && baseRegister != 5 ? 0
                : offset is >= sbyte.MinValue and <= sbyte.MaxValue ? 0x40 : 0x80;
            Emit((byte)opcode, (byte)(mode | ((value & 7) << 3) | baseRegister));
            if (baseRegister == 4) Emit(0x24);
            if (mode == 0x40) Emit(unchecked((byte)offset));
            else if (mode == 0x80) Dword(offset);
        }

        private int Jump() { Emit(0xe9); return Rel32(); }
        private int Conditional(byte opcode) { Emit(0x0f, opcode); return Rel32(); }
        private int Rel32() { int offset = Length; Dword(0); return offset; }

        private void Patch(int patch, int target) => BinaryPrimitives.WriteInt32LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(code).Slice(patch, 4), target - patch - 4);

        private void Dword(int value)
        {
            code.Add((byte)value);
            code.Add((byte)(value >> 8));
            code.Add((byte)(value >> 16));
            code.Add((byte)(value >> 24));
        }

        private void Qword(long value) { Dword((int)value); Dword((int)(value >> 32)); }

        private void Emit(byte first) => code.Add(first);
        private void Emit(byte first, byte second) { code.Add(first); code.Add(second); }
        private void Emit(params byte[] bytes) => code.AddRange(bytes);
    }
}
