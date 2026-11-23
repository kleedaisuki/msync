using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal enum TextPipelineStep : byte { Map, Filter, Take }
        internal sealed record TextPipelineShape(bool File, LlvmFunction Map, LlvmFunction? Filter, bool Bounded);
        private readonly Dictionary<LlvmValue, TextPipelineShape?> textPipelines = [];
        private int textPipelineAlloc = -1;

        internal TextPipelineShape? TextPipeline(LlvmValue value)
        {
            if (!HasLiveText || value is not LlvmCallInstruction { ArgumentCount: 2 } call || !call.Type.IsPointerTy())
                return null;
            if (textPipelines.TryGetValue(value, out var shape)) return shape;
            textPipelines.Add(value, null);
            return textPipelines[value] = SelectTextPipeline(call);
        }

        private TextPipelineShape? SelectTextPipeline(LlvmCallInstruction call)
        {
            var upstream = call.GetArgument(0);
            if (NativeRangeCall(call, "ranges.transform") && LiveTextPlan(upstream) is { } file
                && ReadyUnaryCallback(call.GetArgument(1)) is { } map
                && StringValue(map.Arguments[Captures(map)]) && map.ReturnType.IsDoubleTy())
                return new(file, map, null, false);
            if (IsTransformTake(call) && TextPipeline(upstream) is { } input)
                return input with { Bounded = true };
            if (NativeRangeCall(call, "ranges.filter") && TextPipeline(upstream) is { Filter: null, Bounded: false } mapped
                && ReadyUnaryCallback(call.GetArgument(1)) is { } filter
                && filter.Arguments[Captures(filter)].Type.IsDoubleTy() && filter.ReturnType.IsIntegerTy(1))
                return mapped with { Filter = filter };
            return null;
        }

        internal TextPipelineStep PipelineStep(LlvmCallInstruction call) => NativeRangeCall(call, "ranges.transform")
            ? TextPipelineStep.Map : NativeRangeCall(call, "ranges.filter") ? TextPipelineStep.Filter : TextPipelineStep.Take;

        internal int TextPipelineAllocator()
        {
            if (textPipelineAlloc >= 0) return textPipelineAlloc;
            int layout = AddLayout(new(TextPipelineArguments.Bytes, 8, [TextPipelineArguments.Source])
                { BorrowOffsets = [TextPipelineArguments.Map, TextPipelineArguments.Filter] });
            textPipelineAlloc = Imports.Count;
            Imports.Add(new(HpirService.AllocateObject, layout, HpirWidth.U64, []));
            return textPipelineAlloc;
        }
    }

    private static class TextPipelineArguments
    {
        public const int Source = 0, Map = 8, Filter = 16, Limit = 24, Bytes = 32;
    }

    private sealed partial class Body
    {
        private readonly record struct TextPipelineCursor(
            Geometry.TextPipelineShape Shape, LlvmValue Owner, int Handle, int Remaining,
            int Line, int LineAddress, int[] Map, int[] Filter);
        private readonly Dictionary<LlvmCallInstruction, TextPipelineCursor> textPipelineCursors = [];
        private int textPipelinePlans, textPipelinePullSites, textPipelinePredicateSites;

        private bool TryTextPipeline(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target) return false;
            if (geometry.TextPipeline(call) is not null)
            {
                EmitTextPipelinePlan(call, geometry.PipelineStep(call));
                return true;
            }
            if (!geometry.IteratorOperation(target, out var operation)) return false;
            var input = call.GetArgument(0);
            if (operation == LlvmIteratorOperation.Open && geometry.TextPipeline(input) is { } shape)
            {
                textPipelineCursors.Add(call, OpenTextPipeline(call, input, shape));
                return true;
            }
            if (input is not LlvmCallInstruction original || !textPipelineCursors.TryGetValue(original, out var cursor)) return false;
            if (operation == LlvmIteratorOperation.Close)
            {
                CloseTextPipeline(call, cursor);
                return true;
            }
            if (operation != LlvmIteratorOperation.Next || !DoubleItem(call.GetArgument(1))) return false;
            NextTextPipeline(call, cursor);
            return true;
        }

        private void EmitTextPipelinePlan(LlvmCallInstruction call, Geometry.TextPipelineStep step)
        {
            int plan = Location(call), input = Location(call.GetArgument(0));
            int count = step == Geometry.TextPipelineStep.Take ? TakeCount(call) : -1;
            RangeSourcePoint(call.Source);
            EmitCall(call, ~geometry.TextPipelineAllocator(), plan, []);
            if (step == Geometry.TextPipelineStep.Map)
            {
                Copy(HpirWidth.U64, ~plan, input, call.Source);
                Copy(HpirWidth.U64, Indirect(plan, TextPipelineArguments.Map, call), LeafLocation(call.GetArgument(1), 8), call.Source);
                Add(new(HpirOp.ConstU64, Indirect(plan, TextPipelineArguments.Limit, call), Immediate: -1), call.Source);
            }
            else
            {
                int lane = Reserve(8, 8);
                for (int offset = 0; offset < TextPipelineArguments.Bytes; offset += sizeof(ulong))
                    CopyPlanWord(call, plan, input, offset, lane);
                if (step == Geometry.TextPipelineStep.Filter)
                    Copy(HpirWidth.U64, Indirect(plan, TextPipelineArguments.Filter, call), LeafLocation(call.GetArgument(1), 8), call.Source);
                else
                {
                    int smaller = Reserve(1, 1);
                    Add(new(HpirOp.LtU64, smaller, count, Indirect(plan, TextPipelineArguments.Limit, call)), call.Source);
                    int shorten = CursorBranch(smaller, call.Source);
                    Copy(HpirWidth.U64, Indirect(plan, TextPipelineArguments.Limit, call), count, call.Source);
                    LinkCursorBranch(shorten, shorten + 1, code.Count);
                }
            }
            allocatedTransforms.Add(call);
            ++textPipelinePlans;
        }

        private int TakeCount(LlvmCallInstruction call)
        {
            LlvmValue value = call.GetArgument(1);
            return EmitRangeBound(call, value, HpirRangeBound.Take);
        }

        private TextPipelineCursor OpenTextPipeline(LlvmCallInstruction call, LlvmValue source, Geometry.TextPipelineShape shape)
        {
            int plan = Location(source);
            int remaining = Reserve(8, 8), handle = Reserve(8, 8), line = Reserve(8, 8), address = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, Indirect(plan, TextPipelineArguments.Limit, call), call.Source);
            Add(new(HpirOp.ConstU64, handle), call.Source);
            Add(new(HpirOp.AddressOf, address, line), call.Source);
            var map = ReadPlanCaptures(call, plan, TextPipelineArguments.Map, shape.Map);
            var filter = shape.Filter is { } predicate ? ReadPlanCaptures(call, plan, TextPipelineArguments.Filter, predicate) : [];
            int empty = BranchWhenZero(call, remaining);
            RangeSourcePoint(call.Source);
            EmitCall(call, ~geometry.LiveTextImport(HpirTextRangeImports.Open(shape.File)), handle, [~plan], safepoint: false);
            LinkCursorBranch(empty, code.Count, empty + 1);
            return new(shape, source, handle, remaining, line, address, map, filter);
        }

        private void NextTextPipeline(LlvmCallInstruction call, TextPipelineCursor cursor)
        {
            int available = Location(call), number = Reserve(8, 8);
            var source = call.Source;
            int atLimit = BranchWhenZero(call, cursor.Remaining);
            int pull = code.Count;
            RangeSourcePoint(source);
            EmitCall(call, ~geometry.LiveTextImport(HpirTextRangeImports.Next()), available, [cursor.Handle, cursor.LineAddress]);
            int hasLine = CursorBranch(available, source);
            EmitRangeCallback(call, cursor.Shape.Map, cursor.Map, cursor.Line, number);
            if (cursor.Shape.Filter is { } predicate)
            {
                int accepted = Reserve(1, 1);
                EmitRangeCallback(call, predicate, cursor.Filter, number, accepted);
                int keep = CursorBranch(accepted, source);
                LinkCursorBranch(keep, code.Count, pull);
                ++textPipelinePredicateSites;
            }
            int one = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, one), source);
            Copy(HpirWidth.U64, Memory(call.GetArgument(1)), number, source);
            int done = code.Count;
            Add(new(HpirOp.Jump), source);

            int exhausted = code.Count;
            Add(new(HpirOp.ConstU64, cursor.Remaining), source);
            Add(new(HpirOp.ConstU8, available), source);
            LinkCursorBranch(atLimit, exhausted, pull);
            LinkCursorBranch(hasLine, hasLine + 1, exhausted);
            code[done] = code[done] with { A = code.Count };
            ++textPipelinePullSites;
        }

        private void CloseTextPipeline(LlvmCallInstruction call, TextPipelineCursor cursor)
        {
            int absent = BranchWhenZero(call, cursor.Handle);
            RangeSourcePoint(call.Source);
            EmitCall(call, ~geometry.LiveTextImport(HpirTextRangeImports.Close()), -1, [cursor.Handle], safepoint: false);
            LinkCursorBranch(absent, code.Count, absent + 1);
        }

        private void ReportTextPipelines(CompilationTrace? trace)
        {
            trace?.Metric("range.text_pipeline.plans", textPipelinePlans);
            trace?.Metric("range.text_pipeline.cursor_sites", textPipelineCursors.Count);
            trace?.Metric("range.text_pipeline.pull_sites", textPipelinePullSites);
            trace?.Metric("range.text_pipeline.predicate_sites", textPipelinePredicateSites);
        }
    }
}
