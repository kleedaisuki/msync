using System.Buffers.Binary;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.ReadonlyNumberFold.Operation;

namespace Msync.Runtime;

internal static partial class NativeScopeRecurrence
{
    private sealed class Arm64(ReadonlyNumberFold plan, int[] registers)
    {
        private readonly List<uint> code = [];
        private readonly List<(int Patch, long Bits)> literals = [];

        internal byte[]? Emit()
        {
            // LDR Dn, [Xbase,#offset] scales an unsigned imm12 by eight.
            if (plan.Fields.Length > 4096 || plan.InputSlots.Length > 4096) return null;
            int stride = plan.Fields.Length * sizeof(double);
            code.Add(0xd503245f); // BTI c
            int empty = code.Count;
            code.Add(0x34000001); // cbz w1, finish
            if (stride > 4095) code.Add(0xd2800003 | ((uint)stride << 5)); // movz x3, stride
            int body = code.Count;
            for (int i = 0; i < registers.Length; i++)
                if (registers[i] > 0) Step(i);
            Move(0, registers[plan.Result]);
            code.Add(stride <= 4095 ? 0x91000000 | ((uint)stride << 10) : 0x8b030000);
            code.Add(0x71000421); // subs w1,w1,#1
            int repeat = code.Count;
            code.Add(0x54000001); // b.ne body
            int finish = code.Count;
            code.Add(0xd65f03c0);
            if (code.Count % 2 != 0) code.Add(0);

            var locations = new Dictionary<long, int>();
            foreach (var (patch, bits) in literals)
            {
                if (!locations.TryGetValue(bits, out int offset))
                {
                    offset = code.Count;
                    locations.Add(bits, offset);
                    code.Add((uint)bits);
                    code.Add((uint)(bits >> 32));
                }
                if (!Relative(patch, offset)) return null;
            }
            if (!Relative(empty, finish) || !Relative(repeat, body)) return null;
            byte[] bytes = new byte[code.Count * sizeof(uint)];
            for (int i = 0; i < code.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), code[i]);
            return bytes;
        }

        private void Step(int i)
        {
            int output = registers[i];
            var step = plan.Steps[i];
            switch (step.Op)
            {
                case Op.Field:
                    Load(output, 0, step.Left);
                    break;
                case Op.Input:
                    Load(output, 2, step.Left);
                    break;
                case Op.Constant:
                    literals.Add((code.Count, BitConverter.DoubleToInt64Bits(step.Constant)));
                    code.Add(0x5c000000 | (uint)output); // ldr Dd, literal
                    break;
                case Op.Negate:
                    code.Add(0x1e614000 | (uint)(registers[step.Left] << 5 | output));
                    break;
                default:
                    uint opcode = step.Op switch
                    {
                        Op.Add => 0x1e602800u,
                        Op.Subtract => 0x1e603800u,
                        Op.Multiply => 0x1e600800u,
                        Op.Divide => 0x1e601800u,
                        _ => throw new InvalidOperationException("Unencodable row operation."),
                    };
                    code.Add(opcode | (uint)(registers[step.Right] << 16 | registers[step.Left] << 5 | output));
                    break;
            }
        }

        private void Move(int destination, int source)
        {
            if (destination != source) code.Add(0x1e604000 | (uint)(source << 5 | destination));
        }

        private void Load(int fp, int address, int slot) =>
            code.Add(0xfd400000 | (uint)(slot << 10 | address << 5 | fp));

        private bool Relative(int patch, int target)
        {
            int delta = target - patch;
            if (delta is < -0x40000 or >= 0x40000) return false;
            code[patch] |= ((uint)delta & 0x7ffff) << 5;
            return true;
        }
    }
}
