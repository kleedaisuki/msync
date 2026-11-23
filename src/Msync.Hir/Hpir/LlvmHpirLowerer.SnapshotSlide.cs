using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private readonly record struct SlidePlan(LlvmValue Owner, LlvmType Element);
        private readonly record struct SlideCursor(LlvmValue Owner, int Remaining, int Data, int One, int Stride);
        private readonly record struct WindowRead(LlvmType Element, bool Last);
        private Dictionary<LlvmCallInstruction, SlidePlan>? slidePlans;
        private Dictionary<LlvmCallInstruction, SlideCursor>? slideCursors;
        private Dictionary<LlvmCallInstruction, WindowRead>? slideReads;
        private int slidePulls;

        private bool TrySnapshotSlide(LlvmCallInstruction call)
        {
            if (slideReads?.TryGetValue(call, out var read) == true)
            {
                int window = Location(call.GetArgument(0));
                int offset = read.Last ? geometry.ElementBytes(read.Element) : 0;
                foreach (var leaf in geometry.Leaves(read.Element))
                    Copy(Width(leaf.Type), LeafLocation(call, leaf.Offset),
                        Indirect(window, checked(offset + leaf.Offset), call), call.Source);
                return true;
            }
            if (geometry.NativeRangeCall(call, "ranges.slide")) return SelectSnapshotSlide(call);
            if (slidePlans is null || call.CalledFunction is not { } target || !geometry.IteratorOperation(target, out var op))
                return false;
            if (call.GetArgument(0) is not LlvmCallInstruction origin) return false;
            if (op == LlvmIteratorOperation.Open && slidePlans.TryGetValue(origin, out var plan))
            {
                (slideCursors ??= []).Add(call, OpenSlide(call, plan));
                return true;
            }
            if (slideCursors?.TryGetValue(origin, out var cursor) != true) return false;
            if (op == LlvmIteratorOperation.Close) return true;
            if (op != LlvmIteratorOperation.Next) return false;
            PullSlide(call, cursor);
            return true;
        }

        private bool SelectSnapshotSlide(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || call.GetArgument(1) is not LlvmConstantFP width
                || width.Value.BitcastToAPInt().GetZExtValue() != BitConverter.DoubleToUInt64Bits(2)
                || geometry.VectorShape(call.GetArgument(0)) is not { } shape) return false;

            Dictionary<LlvmCallInstruction, WindowRead> reads = [];
            foreach (var user in call.Users)
            {
                if (user is not LlvmCallInstruction open || !SlideIterator(open, call, LlvmIteratorOperation.Open)) return false;
                if (!SelectSlideOpen(open, shape, reads)) return false;
            }
            (slidePlans ??= []).Add(call, new(call.GetArgument(0), shape.Element));
            if (reads.Count != 0)
            {
                slideReads ??= [];
                foreach (var (consumer, read) in reads) slideReads.Add(consumer, read);
            }
            return true;
        }

        private bool SlideIterator(LlvmCallInstruction usage, LlvmCallInstruction input, LlvmIteratorOperation expected) =>
            usage.ArgumentCount != 0 && usage.GetArgument(0) == input && usage.CalledFunction is { } target
            && geometry.IteratorOperation(target, out var operation) && operation == expected;

        private bool SelectSlideOpen(LlvmCallInstruction open, LlvmSnapshotCarrier shape,
            Dictionary<LlvmCallInstruction, WindowRead> reads)
        {
            HashSet<LlvmAllocaInstruction> items = [];
            foreach (var iteratorUse in open.Users)
            {
                if (iteratorUse is not LlvmCallInstruction next) return false;
                if (SlideIterator(next, open, LlvmIteratorOperation.Close)) continue;
                if (!SlideIterator(next, open, LlvmIteratorOperation.Next) || next.GetArgument(1) is not LlvmAllocaInstruction item)
                    return false;
                if (items.Add(item) && !CollectSlideReads(open, item, shape, reads)) return false;
            }
            return true;
        }

        private bool CollectSlideReads(LlvmCallInstruction open, LlvmAllocaInstruction item, LlvmSnapshotCarrier shape,
            Dictionary<LlvmCallInstruction, WindowRead> reads)
        {
            // A window that stays in local reads needs only a borrowed data
            // pointer, not a heap object. Any other use requires a real view.
            HashSet<LlvmValue> seen = [];
            HashSet<LlvmValue> stored = [];
            Stack<LlvmValue> pending = new([item]);
            while (pending.TryPop(out var value))
            {
                if (!seen.Add(value)) continue;
                foreach (var user in value.Users)
                    if (!FollowSlideUse(value, user, open, shape, reads, stored, pending)) return false;
            }
            return stored.IsSubsetOf(seen);
        }

        private bool FollowSlideUse(LlvmValue value, LlvmUser user, LlvmCallInstruction open, LlvmSnapshotCarrier shape,
            Dictionary<LlvmCallInstruction, WindowRead> reads, HashSet<LlvmValue> stored, Stack<LlvmValue> pending)
        {
            if (value is LlvmAllocaInstruction)
            {
                if (user is LlvmCallInstruction pull && SlideIterator(pull, open, LlvmIteratorOperation.Next)
                    && pull.GetArgument(1) == value) return true;
                if (user is LlvmLoadInstruction load && load.PointerOperand == value) { pending.Push(load); return true; }
                if (user is not LlvmStoreInstruction store || store.PointerOperand != value) return false;
                stored.Add(store.ValueOperand);
                return true;
            }
            if (user is LlvmStoreInstruction move && move.ValueOperand == value
                && move.PointerOperand is LlvmAllocaInstruction local)
            {
                pending.Push(local);
                return true;
            }
            if (user is not LlvmCallInstruction call || call.ArgumentCount != 1 || call.GetArgument(0) != value
                || (!shape.Empty && call.Type != shape.Element)) return false;
            bool last = geometry.NativeRangeCall(call, "ranges.last");
            if (!last && !geometry.NativeRangeCall(call, "ranges.first")) return false;
            reads[call] = new(shape.Empty ? call.Type : shape.Element, last);
            return true;
        }

        private SlideCursor OpenSlide(LlvmCallInstruction site, SlidePlan plan)
        {
            var source = site.Source;
            int owner = Location(plan.Owner), data = Reserve(8, 8), one = Reserve(8, 8);
            int remaining = Reserve(8, 8), full = Reserve(1, 1);
            Add(new(HpirOp.ConstU64, remaining), source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), source);
            Add(new(HpirOp.LtU64, full, one, ~owner), source);
            int enough = CursorBranch(full, source);
            Add(new(HpirOp.SubU64, remaining, ~owner, one), source);
            Copy(HpirWidth.U64, data, Indirect(owner, HpirTransformImports.VectorData, site), source);
            LinkCursorBranch(enough, enough + 1, code.Count);
            return new(plan.Owner, remaining, data, one, geometry.ElementBytes(plan.Element));
        }

        private void PullSlide(LlvmCallInstruction site, SlideCursor cursor)
        {
            var source = site.Source;
            int available = Location(site);
            int exhausted = BranchWhenZero(site, cursor.Remaining);
            Copy(HpirWidth.U64, Memory(site.GetArgument(1)), cursor.Data, source);
            Add(new(HpirOp.OffsetPointer, cursor.Data, cursor.Data, Immediate: cursor.Stride), source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), source);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available, Immediate: 1), source);
            int yielded = code.Count;
            Add(new(HpirOp.Jump), source);
            LinkCursorBranch(exhausted, code.Count, exhausted + 1);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available), source);
            code[yielded] = code[yielded] with { A = code.Count };
            ++slidePulls;
        }

        private void ReportSlides(CompilationTrace? trace)
        {
            if (slidePlans is null) return;
            trace?.Metric("range.slide.borrowed_plans", slidePlans.Count);
            trace?.Metric("range.slide.frame_cursors", slideCursors?.Count ?? 0);
            trace?.Metric("range.slide.pulls", slidePulls);
            trace?.Metric("range.slide.local_reads", slideReads?.Count ?? 0);
        }
    }
}
