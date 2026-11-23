using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private HpirOp WordBinary(LlvmBinaryOperator operation) => operation.Opcode switch
        {
            LlvmOpcode.Add => HpirOp.AddU64,
            LlvmOpcode.Sub => HpirOp.SubU64,
            LlvmOpcode.Mul => HpirOp.MulU64,
            LlvmOpcode.And => HpirOp.AndU64,
            LlvmOpcode.Or => HpirOp.OrU64,
            LlvmOpcode.Xor => HpirOp.XorU64,
            LlvmOpcode.Shl => HpirOp.ShlU64,
            LlvmOpcode.LShr => HpirOp.ShrU64,
            LlvmOpcode.AShr => HpirOp.SarI64,
            _ => throw Error($"lowering {operation.Opcode} on {operation.Type.Id}", operation)
        };

        private void CompareWord(LlvmICmpInstruction compare)
        {
            if (compare.Left.Type.IsIntegerTy(1))
            {
                CompareBool(compare);
                return;
            }
            if (Width(compare.Left.Type) != HpirWidth.U64)
                throw Error($"lowering {compare.Predicate} on {compare.Left.Type.Id}", compare);
            int a = Location(compare.Left), b = Location(compare.Right);
            int result = Location(compare);
            var predicate = compare.Predicate;
            if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge
                or LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge)
            {
                predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
                (a, b) = (b, a);
            }
            var op = predicate switch
            {
                LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe => HpirOp.EqU64,
                LlvmCmpPredicate.ICmpUlt => HpirOp.LtU64,
                LlvmCmpPredicate.ICmpUle => HpirOp.LeU64,
                LlvmCmpPredicate.ICmpSlt => HpirOp.LtI64,
                LlvmCmpPredicate.ICmpSle => HpirOp.LeI64,
                _ => throw Error($"lowering {compare.Predicate}", compare)
            };
            Add(new(op, result, a, b), compare.Source);
            if (predicate == LlvmCmpPredicate.ICmpNe) InvertBool(result, compare.Source);
        }

        private void CompareBool(LlvmICmpInstruction compare)
        {
            int a = Location(compare.Left), b = Location(compare.Right), to = Location(compare);
            var predicate = compare.Predicate;
            if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)
            {
                Add(new(HpirOp.XorU8, to, a, b), compare.Source);
                if (predicate == LlvmCmpPredicate.ICmpEq) InvertBool(to, compare.Source);
                return;
            }
            // A signed i1 interprets true as -1, so the unsigned order reverses.
            if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle
                or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge)
                (a, b) = (b, a);
            Copy(HpirWidth.U8, to, a, compare.Source);
            InvertBool(to, compare.Source);
            Add(new(HpirOp.AndU8, to, to, b), compare.Source);
            if (predicate is LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpUge
                or LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSge)
                InvertBool(to, compare.Source);
        }

        private bool TryWordCast(LlvmCastInstruction cast)
        {
            if (cast.Operand.Type is not LlvmIntegerType input || cast.Type is not LlvmIntegerType output) return false;
            int from = Location(cast.Operand), to = Location(cast);
            if (cast.Opcode == LlvmOpcode.Trunc)
            {
                Copy(Width(output), to, from, cast.Source);
                if (output.BitWidth == 1)
                    Add(new(HpirOp.AndU8, to, to, TrueByte(cast.Source)), cast.Source);
                return true;
            }
            if (cast.Opcode == LlvmOpcode.ZExt && output.BitWidth > 8)
            {
                Add(new(ConstOp(Width(output)), to), cast.Source);
                Copy(Width(input), to, from, cast.Source);
                return true;
            }
            if (cast.Opcode != LlvmOpcode.SExt || output.BitWidth != 64) return false;
            SignExtend(input, from, to, cast);
            return true;
        }

        private void SignExtend(LlvmIntegerType input, int from, int to, LlvmInstruction site)
        {
            var op = input.BitWidth switch
            {
                8 => HpirOp.SignExtendI8,
                16 => HpirOp.SignExtendI16,
                32 => HpirOp.SignExtendI32,
                _ => throw Error($"sign extending i{input.BitWidth} displacement", site)
            };
            Add(new(op, to, from), site.Source);
        }
    }
}
