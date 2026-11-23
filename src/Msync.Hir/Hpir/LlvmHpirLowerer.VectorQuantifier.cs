using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private enum Quantifier : byte { None, AllOf, AnyOf, NoneOf, Contains }

    private sealed partial class Geometry
    {
        internal Quantifier VectorQuantifier(LlvmCallInstruction call) =>
            call.CalledFunction is { } target ? graph.NativeCalls.GetValueOrDefault(target) switch
            {
                "ranges.all_of" => Quantifier.AllOf,
                "ranges.any" or "ranges.any_of" => Quantifier.AnyOf,
                "ranges.none" or "ranges.none_of" => Quantifier.NoneOf,
                "ranges.contains" => Quantifier.Contains,
                _ => Quantifier.None
            } : Quantifier.None;
    }

    private sealed partial class Body
    {
        private int typeQuantifiers, scalarQuantifiers, emptyQuantifiers, snapshotMemberships;

        private bool TryVectorQuantifier(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !call.Type.IsIntegerTy(1)) return false;
            Quantifier operation = geometry.VectorQuantifier(call);
            if (operation == Quantifier.None) return false;
            LlvmValue vector = call.GetArgument(0);
            if ((geometry.SelectedSnapshot(call) ?? geometry.VectorShape(vector)) is not { } shape) return false;
            if (operation == Quantifier.Contains)
                return TrySnapshotMembership(call, shape);
            bool defaultResult = operation != Quantifier.AnyOf;
            if (shape.Empty || geometry.KnownEmptyRangeConsumer(call))
            {
                Add(new(HpirOp.ConstU8, Location(call), Immediate: defaultResult ? 1 : 0), call.Source);
                ++emptyQuantifiers;
                return true;
            }

            LlvmValue closure = call.GetArgument(1);
            bool pointer = geometry.IsGuestPointer(shape.Element);
            bool type = pointer && geometry.SnapshotKind(call, 0, shape.Element) == TypeKind.Type;
            if ((!pointer && !shape.Element.IsIntegerTy(1) && !shape.Element.IsDoubleTy())
                || geometry.ReadyUnaryCallback(closure) is not { } target || !target.ReturnType.IsIntegerTy(1)
                || target.Arguments[geometry.Captures(target)].Type != shape.Element
                || geometry.ReductionWork(target) != LlvmRangeCallbackWork.Ready)
                return false;
            EmitVectorQuantifier(call, target, shape, operation, defaultResult);
            if (type) ++typeQuantifiers;
            else ++scalarQuantifiers;
            return true;
        }

        private void EmitVectorQuantifier(LlvmCallInstruction call, LlvmFunction target, LlvmSnapshotCarrier shape,
            Quantifier operation, bool defaultResult)
        {
            var source = call.Source;
            int result = Location(call), owner = Location(call.GetArgument(0));
            int remaining = Reserve(8, 8), data = Reserve(8, 8);
            int zero = Reserve(8, 8), one = Reserve(8, 8), noItems = Reserve(1, 1);
            Add(new(HpirOp.ConstU8, result, Immediate: defaultResult ? 1 : 0), source);
            Add(new(HpirOp.ConstU64, zero), source);
            Copy(HpirWidth.U64, remaining, ~owner, source);
            Add(new(HpirOp.EqU64, noItems, remaining, zero), source);
            int empty = CursorBranch(noItems, source);
            Copy(HpirWidth.U64, data, Indirect(owner, HpirTransformImports.VectorData, call), source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), source);

            int captureCount = geometry.Captures(target);
            int[] captures = captureCount == 0 ? [] : ReadCaptureAddresses(call, LeafLocation(call.GetArgument(1), 8), captureCount);
            HpirWidth width = Width(shape.Element);
            int item = Reserve((int)width, (int)width), predicate = Reserve(1, 1);
            int next = code.Count;
            Copy(width, item, ~data, source);
            EmitRangeCallback(call, target, captures, item, predicate);
            int tested = CursorBranch(predicate, source);
            int advance = code.Count;
            Add(new(HpirOp.SubU64, remaining, remaining, one), source);
            Add(new(HpirOp.OffsetPointer, data, data, Immediate: geometry.ElementBytes(shape.Element)), source);
            Add(new(HpirOp.EqU64, noItems, remaining, zero), source);
            int exhausted = CursorBranch(noItems, source);
            int decisive = code.Count;
            Add(new(HpirOp.ConstU8, result, Immediate: defaultResult ? 0 : 1), source);

            int done = code.Count;
            LinkCursorBranch(empty, done, empty + 1);
            LinkCursorBranch(exhausted, done, next);
            if (operation == Quantifier.AllOf) LinkCursorBranch(tested, advance, decisive);
            else LinkCursorBranch(tested, decisive, advance);
        }

        private bool TrySnapshotMembership(LlvmCallInstruction call, LlvmSnapshotCarrier shape)
        {
            var sought = call.GetArgument(1);
            if (FutureRoot(sought) || UnionCell(sought)) return false;
            int result = Location(call);
            if (shape.Empty)
            {
                Add(new(HpirOp.ConstU8, result), call.Source);
                ++emptyQuantifiers;
                return true;
            }
            if (sought.Type != shape.Element) return false;
            TypeKind member = geometry.SnapshotKind(call, 0, shape.Element);
            if (shape.Element.IsIntegerTy(1)) member = TypeKind.Bool;
            if (member is not (TypeKind.Number or TypeKind.Bool or TypeKind.Scope or TypeKind.String)) return false;
            if (geometry.IsGuestPointer(shape.Element) && geometry.ReflectedType(call, 1)?.Kind != member) return false;
            EmitSnapshotMembership(call, result, Location(sought), member, geometry.ElementBytes(shape.Element));
            ++snapshotMemberships;
            return true;
        }

        private void EmitSnapshotMembership(LlvmCallInstruction call, int result, int sought, TypeKind member, int stride)
        {
            int vector = Location(call.GetArgument(0)), remaining = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, ~vector, call.Source);
            var cursor = OpenSnapshotWords(call, vector, remaining);
            Add(new(HpirOp.ConstU8, result), call.Source);
            int soughtNaN = -1;
            if (member == TypeKind.Number)
            {
                soughtNaN = Reserve(1, 1);
                Add(new(HpirOp.NeF64, soughtNaN, sought, sought), call.Source);
            }
            int next = code.Count;
            int exhausted = BranchWhenZero(call, remaining);
            int item = ~cursor.Data;
            switch (member)
            {
                case TypeKind.Scope:
                    Add(new(HpirOp.EqU64, result, item, sought), call.Source);
                    break;
                case TypeKind.Bool:
                    Add(new(HpirOp.EqU8, result, item, sought), call.Source);
                    break;
                case TypeKind.Number:
                    EmitNumberMembership(call, result, item, sought, soughtNaN);
                    break;
                default:
                    var (index, _) = geometry.StringImport(HpirStringOp.Equal);
                    EmitCall(call, ~index, result, [item, sought], safepoint: false);
                    break;
            }
            int matched = CursorBranch(result, call.Source);
            Add(new(HpirOp.OffsetPointer, cursor.Data, cursor.Data, Immediate: stride), call.Source);
            Add(new(HpirOp.SubU64, remaining, remaining, cursor.One), call.Source);
            Add(new(HpirOp.Jump, A: next), call.Source);
            LinkCursorBranch(matched, code.Count, matched + 1);
            LinkCursorBranch(exhausted, code.Count, exhausted + 1);
        }

        private void EmitNumberMembership(LlvmInstruction site, int result, int item, int sought, int soughtNaN)
        {
            // Range equality, unlike scalar IEEE ==, regards all NaNs as equal.
            int a = Reserve(1, 1);
            Add(new(HpirOp.EqF64, result, item, sought), site.Source);
            Add(new(HpirOp.NeF64, a, item, item), site.Source);
            Add(new(HpirOp.AndU8, a, a, soughtNaN), site.Source);
            Add(new(HpirOp.OrU8, result, result, a), site.Source);
        }

        private void ReportVectorQuantifiers(CompilationTrace? trace)
        {
            if (typeQuantifiers != 0) trace?.Metric("vectors.type_quantifiers", typeQuantifiers);
            if (scalarQuantifiers != 0) trace?.Metric("vectors.scalar_quantifiers", scalarQuantifiers);
            if (emptyQuantifiers != 0) trace?.Metric("vectors.empty_quantifiers", emptyQuantifiers);
            if (snapshotMemberships != 0) trace?.Metric("vectors.membership_sites", snapshotMemberships);
        }
    }
}
