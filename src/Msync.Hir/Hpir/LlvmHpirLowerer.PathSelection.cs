using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int pathSelectionLayout = -1;
        private readonly bool hasSelectionPlans = graph.ValueOperations.Values.Any(static binding =>
            binding.Operation == LlvmRuntimeValueOperation.SelectPath);
        private Dictionary<LlvmValue, bool>? selectionPlans;

        internal bool SelectionPlan(LlvmValue value)
        {
            if (!hasSelectionPlans || !IsGuestPointer(value.Type)) return false;
            if (value is LlvmCallInstruction call && PathSelection(call, out _)) return true;
            selectionPlans ??= [];
            if (selectionPlans.TryGetValue(value, out bool known)) return known;
            selectionPlans.Add(value, false);
            return selectionPlans[value] = RangeInputs(value) is { Count: > 0 } inputs && inputs.All(SelectionPlan);
        }

        internal bool PathSelection(LlvmCallInstruction call, out string? pattern)
        {
            pattern = null;
            return call.ArgumentCount == 1 && call.CalledFunction is { } target && TrySelection(target, out pattern);
        }

        internal int PathSelectionImport(bool prepared)
        {
            if (pathSelectionLayout < 0)
            {
                pathSelectionLayout = Layouts.Count;
                Layouts.Add(HpirPathSelectionImports.Layout());
            }
            _ = TextLayout();
            return FutureImport(HpirPathSelectionImports.Capture(pathSelectionLayout, prepared));
        }

        internal int LiveSelectionImport(bool observed) =>
            FutureImport(HpirLiveTransferImports.CreateSelection(TransferCompletionLayout(observed)));
    }

    private sealed partial class Body
    {
        private int liveSelectionPlans, liveSelectionTransfers;

        private bool TryPathSelection(LlvmCallInstruction call)
        {
            if (!geometry.PathSelection(call, out string? pattern)) return false;
            if (!Geometry.IsPathRecord(call.GetArgument(0).Type) || !geometry.IsGuestPointer(call.Type))
                throw Error("a path selection without a concrete Path record", call);
            int literal = Reserve(4, 4);
            Add(new(HpirOp.ConstU32, literal, Immediate: geometry.TextLiteral(pattern!)), call.Source);
            int path = PathRecordAddress(call.GetArgument(0), call);
            if (call == preparedSelector)
            {
                int recipe = Reserve(4, 4);
                Add(new(HpirOp.ConstU32, recipe, Immediate: preparedSelectorIndex), call.Source);
                EmitImportCall(call, geometry.PathSelectionImport(prepared: true), [path, literal, recipe]);
            }
            else
            {
                EmitImportCall(call, geometry.PathSelectionImport(prepared: false), [path, literal]);
                ++liveSelectionPlans;
            }
            return true;
        }

        private bool TryLiveSelectionTransfer(LlvmFileOutInst transfer)
        {
            LlvmValue selected = transfer.Value;
            if (!geometry.SelectionPlan(selected)) return false;
            bool observed = !transfer.UseEmpty;
            int import = geometry.LiveSelectionImport(observed);
            EmitCall(transfer, ~import, observed ? Location(transfer) : -1,
                [Location(selected), PathRecordAddress(transfer.Destination, transfer)], safepoint: observed);
            ++liveSelectionTransfers;
            return true;
        }

        private void ReportLiveSelections(CompilationTrace? trace)
        {
            trace?.Metric("paths.live_selection_plans", liveSelectionPlans);
            trace?.Metric("paths.live_selection_transfers", liveSelectionTransfers);
        }
    }
}
