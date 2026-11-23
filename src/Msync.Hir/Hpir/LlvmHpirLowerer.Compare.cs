using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private int trueByte = -1;

        private int TrueByte(SourceSpan? source)
        {
            if (trueByte < 0) trueByte = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, trueByte, Immediate: 1), source);
            return trueByte;
        }

        private void InvertBool(int destination, SourceSpan? source)
        {
            Add(new(HpirOp.XorU8, destination, destination, TrueByte(source)), source);
        }

        private void Compare(LlvmFCmpInstruction compare)
        {
            var emitter = new CompareEmitter(this, compare);
            emitter.Emit();
        }

        private readonly struct CompareEmitter(Body body, LlvmFCmpInstruction compare)
        {
            private readonly int dest = body.Location(compare), a = body.Location(compare.Left), b = body.Location(compare.Right);
            private LlvmCmpPredicate Predicate => compare.Predicate;

            internal void Emit()
            {
                switch (Predicate)
                {
                    case LlvmCmpPredicate.FCmpFalse:
                    case LlvmCmpPredicate.FCmpTrue:
                        body.Add(new(HpirOp.ConstU8, dest, Immediate: Predicate == LlvmCmpPredicate.FCmpTrue ? 1 : 0), compare.Source);
                        return;
                    case LlvmCmpPredicate.FCmpOrd:
                        Ordered(true);
                        return;
                    case LlvmCmpPredicate.FCmpUno:
                        Ordered(false);
                        return;
                    case LlvmCmpPredicate.FCmpOne:
                    case LlvmCmpPredicate.FCmpUeq:
                        EqualityWithNaN(Predicate == LlvmCmpPredicate.FCmpUeq);
                        return;
                }
                HpirOp op = Predicate switch
                {
                    LlvmCmpPredicate.FCmpOeq => HpirOp.EqF64,
                    LlvmCmpPredicate.FCmpUne => HpirOp.NeF64,
                    LlvmCmpPredicate.FCmpOlt or LlvmCmpPredicate.FCmpUge => HpirOp.LtF64,
                    LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpUgt => HpirOp.LeF64,
                    LlvmCmpPredicate.FCmpOgt or LlvmCmpPredicate.FCmpUle => HpirOp.GtF64,
                    LlvmCmpPredicate.FCmpOge or LlvmCmpPredicate.FCmpUlt => HpirOp.GeF64,
                    _ => throw body.Error($"lowering {Predicate}", compare)
                };
                Add(op, dest, a, b);
                if (Predicate is >= LlvmCmpPredicate.FCmpUgt and <= LlvmCmpPredicate.FCmpUle)
                    body.InvertBool(dest, compare.Source);
            }

            private void Ordered(bool ordered)
            {
                int scratch = body.Reserve(1, 1);
                HpirOp test = ordered ? HpirOp.EqF64 : HpirOp.NeF64;
                Add(test, dest, a, a);
                Add(test, scratch, b, b);
                Add(ordered ? HpirOp.AndU8 : HpirOp.OrU8, dest, dest, scratch);
            }

            private void EqualityWithNaN(bool unorderedEqual)
            {
                int scratch = body.Reserve(1, 1);
                HpirOp test = unorderedEqual ? HpirOp.NeF64 : HpirOp.EqF64;
                HpirOp merge = unorderedEqual ? HpirOp.OrU8 : HpirOp.AndU8;
                Add(unorderedEqual ? HpirOp.EqF64 : HpirOp.NeF64, dest, a, b);
                Add(test, scratch, a, a);
                Add(merge, dest, dest, scratch);
                Add(test, scratch, b, b);
                Add(merge, dest, dest, scratch);
            }

            private void Add(HpirOp op, int d, int x, int y) => body.Add(new(op, d, x, y), compare.Source);

        }
    }
}
