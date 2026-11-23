using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

internal readonly record struct LlvmSnapshotCarrier(LlvmType Element, bool Empty, bool Completion = false);
internal readonly record struct LlvmSnapshotOperands(LlvmSnapshotCarrier? First, LlvmSnapshotCarrier? Second);

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private List<LlvmStoreInstruction>? rangeWrites;
        private List<LlvmCallInstruction>? rangeConsumers;
        private HashSet<LlvmFunction>? emptyRangeConsumers;
        private Dictionary<LlvmFunction, LlvmSnapshotOperands>? snapshotRangeConsumers;

        internal void RecordRangeSite(LlvmInstruction instruction)
        {
            switch (instruction)
            {
                case LlvmStoreInstruction store when IsRangeType(store.GetOperandUse(0).LogicalType)
                    || IsRangeType(store.ValueOperand.LogicalType):
                    (rangeWrites ??= []).Add(store);
                    break;
                case LlvmFileOutInst { Mode: LlvmFileOutMode.Transfer, Value: LlvmLoadInstruction
                    { PointerOperand: LlvmArgument } load } transfer
                    when IsRangeType(load.LogicalType) && metadata.Types[load.LogicalType].Element >= 0
                        && metadata.Types[metadata.Types[load.LogicalType].Element].Kind == TypeKind.Path:
                    (capturedPathBatches ??= []).Add(transfer);
                    break;
                case LlvmCallInstruction { CalledFunction: { } target } call
                    when nativeCalls.GetValueOrDefault(target) is "ranges.front" or "ranges.first" or "ranges.size"
                        or "ranges.empty" or "ranges.at" or "ranges.concat" or "string.join" or "text.append_chunk"
                        or "process.spawn" or "ranges.transform" or "ranges.take" or "ranges.drop" or "ranges.reverse"
                        or "ranges.materialize" or "ranges.sort_with" or "ranges.filter" or "ranges.zip" or "ranges.enumerate_from"
                        or "ranges.all_of" or "ranges.any" or "ranges.any_of" or "ranges.none" or "ranges.none_of"
                        || iteratorCalls.TryGetValue(target, out var operation) && operation == LlvmIteratorOperation.Open:
                    (rangeConsumers ??= []).Add(call);
                    break;
            }
        }

        private bool IsRangeType(int type) => type >= 0 && metadata.Types[type].Kind == TypeKind.Range;

        private RangeFields? SelectEmptyRangeConsumers()
        {
            if (session is not null || rangeWrites is null || (rangeConsumers is null && capturedPathBatches is null))
                return null;
            var facts = new RangeFields(this);
            if (!facts.Record(rangeWrites))
            {
                trace?.Detail?.Metric("range.fields.opaque_writes", 1);
                return null;
            }
            var replacements = new Dictionary<LlvmFunction, LlvmFunction>();
            int selected = 0;
            foreach (LlvmCallInstruction call in (IEnumerable<LlvmCallInstruction>?)rangeConsumers ?? [])
            {
                var old = call.CalledFunction!;
                if (call.ArgumentCount != 1 || !nativeCalls.TryGetValue(old, out var name) || name == "ranges.materialize"
                    || !facts.IsEmpty(call.GetArgument(0))) continue;
                if (!replacements.TryGetValue(old, out var target))
                {
                    target = LlvmFunction.Create(old.FunctionType, LlvmLinkage.ExternalLinkage, "msync.empty." + name, module);
                    nativeCalls.Add(target, name);
                    replacements.Add(old, target);
                    (emptyRangeConsumers ??= []).Add(target);
                }
                // Keep the actual operand and existing path/projection. The
                // selected operation survives even if LLVM merges the loads.
                call.CalledOperand = target;
                ++selected;
            }
            trace?.Detail?.Metric("range.fields.writes", rangeWrites.Count);
            trace?.Detail?.Metric("range.fields.declarations", facts.Declarations);
            trace?.Detail?.Metric("range.fields.empty_consumers", selected);
            return facts;
        }

        private sealed partial class RangeFields(Lowering owner)
        {
            private readonly Dictionary<LlvmFieldCarrier, List<LlvmValue>> writes = [];
            private readonly Dictionary<LlvmFieldCarrier, bool> empty = [];
            private readonly Dictionary<LlvmValue, LlvmFieldCarrier[]?> origins = [];
            private readonly Dictionary<LlvmValue, bool> emptyValues = [];
            private Dictionary<LlvmArgument, List<LlvmValue>>? captures;
            private HashSet<int>? staticLayouts;

            internal int Declarations => writes.Count;

            internal bool Record(List<LlvmStoreInstruction> stores)
            {
                foreach (LlvmStoreInstruction store in stores)
                {
                    if (Lanes(store.PointerOperand) is not { } fields) return false;
                    foreach (LlvmFieldCarrier field in fields)
                    {
                        if (!writes.TryGetValue(field, out var sources)) writes.Add(field, sources = []);
                        sources.Add(store.ValueOperand);
                    }
                }
                return true;
            }

            private bool EmptyField(LlvmFieldCarrier field)
            {
                if (empty.TryGetValue(field, out bool known)) return known;
                empty.Add(field, false);
                return empty[field] = IsGuestField(field)
                    && writes.TryGetValue(field, out var values)
                    && values.All(IsEmpty);
            }

            private bool IsGuestField(LlvmFieldCarrier field)
            {
                staticLayouts ??= owner.staticScopes.Values.Select(scope => scope.Layout).ToHashSet();
                return !staticLayouts.Contains(field.Layout) && !owner.builtinRecords.ContainsKey(field.Layout);
            }

            internal bool IsEmpty(LlvmValue value)
            {
                if (emptyValues.TryGetValue(value, out bool known)) return known;
                emptyValues.Add(value, false);
                return emptyValues[value] = value switch
                {
                    LlvmLoadInstruction load => Lanes(load.PointerOperand) is { Length: > 0 } fields && fields.All(EmptyField),
                    LlvmPhiInstruction phi => phi.IncomingCount != 0 && Enumerable.Range(0, phi.IncomingCount)
                        .Select(phi.GetIncomingValue).All(IsEmpty),
                    LlvmSelectInstruction select => IsEmpty(select.TrueValue) && IsEmpty(select.FalseValue),
                    LlvmCallInstruction { CalledFunction: { } target } call => EmptyCall(call, target),
                    _ => false
                };
            }

            private bool EmptyCall(LlvmCallInstruction call, LlvmFunction target)
            {
                if (owner.nativeCalls.TryGetValue(target, out string? name))
                    return name == "ranges.empty_of" || name == "ranges.take" && call.GetArgument(1) is LlvmConstantFP literal
                        && (literal.Value.BitcastToAPInt().GetZExtValue() & long.MaxValue) == 0;
                if (owner.constructors.GetValueOrDefault(target) == NativeKind.Range) return call.ArgumentCount == 0;
                if (owner.runtimeConversions.GetValueOrDefault(target)?.Operation == LlvmRuntimeConversionOperation.Represent)
                    return IsEmpty(call.GetArgument(0));
                return false;
            }

            private LlvmFieldCarrier[]? Lanes(LlvmValue address)
            {
                if (origins.TryGetValue(address, out var known)) return known;
                origins.Add(address, null);
                return origins[address] = address switch
                {
                    LlvmAllocaInstruction or LlvmGlobalVariable => [],
                    LlvmHeapAllocInst { LogicalLayout: >= 0 } alloc => [new(alloc.LogicalLayout, 0)],
                    LlvmGetElementPtrInstruction { TargetStorageType: { } type, IndexCount: 2 } gep
                        when owner.structLayouts.TryGetValue(type, out int layout) && gep.GetIndex(1) is LlvmConstantInt ordinal
                        => [new(layout, (int)ordinal.Value.GetLimitedValue())],
                    LlvmCallInstruction { CalledFunction: { } target }
                        when owner.valueOperations.GetValueOrDefault(target)?.Projection is { } projection
                        => projection.Cases.Select(lane => new LlvmFieldCarrier(lane.Layout, lane.Field)).ToArray(),
                    LlvmArgument argument => CaptureLanes(argument),
                    LlvmPhiInstruction phi => Merge(Enumerable.Range(0, phi.IncomingCount).Select(phi.GetIncomingValue)),
                    LlvmSelectInstruction select => Merge([select.TrueValue, select.FalseValue]),
                    _ => null
                };
            }

            private LlvmFieldCarrier[]? CaptureLanes(LlvmArgument argument)
            {
                captures ??= owner.CaptureSourceAddresses();
                return captures.TryGetValue(argument, out var values) ? Merge(values) : null;
            }

            private LlvmFieldCarrier[]? Merge(IEnumerable<LlvmValue> values)
            {
                var result = new HashSet<LlvmFieldCarrier>();
                foreach (LlvmValue value in values)
                {
                    if (Lanes(value) is not { } known) return null;
                    result.UnionWith(known);
                }
                return result.ToArray();
            }
        }
    }
}
