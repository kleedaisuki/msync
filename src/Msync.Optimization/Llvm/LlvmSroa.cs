// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/SROA.cpp driver and SROA.h options.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed record LlvmSroaOptions
{
    public bool PreserveCFG { get; init; } = true;
    public bool AggregateToVector { get; init; }
}

public readonly record struct LlvmSroaResult(bool Changed, bool CFGChanged)
{
    public bool Promoted { get; init; }
}

public static partial class LlvmSroa
{
    public static LlvmSroaResult Run(LlvmFunction function, LlvmDominatorTree tree,
        LlvmAssumptionCache? assumptions = null, LlvmSroaOptions? options = null,
        CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        options ??= new();
        using var updater = new LlvmDomTreeUpdater(tree, null, LlvmDomTreeUpdateStrategy.Lazy, trace);
        var state = new State(function, updater, assumptions, options, trace);
        if (trace is null) return state.Run();
        return trace.MeasureDetailed("optimizer.pass.llvm-sroa", state, static pass => pass.Run(),
            attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => state.Metrics!);
    }

    internal sealed class DeadInstructionList : IDisposable
    {
        private readonly List<LlvmWeakValueHandle> values = [];
        internal int Count => values.Count;
        internal void Add(LlvmInstruction instruction) => values.Add(new(instruction));
        internal LlvmWeakValueHandle PopBack()
        {
            var result = values[^1];
            values.RemoveAt(values.Count - 1);
            return result;
        }
        public void Dispose() { foreach (var handle in values) handle.Dispose(); values.Clear(); }
    }

    internal sealed partial class State
    {
        internal readonly LlvmFunction Function;
        internal readonly LlvmContext C;
        internal readonly LlvmDataLayout DL;
        internal readonly LlvmDomTreeUpdater DTU;
        internal readonly LlvmAssumptionCache? AC;
        internal readonly bool PreserveCFG;
        internal readonly bool AggregateToVector;
        internal readonly CompilationTrace? Trace;
        internal readonly Dictionary<string, ulong>? Metrics;
        internal readonly OrderedSet<LlvmAllocaInstruction> Worklist = new();
        internal readonly OrderedSet<LlvmAllocaInstruction> PostPromotionWorklist = new();
        internal readonly OrderedSet<LlvmAllocaInstruction> PromotableAllocas = new();
        internal readonly DeadInstructionList DeadInsts = new();
        internal readonly OrderedSet<LlvmPhiInstruction> SpeculatablePHIs = new();
        internal readonly OrderedSet<LlvmPhiInstruction> PHIsWithStoreToRewrite = new();
        internal readonly OrderedMap<LlvmSelectInstruction, RewriteableMemOps> SelectsToRewrite = new();

        internal State(LlvmFunction function, LlvmDomTreeUpdater updater, LlvmAssumptionCache? assumptions,
            LlvmSroaOptions options, CompilationTrace? trace)
        {
            Function = function;
            C = function.Context;
            DL = function.Parent!.DataLayout;
            DTU = updater;
            AC = assumptions;
            PreserveCFG = options.PreserveCFG;
            AggregateToVector = options.AggregateToVector;
            Trace = trace;
            Metrics = trace is null ? null : [];
        }

        internal void Count(string key, ulong amount = 1)
        {
            if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount;
        }
        internal void AddDead(LlvmInstruction instruction) => DeadInsts.Add(instruction);
        internal void ClobberUse(LlvmUse use)
        {
            var old = use.Value!;
            use.Set(LlvmPoisonValue.Get(old.Type));
            if (old is LlvmInstruction instruction && LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                AddDead(instruction);
        }

        internal LlvmSroaResult Run()
        {
            using var pending = DeadInsts;
            if (Function.BasicBlocks.Count == 0) return default;
            foreach (var instruction in Function.BasicBlocks[0].Instructions)
            {
                if (instruction is not LlvmAllocaInstruction alloca) continue;
                var size = alloca.GetAllocationSize(DL);
                if (size is { IsScalable: true } && LlvmPromoteMemoryToRegister.IsAllocaPromotable(alloca))
                    PromotableAllocas.Add(alloca);
                else Worklist.Add(alloca);
            }
            bool changed = false, cfgChanged = false, promoted = false;
            HashSet<LlvmAllocaInstruction> deleted = [];
            do
            {
                while (Worklist.Count != 0)
                {
                    var alloca = Worklist.PopBack();
                    var iteration = RunOnAlloca(alloca);
                    changed |= iteration.Changed;
                    cfgChanged |= iteration.CFGChanged;
                    changed |= Trace is null ? DeleteDeadInstructions(alloca, deleted) :
                        Trace.MeasureDetailed("optimizer.llvm-sroa.delete", (Pass: this, Alloca: alloca, Deleted: deleted),
                            static state => state.Pass.DeleteDeadInstructions(state.Alloca, state.Deleted),
                            attributes: new Dictionary<string, string> { ["function.name"] = Function.Name },
                            metrics: _ => Metrics!);
                    if (deleted.Count == 0) continue;
                    Worklist.ExceptWith(deleted);
                    PostPromotionWorklist.ExceptWith(deleted);
                    PromotableAllocas.ExceptWith(deleted);
                    deleted.Clear();
                }
                bool promotedIteration = PromoteAllocas();
                changed |= promotedIteration;
                promoted |= promotedIteration;
                Worklist.AddRange(PostPromotionWorklist.TakeVector());
            } while (Worklist.Count != 0);
            Debug.Assert(!cfgChanged || changed);
            Debug.Assert(!cfgChanged || !PreserveCFG);
            Count("sroa.changed", changed ? 1UL : 0);
            Count("sroa.cfg_changed", cfgChanged ? 1UL : 0);
            return new(changed, cfgChanged) { Promoted = promoted };
        }

        private bool PromoteAllocas()
        {
            if (PromotableAllocas.Count == 0) return false;
            Count("sroa.allocas.promoted", (ulong)PromotableAllocas.Count);
            LlvmPromoteMemoryToRegister.PromoteMemToReg(PromotableAllocas.Values, DTU.GetTree()!, Trace, AC);
            PromotableAllocas.Clear();
            return true;
        }
    }
}
