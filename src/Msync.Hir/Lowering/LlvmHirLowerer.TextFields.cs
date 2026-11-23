using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private HashSet<LlvmFieldCarrier>? textCompletionFields;
        private HashSet<LlvmFunction>? completedProcessText;

        private void SelectCompletedProcessText()
        {
            if (readinessSources is null) return;
            Dictionary<LlvmFunction, LlvmFunction>? selected = null;
            int projections = 0;
            foreach (var (value, _) in readinessSources)
            {
                if (value is not LlvmCallInstruction { CalledFunction: { } target } call
                    || nativeCalls.GetValueOrDefault(target) is not ("process.stdout" or "process.stderr")
                    || !AlreadyWaitedProcess(call)) continue;
                selected ??= [];
                if (!selected.TryGetValue(target, out var projection))
                {
                    projection = LlvmFunction.Create(target.FunctionType, LlvmLinkage.ExternalLinkage,
                        target.Name + ".completed", module);
                    nativeCalls.Add(projection, nativeCalls[target]);
                    (completedProcessText ??= []).Add(projection);
                    selected.Add(target, projection);
                }
                call.CalledOperand = projection;
                ++projections;
            }
            if (projections != 0) trace?.Detail?.Metric("flow.completed_process_text_projections", projections);
        }

        private bool AlreadyWaitedProcess(LlvmCallInstruction projection)
        {
            LlvmValue handle = projection.GetArgument(0);
            if (handle is LlvmLoadInstruction { PointerOperand: LlvmAllocaInstruction location } load
                && load.Parent == projection.Parent
                && SingleProducerStore(location, projection.Parent!) is { } store && store.ComesBefore(load))
                handle = store.ValueOperand;
            // A wait returns the completed handle; an ordinary ProcessCall
            // also waits. A released Process, or a mutable alias, is not ready.
            return handle is LlvmCallInstruction { CalledFunction: { } origin }
                && (nativeCalls.GetValueOrDefault(origin) == "process.wait"
                    || valueOperations.GetValueOrDefault(origin) is { Operation: LlvmRuntimeValueOperation.ProcessCall } call
                        && (call.Mode & 1) == 0);
        }

        private void SelectTextCompletionFields()
        {
            if (session is not null || readinessSources is null) return;
            var pending = new Queue<LlvmValue>();
            var seen = new HashSet<LlvmValue>();
            foreach (var (value, _) in readinessSources)
                if (value is LlvmFileInInst { Format: LlvmFileInFormat.Text }
                    || value is LlvmCallInstruction { CalledFunction: { } target }
                    && completedProcessText?.Contains(target) != true
                    && nativeCalls.GetValueOrDefault(target) is ("io.read_line" or "io.read_stdin" or "process.stdout" or "process.stderr"))
                    Enqueue(value);
            while (pending.TryDequeue(out var source))
                foreach (LlvmUser user in source.Users)
                {
                    if (user is LlvmStoreInstruction store && store.ValueOperand == source)
                    {
                        RecordTextCompletionField(store.PointerOperand);
                        continue;
                    }
                    if (user is LlvmPhiInstruction or LlvmSelectInstruction
                        || user is LlvmCallInstruction { CalledFunction: { } target } narrow
                        && narrow.ArgumentCount == 1 && narrow.GetArgument(0) == source
                        && runtimeConversions.GetValueOrDefault(target)?.Operation == LlvmRuntimeConversionOperation.Represent)
                        Enqueue(user);
                }
            trace?.Detail?.Metric("text.completion_fields", textCompletionFields?.Count ?? 0);

            void Enqueue(LlvmValue value) { if (seen.Add(value)) pending.Enqueue(value); }
        }

        private void RecordTextCompletionField(LlvmValue address)
        {
            if (address is LlvmGetElementPtrInstruction { TargetStorageType: { } structure, IndexCount: 2 } field
                && field.GetIndex(1) is LlvmConstantInt ordinal && structLayouts.TryGetValue(structure, out int layout))
                Record(layout, checked((int)ordinal.Value.GetLimitedValue()));
            else if (address is LlvmCallInstruction { CalledFunction: { } target }
                && valueOperations.GetValueOrDefault(target)?.Projection is { } projection)
                foreach (var lane in projection.Cases) Record(lane.Layout, lane.Field);

            void Record(int layout, int ordinal)
            {
                if (builtinRecords.ContainsKey(layout)) return;
                var type = metadata.Types[metadata.Layouts[layout].Fields[ordinal].Type];
                if ((type.Kind & ~TypeKind.Null) == TypeKind.String)
                    (textCompletionFields ??= []).Add(new(layout, ordinal));
            }
        }
    }
}
