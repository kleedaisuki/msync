using System.Buffers.Binary;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.ReadonlyNumberFold.Operation;

namespace Msync.Runtime;

internal static partial class NativeScopeRecurrence
{
    private sealed class X64(ReadonlyNumberFold plan, int[] registers, bool windows)
    {
        private readonly List<byte> code = [];
        private readonly List<(int Patch, long Bits, bool Sign)> literals = [];
        private int Rows => windows ? 1 : 7; // rcx / rdi
        private int Count => windows ? 2 : 6; // edx / esi
        private int Inputs => windows ? 8 : 2; // r8 / rdx

        internal byte[] Emit()
        {
            Bytes(0xf3, 0x0f, 0x1e, 0xfa); // ENDBR64
            if (windows) Move(0, 3); // fourth positional argument in xmm3
            Bytes(0x85, (byte)(0xc0 | Count << 3 | Count));
            int empty = Branch(0x84);
            int body = code.Count;
            for (int i = 0; i < registers.Length; i++)
                if (registers[i] > 0) Step(i);
            Move(0, registers[plan.Result]);
            Bytes(0x48, 0x81, (byte)(0xc0 | Rows));
            Integer(checked(plan.Fields.Length * sizeof(double)));
            Bytes(0xff, (byte)(0xc8 | Count)); // dec count
            int repeat = Branch(0x85);
            int finish = code.Count;
            code.Add(0xc3);

            var locations = new Dictionary<(long, bool), int>();
            foreach (var (patch, bits, sign) in literals)
            {
                if (!locations.TryGetValue((bits, sign), out int offset))
                {
                    int alignment = sign ? 16 : 8;
                    while (code.Count % alignment != 0) code.Add(0);
                    offset = code.Count;
                    locations.Add((bits, sign), offset);
                    Integer((int)bits);
                    Integer((int)(bits >> 32));
                    if (sign)
                    {
                        Integer(0);
                        Integer(0);
                    }
                }
                Relative(patch, offset);
            }
            Relative(empty, finish);
            Relative(repeat, body);
            return code.ToArray();
        }

        private void Step(int i)
        {
            int output = registers[i];
            var step = plan.Steps[i];
            switch (step.Op)
            {
                case Op.Field: Load(output, Rows, step.Left); break;
                case Op.Input: Load(output, Inputs, step.Left); break;
                case Op.Constant: Literal(0xf2, 0x10, output, BitConverter.DoubleToInt64Bits(step.Constant)); break;
                case Op.Negate:
                    Move(output, registers[step.Left]);
                    Literal(0x66, 0x57, output, long.MinValue, sign: true); // XORPD with the low sign bit
                    break;
                default:
                    Move(output, registers[step.Left]);
                    byte opcode = step.Op switch
                    {
                        Op.Add => 0x58,
                        Op.Subtract => 0x5c,
                        Op.Multiply => 0x59,
                        Op.Divide => 0x5e,
                        _ => throw new InvalidOperationException("Unencodable row operation."),
                    };
                    Bytes(0xf2, 0x0f, opcode, (byte)(0xc0 | output << 3 | registers[step.Right]));
                    break;
            }
        }

        private void Move(int destination, int source)
        {
            if (destination != source)
                Bytes(0x66, 0x0f, 0x28, (byte)(0xc0 | destination << 3 | source));
        }

        private void Load(int fp, int address, int slot)
        {
            code.Add(0xf2);
            if (address >= 8) code.Add(0x41);
            Bytes(0x0f, 0x10, (byte)(0x80 | fp << 3 | (address & 7)));
            Integer(checked(slot * sizeof(double)));
        }

        private void Literal(byte prefix, byte opcode, int fp, long bits, bool sign = false)
        {
            Bytes(prefix, 0x0f, opcode, (byte)(0x05 | fp << 3));
            literals.Add((code.Count, bits, sign));
            Integer(0);
        }

        private int Branch(byte condition)
        {
            Bytes(0x0f, condition);
            int patch = code.Count;
            Integer(0);
            return patch;
        }

        private void Relative(int patch, int target) =>
            BinaryPrimitives.WriteInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(code)[patch..], target - patch - 4);

        private void Integer(int value)
        {
            code.Add((byte)value);
            code.Add((byte)(value >> 8));
            code.Add((byte)(value >> 16));
            code.Add((byte)(value >> 24));
        }

        private void Bytes(params ReadOnlySpan<byte> bytes) => code.AddRange(bytes);
    }
}
