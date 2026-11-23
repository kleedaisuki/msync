using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private List<LlvmFileOutInst>? capturedPathBatches;
        private Dictionary<LlvmFunction, LlvmFunction>? pathBatchCarriers;

        private void SelectPathBatchCaptures(RangeFields fields)
        {
            if (capturedPathBatches is null) return;
            var existing = new Dictionary<LlvmFunction, LlvmFunction>();
            int selected = 0;
            foreach (var transfer in capturedPathBatches)
            {
                var load = (LlvmLoadInstruction)transfer.Value;
                if (fields.ReadFields(load.PointerOperand) is not { Length: > 0 } lanes
                    || CapturedPathMapper(fields, lanes) is not { } callback) continue;
                if (!existing.TryGetValue(callback, out var target))
                {
                    target = LlvmFunction.Create(LlvmFunctionType.Get(load.Type, [load.Type]),
                        LlvmLinkage.ExternalLinkage, "msync.path.batch.capture", module);
                    target.MemoryEffectsAttribute = new(0);
                    target.NoUnwind = true;
                    target.WillReturnAttribute = true;
                    runtimeConversions.Add(target, new(LlvmRuntimeConversionOperation.Represent));
                    existing.Add(callback, target);
                    (pathBatchCarriers ??= []).Add(target, callback);
                }
                // Only the selected carrier crosses the optimizer boundary;
                // the load still reads the live, aliased capture location.
                var carrier = new LlvmCallInstruction(target.FunctionType, target, [load]);
                carrier.Source = load.Source;
                carrier.LogicalType = load.LogicalType;
                transfer.Parent!.InsertBefore(carrier, transfer);
                transfer.Value = carrier;
                ++selected;
            }
            trace?.Detail?.Metric("range.fields.path_batch_capture_consumers", selected);
        }

        private LlvmFunction? CapturedPathMapper(RangeFields fields, LlvmFieldCarrier[] lanes)
        {
            LlvmFunction? selected = null;
            foreach (var lane in lanes)
            {
                if (fields.FieldWrites(lane) is not { Count: > 0 } writes) return null;
                foreach (var write in writes)
                {
                    if (PathMapper(write) is not { } callback || selected is not null && selected != callback) return null;
                    selected = callback;
                }
            }
            return selected;
        }

        private LlvmFunction? PathMapper(LlvmValue value)
        {
            while (value is LlvmCallInstruction { CalledFunction: { } target, ArgumentCount: 1 } forward
                && runtimeConversions.GetValueOrDefault(target)?.Operation == LlvmRuntimeConversionOperation.Represent)
                value = forward.GetArgument(0);
            if (value is not LlvmCallInstruction { CalledFunction: { } producer, ArgumentCount: 2 } map
                || nativeCalls.GetValueOrDefault(producer) != "ranges.transform"
                || map.GetArgument(1) is not LlvmCallInstruction { CalledFunction: { } adapter }
                || callableCalls.GetValueOrDefault(adapter) is not
                    { Operation: LlvmCallableRuntimeOperation.Adapt, SessionSlot: < 0, Targets.Count: 1 } binding)
                return null;
            var callback = binding.Targets.Values.First();
            return callback.ReturnType is LlvmStructType { Name: "msync.path" } ? callback : null;
        }
    }
}
