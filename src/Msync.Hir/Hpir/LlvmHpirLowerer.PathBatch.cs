using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly List<HpirFunction> PathBatchWorkers = [];
        private readonly Dictionary<LlvmValue, LlvmFunction?> pathBatchRecipes = [];
        private readonly Dictionary<(LlvmFunction Callback, bool Observed), int> pathBatchImports = [];
        private readonly Dictionary<LlvmFunction, int> pathBatchEntries = [];
        private int pathBatchStorage = -1;

        internal int PathBatchStorage() => pathBatchStorage >= 0 ? pathBatchStorage
            : pathBatchStorage = AddLayout(HpirPathImports.BorrowedRecord());

        internal LlvmFunction? CapturedPathMapper(LlvmCallInstruction call) =>
            call.CalledFunction is { } target && graph.PathBatchCarriers is { } hosts
                ? hosts.GetValueOrDefault(target) : null;

        internal LlvmFunction? PathBatchCallback(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (pathBatchRecipes.TryGetValue(value, out var present)) return present;
            pathBatchRecipes.Add(value, null);
            if (value is LlvmCallInstruction forwarded && CapturedPathMapper(forwarded) is { } retained)
                return pathBatchRecipes[value] = retained;
            if (value is LlvmCallInstruction call && NativeRangeCall(call, "ranges.transform") && call.ArgumentCount == 2
                && VectorShape(call.GetArgument(0)) is { } source
                && ReadyUnaryCallback(call.GetArgument(1)) is { } callback && IsPathRecord(callback.ReturnType)
                && callback.Arguments[Captures(callback)].Type == callback.ReturnType
                && (source.Empty || source.Element == callback.ReturnType))
            {
                CheckPathRecord(callback.ReturnType, call.Source);
                return pathBatchRecipes[value] = callback;
            }
            if (RangeInputs(value) is not { Count: > 0 } inputs || PathBatchCallback(inputs[0]) is not { } selected)
                return null;
            for (int index = 1; index < inputs.Count; ++index)
                if (PathBatchCallback(inputs[index]) != selected) return null;
            return pathBatchRecipes[value] = selected;
        }

        internal int PathBatchImport(LlvmFunction callback, bool observed)
        {
            var key = (callback, observed);
            if (pathBatchImports.TryGetValue(key, out int import)) return import;
            if (!pathBatchEntries.TryGetValue(callback, out int entry))
            {
                entry = entries.Count + PathBatchWorkers.Count;
                PathBatchWorkers.Add(PathBatchWorker(callback));
                pathBatchEntries.Add(callback, entry);
            }
            import = Imports.Count;
            Imports.Add(HpirPathBatchImports.Invoke(entry, observed));
            return pathBatchImports[key] = import;
        }

        // The worker advances the same snapshot and passes actual byte addresses to
        // the selected callback. Only the two owners are roots across the CALL.
        private HpirFunction PathBatchWorker(LlvmFunction callback)
        {
            const int plan = 0, result = 8, source = 16, target = 24, remaining = 32, data = 40, one = 48, zero = 56, test = 64;
            var code = new List<HpirInstruction>
            {
                new(HpirOp.CopyU64, source, ~plan),
                new(HpirOp.CopyU64, remaining, ~source),
                new(HpirOp.OffsetPointer, data, source, Immediate: HpirTransformImports.VectorData),
                new(HpirOp.CopyU64, data, ~data),
                new(HpirOp.CopyU64, target, result),
                new(HpirOp.ConstU64, one, Immediate: 1),
                new(HpirOp.ConstU64, zero)
            };
            int captures = Captures(callback), offset = 72;
            var arguments = new List<int> { target };
            if (captures != 0)
            {
                int environment = offset;
                offset += 8;
                code.Add(new(HpirOp.OffsetPointer, environment, plan, Immediate: HpirPathBatchImports.Environment));
                code.Add(new(HpirOp.CopyU64, environment, ~environment));
                for (int index = 0; index < captures; ++index, offset += 8)
                {
                    code.Add(new(HpirOp.OffsetPointer, offset, environment, Immediate: index * 8));
                    code.Add(new(HpirOp.CopyU64, offset, ~offset));
                    arguments.Add(offset);
                }
            }
            int head = code.Count;
            code.Add(new(HpirOp.EqU64, test, remaining, zero));
            int check = code.Count;
            code.Add(new(HpirOp.Branch, D: check + 1, A: test));
            foreach (Leaf leaf in Leaves(callback.ReturnType))
            {
                code.Add(new(HpirOp.OffsetPointer, offset, data, Immediate: leaf.Offset));
                arguments.Add(~offset);
                offset += 8;
            }
            int invoke = code.Count;
            code.Add(new(HpirOp.Call, A: 0));
            code.Add(new(HpirOp.OffsetPointer, data, data, Immediate: HpirPathImports.PathBytes));
            code.Add(new(HpirOp.OffsetPointer, target, target, Immediate: HpirPathImports.PathBytes));
            code.Add(new(HpirOp.SubU64, remaining, remaining, one));
            code.Add(new(HpirOp.Jump, A: head));
            code[check] = code[check] with { B = code.Count };
            code.Add(new(HpirOp.Return));
            HpirSource[] sources = callback.BasicBlocks[0].FirstInstruction?.Source is { } span
                ? [new(0, span)] : [];
            return new($"<path-batch:{callback.Name}>", offset, HpirWidth.Void,
                [new(plan, HpirWidth.U64), new(result, HpirWidth.U64)], code.ToArray(),
                [new(entries[callback], arguments.ToArray())], sources)
            { Safepoints = [new(invoke, [plan, result])] };
        }
    }

    private sealed partial class Body
    {
        private int pathBatchPlans, pathBatchSites;

        private bool TryPathTransform(LlvmCallInstruction call)
        {
            if (geometry.CapturedPathMapper(call) is not null)
            {
                Alias(call, Location(call.GetArgument(0)));
                return true;
            }
            if (!geometry.NativeRangeCall(call, "ranges.transform") || geometry.PathBatchCallback(call) is null) return false;
            int output = Location(call);
            EmitCall(call, ~geometry.RecordTransformAllocator(), output, []);
            Copy(HpirWidth.U64, ~output, Location(call.GetArgument(0)), call.Source);
            Copy(HpirWidth.U64, Indirect(output, HpirPathBatchImports.Environment, call), LeafLocation(call.GetArgument(1), 8), call.Source);
            allocatedTransforms.Add(call);
            ++pathBatchPlans;
            return true;
        }

        private bool TryPathBatchTransfer(LlvmFileOutInst transfer)
        {
            if (geometry.PathBatchCallback(transfer.Value) is not { } callback) return false;
            if (geometry.ReductionWork(callback) == LlvmRangeCallbackWork.Other)
                throw Error("a Path batch callback requiring file effects or unscoped work", transfer);
            bool observed = !transfer.UseEmpty;
            int layout = Reserve(4, 4), future = Reserve(4, 4);
            Add(new(HpirOp.ConstU32, layout, Immediate: geometry.PathBatchStorage()), transfer.Source);
            Add(new(HpirOp.ConstU32, future, Immediate: geometry.TransferCompletionLayout(observed)), transfer.Source);
            EmitCall(transfer, ~geometry.PathBatchImport(callback, observed), observed ? Location(transfer) : -1,
                [Location(transfer.Value), PathRecordAddress(transfer.Destination, transfer), layout, future]);
            ++pathBatchSites;
            return true;
        }

        private void ReportPathBatches(CompilationTrace? trace)
        {
            if (pathBatchPlans != 0) trace?.Metric("paths.batch.plans", pathBatchPlans);
            if (pathBatchSites != 0) trace?.Metric("paths.batch.sites", pathBatchSites);
        }
    }
}
