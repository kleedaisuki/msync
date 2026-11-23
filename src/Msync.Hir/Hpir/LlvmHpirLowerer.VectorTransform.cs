using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private static class VectorTransformPlan
    {
        public const int Owner = 0, Environment = 8, Limit = 16, Bytes = 24;
    }

    private sealed partial class Geometry
    {
        internal readonly record struct VectorTransform(bool ScopeText, bool Completion = false);

        internal bool ScopeTextSnapshot(LlvmCallInstruction call,
            LlvmSnapshotCarrier snapshot, LlvmArgument parameter)
        {
            if (!IsGuestPointer(parameter.Type) || !snapshot.Empty && snapshot.Element != parameter.Type)
                return false;
            int logical = call.GetOperandUse(0).LogicalType;
            if (logical < 0) logical = call.GetArgument(0).LogicalType;
            if (logical < 0 || parameter.LogicalType < 0) return false;
            var source = graph.Types[logical];
            if (source.Kind != TypeKind.Range || source.Element < 0) return false;
            var item = graph.Types[source.Element];
            var accepted = graph.Types[parameter.LogicalType];
            if (item.Kind != TypeKind.Scope || accepted.Kind != TypeKind.Scope) return false;
            // Both edges keep the actual member pointer. A structural view
            // admits any Scope layout, while a fixed view keeps its domain.
            return accepted.Layouts is not { Count: > 0 } layouts
                || item.Layouts is { Count: > 0 } actual && actual.All(layouts.Contains);
        }
        private int vectorTransformAllocator = -1;

        internal int VectorTransformAllocator()
        {
            if (vectorTransformAllocator >= 0) return vectorTransformAllocator;
            return vectorTransformAllocator = PhysicalAllocator(AddLayout(new(VectorTransformPlan.Bytes, 8,
                [VectorTransformPlan.Owner]) { BorrowOffsets = [VectorTransformPlan.Environment] }));
        }
    }

    private sealed partial class Body
    {
        private readonly record struct VectorTransformCursor(int Remaining, int Data, int One);
        private int vectorTransformPlans, vectorScopeTextPlans, vectorTransformOpens, vectorTransformPulls, emptyTransformSizes,
            vectorCompletionCalls;

        private void EmitVectorTransform(LlvmCallInstruction call, int input, Geometry.TransformRecipe shape)
        {
            bool take = geometry.IsTransformTake(call);
            int limit = take ? EmitRangeBound(call, call.GetArgument(1), HpirRangeBound.Take) : Reserve(8, 8);
            int output = Location(call);
            EmitCall(call, ~geometry.RetainedVectorTransform(shape), output, []);
            WriteVectorCallbackPlan(call, input, output, limit, take);
            if (take) ++transformWindows; else ++transformPlans;
            ++vectorTransformPlans;
        }

        private void WriteVectorCallbackPlan(LlvmCallInstruction call, int input, int output, int limit, bool take)
        {
            if (take)
            {
                int scratch = Reserve(8, 8);
                CopyPlanWord(call, output, input, VectorTransformPlan.Owner, scratch);
                CopyPlanWord(call, output, input, VectorTransformPlan.Environment, scratch);
                ClampRangeLength(call, limit, Indirect(input, VectorTransformPlan.Limit, call));
            }
            else
            {
                Copy(HpirWidth.U64, ~output, input, call.Source);
                Copy(HpirWidth.U64, Indirect(output, VectorTransformPlan.Environment, call), LeafLocation(call.GetArgument(1), 8), call.Source);
                Add(new(HpirOp.ConstU64, limit, Immediate: -1), call.Source);
            }
            Copy(HpirWidth.U64, Indirect(output, VectorTransformPlan.Limit, call), limit, call.Source);
            allocatedTransforms.Add(call);
        }

        private void ClampRangeLength(LlvmInstruction site, int limited, int maximum)
        {
            int below = Reserve(1, 1);
            Add(new(HpirOp.LtU64, below, maximum, limited), site.Source);
            int clamp = CursorBranch(below, site.Source);
            Copy(HpirWidth.U64, limited, maximum, site.Source);
            LinkCursorBranch(clamp, clamp + 1, code.Count);
        }

        private VectorTransformCursor OpenVectorTransform(LlvmInstruction site, int plan)
        {
            int owner = Reserve(8, 8), remaining = Reserve(8, 8);
            Copy(HpirWidth.U64, owner, ~plan, site.Source);
            Copy(HpirWidth.U64, remaining, Indirect(plan, VectorTransformPlan.Limit, site), site.Source);
            ClampRangeLength(site, remaining, ~owner);
            ++vectorTransformOpens;
            return OpenSnapshotWords(site, owner, remaining);
        }

        private VectorTransformCursor OpenSnapshotWords(LlvmInstruction site, int owner, int remaining)
        {
            int data = Reserve(8, 8), one = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), site.Source);
            int empty = BranchWhenZero(site, remaining);
            Copy(HpirWidth.U64, data, Indirect(owner, HpirTransformImports.VectorData, site), site.Source);
            LinkCursorBranch(empty, code.Count, empty + 1);
            return new(remaining, data, one);
        }

        private void PullVectorTransform(LlvmInstruction site, VectorTransformCursor cursor, int available, int output, int completion = -1)
        {
            var source = site.Source;
            int exhausted = BranchWhenZero(site, cursor.Remaining);
            Copy(HpirWidth.U64, output, ~cursor.Data, source);
            if (completion >= 0)
                Copy(HpirWidth.U64, completion, Indirect(cursor.Data, SnapshotCompletionOffset, site), source);
            Add(new(HpirOp.OffsetPointer, cursor.Data, cursor.Data,
                Immediate: completion >= 0 ? SnapshotCompletionStride : sizeof(ulong)), source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), source);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available, Immediate: 1), source);
            int yielded = code.Count;
            Add(new(HpirOp.Jump), source);
            LinkCursorBranch(exhausted, code.Count, exhausted + 1);
            RangeSourcePoint(source);
            Add(new(HpirOp.ConstU8, available), source);
            code[yielded] = code[yielded] with { A = code.Count };
            ++vectorTransformPulls;
        }

        private void ReportVectorTransforms(CompilationTrace? trace)
        {
            trace?.Metric("range.transform.vector_plans", vectorTransformPlans);
            if (vectorScopeTextPlans != 0) trace?.Metric("range.transform.scope_text_vector_plans", vectorScopeTextPlans);
            trace?.Metric("range.transform.vector_opens", vectorTransformOpens);
            trace?.Metric("range.snapshot.word_pulls", vectorTransformPulls);
            trace?.Metric("range.transform.empty_size", emptyTransformSizes);
            if (vectorCompletionCalls != 0) trace?.Metric("range.transform.completion_calls", vectorCompletionCalls);
        }
    }
}
