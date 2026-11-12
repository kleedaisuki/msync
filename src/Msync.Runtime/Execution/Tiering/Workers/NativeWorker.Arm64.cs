using System.Buffers.Binary;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.NativeWorkerOp;

namespace Msync.Runtime;

internal static partial class NativeWorker
{
    private sealed class Arm64Emitter(NativeWorkerPlan plan, bool counted)
    {
        private readonly List<uint> code = [];
        private readonly List<(int Patch, int Target, bool Wide)> fixups = [];
        private readonly int[] labels = new int[plan.Instructions.Length];

        internal byte[]? Emit()
        {
            if (plan.Slots.Length + (counted ? plan.Instructions.Length : 0) > 4096) return null;
            Word(0xd503245f); // BTI c
            if (plan.RowStride != 0) Constant(7, (uint)plan.RowStride);
            EmitResume();
            for (int pc = 0; pc < plan.Instructions.Length; pc++)
            {
                labels[pc] = code.Count;
                if (counted) Count(pc);
                EmitInstruction(pc, plan.Instructions[pc]);
            }
            foreach (var (patch, target, wide) in fixups)
                if (!Patch(patch, labels[target], wide)) return null;
            byte[] bytes = new byte[code.Count * sizeof(uint)];
            for (int i = 0; i < code.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), code[i]);
            return bytes;
        }

        private void EmitResume()
        {
            int[] targets = ResumeTargets(plan);
            int first = targets.Length != 0 && targets[0] == 0 ? 1 : 0;
            if (targets.Length == first) return;
            if (targets.Length - first == 1)
            {
                Branch(0x35000002, targets[first]); // cbnz w2, only nonzero continuation
                return;
            }
            Branch(0x34000002, 0); // cbz w2, entry
            foreach (int target in targets.AsSpan(first, targets.Length - first - 1))
            {
                Constant(4, (uint)target);
                Word(0x6b04005f); // cmp w2, w4
                Branch(0x54000000, target); // b.eq
            }
            Branch(0x14000000, targets[^1], wide: true);
        }

        private void Count(int pc)
        {
            int slot = plan.Slots.Length + pc;
            Load(4, slot);
            Word(0x91000484); // add x4,x4,#1
            Store(4, slot);
        }

        private void EmitInstruction(int pc, NativeWorkerInstruction op)
        {
            switch (op.Op)
            {
                case Op.Nop:
                    break;
                case Op.Move:
                    Load(4, op.Left);
                    Store(4, op.Destination);
                    break;
                case Op.IndexConstant:
                case Op.BoolConstant:
                case Op.Word32Constant:
                case Op.NumberConstant:
                    Constant(4, op.Op == Op.NumberConstant ? (ulong)BitConverter.DoubleToInt64Bits(op.Constant) : (uint)op.Data);
                    Store(4, op.Destination);
                    break;
                case Op.Word32ToNumber:
                    Load(4, op.Left);
                    Word(0x1e630080); // ucvtf d0,w4
                    Word(0xfd000020 | (uint)op.Destination << 10);
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
                case Op.BranchIfFalse:
                case Op.BranchIfTrue:
                    Load(4, op.Left);
                    int skip = code.Count;
                    Word(op.Op == Op.BranchIfTrue ? 0x34000004u : 0x35000004u);
                    Edge(pc, op.Data);
                    Patch(skip, code.Count, wide: false);
                    break;
                case Op.Jump:
                    Edge(pc, op.Data);
                    break;
                case Op.Return:
                    Load(4, op.Left);
                    Store(4, plan.Result);
                    Word(0x12800000); // movn w0,#0 (complete)
                    Ret();
                    break;
                default:
                    Primitive(op);
                    break;
            }
        }

        private void Row(NativeWorkerInstruction op, bool store)
        {
            Load(4, op.Left);
            Word(0x9b070084); // madd x4,x4,x7,x0
            bool number = op.Op is Op.LoadNumber or Op.StoreNumber;
            int width = number ? 8 : 4;
            int register = store ? 5 : 4;
            if (store) Load(register, op.Right);
            uint memory = number ? (store ? 0xf9000000u : 0xf9400000u)
                : (store ? 0xb9000000u : 0xb9400000u);
            if (op.Data % width == 0 && op.Data / width < 4096)
                Word(memory | (uint)(op.Data / width << 10 | 4 << 5 | register));
            else
            {
                Constant(6, (uint)op.Data);
                Word(0x8b060084); // add x4,x4,x6
                Word(memory | (4u << 5) | (uint)register);
            }
            if (!store) Store(4, op.Destination);
        }

        private void Primitive(NativeWorkerInstruction op)
        {
            switch (op.Op)
            {
                case Op.NumberAdd:
                case Op.NumberSubtract:
                case Op.NumberMultiply:
                case Op.NumberDivide:
                    LoadNumber(0, op.Left);
                    LoadNumber(1, op.Right);
                    Word(op.Op switch
                    {
                        Op.NumberAdd => 0x1e612800u,
                        Op.NumberSubtract => 0x1e613800u,
                        Op.NumberMultiply => 0x1e610800u,
                        _ => 0x1e611800u,
                    });
                    Word(0xfd000020 | (uint)op.Destination << 10); // str d0,[x1,#slot]
                    return;
                case Op.NumberNegate:
                    Load(4, op.Left);
                    Constant(6, 1ul << 63);
                    Word(0xca060084); // eor x4,x4,x6
                    break;
                case Op.BoolNot:
                    Load(4, op.Left);
                    Word(0x52000084); // eor w4,w4,#1
                    break;
                case Op.IndexAdd:
                case Op.IndexSubtract:
                    Load(4, op.Left);
                    Load(5, op.Right);
                    Word(op.Op == Op.IndexAdd ? 0x0b050084u : 0x4b050084u);
                    break;
                default:
                    Compare(op);
                    break;
            }
            Store(4, op.Destination);
        }

        private void Word32(NativeWorkerInstruction op)
        {
            Load(4, op.Left);
            if (op.ReadsWord32Right) Load(5, op.Right);
            if (op.Intrinsic == NumericIntrinsic.RotateLeft) Word(0x4b0503e5); // neg w5,w5
            if (op.Intrinsic == NumericIntrinsic.TrailingZeroCount) Word(0x5ac00084); // rbit w4,w4
            if (op.Intrinsic == NumericIntrinsic.PopCount)
            {
                Word(0x1e270080); // fmov s0,w4 (zeroes other lanes)
                Word(0x0e205800); // cnt v0.8b,v0.8b
                Word(0x0e31b800); // addv b0,v0.8b
                Word(0x0e013c04); // umov w4,v0.b[0]
            }
            else Word(op.Intrinsic switch
            {
                NumericIntrinsic.And => 0x0a050084, // and w4,w4,w5
                NumericIntrinsic.Or => 0x2a050084,
                NumericIntrinsic.Xor => 0x4a050084,
                NumericIntrinsic.Not => 0x2a2403e4, // mvn w4,w4
                NumericIntrinsic.ShiftLeft => 0x1ac52084,
                NumericIntrinsic.ShiftRight => 0x1ac52484,
                NumericIntrinsic.ShiftRightSigned => 0x1ac52884,
                NumericIntrinsic.RotateLeft or NumericIntrinsic.RotateRight => 0x1ac52c84, // rorv w4,w4,w5
                NumericIntrinsic.LeadingZeroCount or NumericIntrinsic.TrailingZeroCount => 0x5ac01084, // clz w4,w4
                NumericIntrinsic.ReverseBytes => 0x5ac00884, // rev w4,w4
                _ => throw new InvalidOperationException("Invalid Word32 operation."),
            });
            Store(4, op.Destination);
        }

        private void Compare(NativeWorkerInstruction op)
        {
            bool number = plan.Slots[op.Left] == NativeWorkerType.Number;
            if (number)
            {
                LoadNumber(0, op.Left);
                LoadNumber(1, op.Right);
                Word(0x1e612000); // fcmp d0,d1
                if (op.Op == Op.NumberLessNaNLast)
                {
                    SetCondition(4); // mi: ordered less
                    int ordered = code.Count;
                    Word(0x54000007); // b.vc: skip the unordered case
                    Word(0x1e602000); // fcmp d0,d0
                    SetCondition(0); // eq: a is not NaN, so only b is NaN
                    Patch(ordered, code.Count, wide: false);
                    return;
                }
            }
            else
            {
                Load(4, op.Left);
                Load(5, op.Right);
                Word(0x6b05009f); // cmp w4,w5
            }
            // Unordered sets C and V, not N or Z.
            int condition = op.Op switch
            {
                Op.NumberEqual or Op.IndexEqual => 0, // eq
                Op.NumberNotEqual or Op.IndexNotEqual => 1, // ne
                Op.NumberLess => 4, // mi
                Op.NumberGreaterEqual => 10, // ge
                Op.IndexLess => 3, // lo
                Op.IndexGreaterEqual => 2, // hs
                _ => throw new InvalidOperationException("Invalid native comparison."),
            };
            SetCondition(condition);
        }

        private void SetCondition(int condition) =>
            Word(0x1a9f07e4 | (uint)((condition ^ 1) << 12)); // cset w4, condition

        private void Edge(int pc, int target)
        {
            if (target > pc)
            {
                Branch(0x14000000, target, wide: true);
                return;
            }
            Word(0x71000463); // subs w3,w3,#1
            Branch(0x54000001, target); // b.ne
            Constant(0, (uint)target);
            Ret();
        }

        private void Branch(uint instruction, int target, bool wide = false)
        {
            fixups.Add((code.Count, target, wide));
            Word(instruction);
        }

        private bool Patch(int patch, int target, bool wide)
        {
            int delta = target - patch;
            int half = wide ? 0x2000000 : 0x40000;
            if (delta < -half || delta >= half) return false;
            code[patch] |= ((uint)delta & (uint)(2 * half - 1)) << (wide ? 0 : 5);
            return true;
        }

        private void Constant(int register, ulong value)
        {
            Word(0xd2800000 | ((uint)value & 0xffff) << 5 | (uint)register);
            for (int shift = 16; shift < 64; shift += 16)
            {
                uint part = (uint)(value >> shift) & 0xffff;
                if (part != 0) Word(0xf2800000 | (uint)(shift / 16) << 21 | part << 5 | (uint)register);
            }
        }

        private void Load(int register, int slot) => Word(0xf9400020 | (uint)(slot << 10 | register));
        private void Store(int register, int slot) => Word(0xf9000020 | (uint)(slot << 10 | register));
        private void LoadNumber(int register, int slot) => Word(0xfd400020 | (uint)(slot << 10 | register));
        private void Ret() => Word(0xd65f03c0);
        private void Word(uint value) => code.Add(value);
    }
}
