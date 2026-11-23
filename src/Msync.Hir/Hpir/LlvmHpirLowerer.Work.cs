using System.Globalization;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<HpirWorkOperation, int> workImports = [];

        internal LlvmRuntimeValueBinding? WorkBinding(LlvmFunction target) =>
            graph.ValueOperations.GetValueOrDefault(target);

        internal bool AcquireTarget(LlvmFunction target) => WorkBinding(target)?.Operation == LlvmRuntimeValueOperation.Acquire;

        internal int WorkImport(HpirWorkOperation operation)
        {
            if (workImports.TryGetValue(operation, out int import)) return import;
            import = Imports.Count;
            Imports.Add(HpirWorkImports.Invoke(operation));
            return workImports[operation] = import;
        }
    }

    private sealed partial class Body
    {
        private Dictionary<int, int>? frontierMarks;
        private int frontierPublications, ownerJoins, processJoins;

        private void ReportWork(CompilationTrace? trace)
        {
            if (frontierMarks is not null) trace?.Metric("effects.work.lexical_frontiers", frontierMarks.Count);
            if (frontierPublications != 0) trace?.Metric("effects.work.lexical_exits", frontierPublications);
            if (ownerJoins != 0) trace?.Metric("effects.work.owner_joins", ownerJoins);
            if (processJoins != 0) trace?.Metric("effects.work.process_joins", processJoins);
        }

        private bool TryWorkCall(LlvmCallInstruction call, LlvmFunction target)
        {
            var binding = geometry.WorkBinding(target);
            if (binding is { Operation: LlvmRuntimeValueOperation.Barrier, Symbol: { } region })
            {
                // HIR already places each close on its exact normal or abrupt
                // edge. Reserve one physical mark per lexical region in this frame.
                int id = int.Parse(region, CultureInfo.InvariantCulture);
                frontierMarks ??= [];
                if (!frontierMarks.TryGetValue(id, out int mark))
                    frontierMarks.Add(id, mark = Reserve(4, 4));
                if (binding.Mode != 0)
                    EmitCall(call, ~geometry.WorkImport(HpirWorkOperation.Mark), mark, [], safepoint: false);
                else
                {
                    ++frontierPublications;
                    EmitCall(call, ~geometry.WorkImport(HpirWorkOperation.Publish), -1, [mark], safepoint: false);
                }
                return true;
            }
            if (binding?.Operation != LlvmRuntimeValueOperation.Acquire) return false;
            if (call.ArgumentCount == 0)
            {
                ++ownerJoins;
                EmitCall(call, ~geometry.WorkImport(HpirWorkOperation.Join), -1, []);
                return true;
            }
            LlvmValue handle = call.GetArgument(0);
            if (processHandles?.Contains(handle) != true)
                throw Error("acquire needs a selected Process handle", call);
            ++processJoins;
            EmitCall(call, ~geometry.WorkImport(HpirWorkOperation.JoinProcess), Location(call), [Location(handle)]);
            return true;
        }
    }
}
