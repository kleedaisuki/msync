// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/SROA.cpp 6094–6310,6409–6551.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal sealed partial class State
    {
        private LlvmSroaResult RunOnAlloca(LlvmAllocaInstruction alloca)
        {
            if (Trace is null) return AnalyzeAlloca(alloca);
            var attributes = new Dictionary<string, string>
            { ["function.name"] = Function.Name, ["alloca.name"] = alloca.Name };
            if (alloca.Source is { } source)
            {
                attributes["source.path"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return Trace.MeasureDetailed("optimizer.llvm-sroa.alloca", alloca, AnalyzeAlloca,
                attributes: attributes, metrics: _ => Metrics!);
        }

        private LlvmSroaResult AnalyzeAlloca(LlvmAllocaInstruction alloca)
        {
            Count("sroa.allocas.analyzed");
            if (alloca.UseEmpty) { AddDead(alloca); return new(true, false); }
            var size = alloca.GetAllocationSize(DL);
            if (alloca.IsArrayAllocation || size is null || size.Value.IsScalable || size.Value.IsZero)
            {
                Count("sroa.allocas.unsupported_allocation_form");
                return default;
            }
            var builder = new LlvmTransformBuilder(DL, Metrics);
            builder.SetInsertPoint(alloca);
            builder.DisableGEPCanonicalization();
            bool changed = new AggLoadStoreRewriter(DL, builder, this).Rewrite(alloca);
            bool cfgChanged = false;
            var slices = new AllocaSlices(DL, alloca, Metrics);
            Count("sroa.slices", (ulong)slices.Slices.Count);
            if (slices.IsEscaped)
            {
                Count("sroa.allocas.escaped");
                if (Metrics is not null) Count("sroa.escape.opcode." + slices.PointerEscapingInstruction!.Opcode);
                return new(changed, false);
            }
            if (slices.IsEscapedReadOnly)
            {
                Count("sroa.allocas.escaped_readonly");
                return new(changed | PropagateStoredValuesToLoads(alloca, slices), false);
            }
            foreach (var dead in slices.DeadUsers)
            {
                foreach (var operand in dead.OperandUses) ClobberUse(operand);
                dead.ReplaceAllUsesWith(LlvmPoisonValue.Get(dead.Type));
                AddDead(dead);
                changed = true;
            }
            foreach (var operand in slices.DeadOperands) { ClobberUse(operand); changed = true; }
            if (slices.Slices.Count == 0) return new(changed, false);
            changed |= Trace is null ? SplitAlloca(alloca, slices) :
                Trace.MeasureDetailed("optimizer.llvm-sroa.split", (Pass: this, Alloca: alloca, Slices: slices),
                    static state => state.Pass.SplitAlloca(state.Alloca, state.Slices),
                    attributes: new Dictionary<string, string> { ["function.name"] = Function.Name, ["alloca.name"] = alloca.Name },
                    metrics: _ => Metrics!);
            cfgChanged |= Trace is null ? RewriteMerges(builder) :
                Trace.MeasureDetailed("optimizer.llvm-sroa.phi-select", (Pass: this, Builder: builder),
                    static state => state.Pass.RewriteMerges(state.Builder),
                    attributes: new Dictionary<string, string> { ["function.name"] = Function.Name },
                    metrics: _ => Metrics!);
            return new(changed, cfgChanged);
        }

        private bool RewriteMerges(LlvmTransformBuilder builder)
        {
            bool cfgChanged = false;
            while (SpeculatablePHIs.Count != 0)
            {
                Count("sroa.phis.speculated");
                SpeculatePHINodeLoads(builder, SpeculatablePHIs.PopBack());
            }
            var phis = PHIsWithStoreToRewrite.TakeVector();
            for (int i = phis.Count - 1; i >= 0; --i)
            {
                var phi = phis[i];
                var store = (LlvmStoreInstruction)phi.Users.Last();
                Count("sroa.phi_stores.rewritten");
                cfgChanged |= RewritePHINodeStore(phi, store, DTU, Worklist);
            }
            var selects = SelectsToRewrite.TakeVector();
            for (int i = selects.Count - 1; i >= 0; --i)
            {
                Count("sroa.selects.rewritten");
                cfgChanged |= RewriteSelectInstMemOps(selects[i].Key, selects[i].Value, builder, PreserveCFG ? null : DTU);
            }
            return cfgChanged;
        }

        private bool SplitAlloca(LlvmAllocaInstruction alloca, AllocaSlices slices)
        {
            if (slices.Slices.Count == 0) return false;
            bool changed = PresplitLoadsAndStores(alloca, slices);
            bool sorted = true;
            ulong allocaSize = alloca.GetAllocationSize(DL)!.Value.GetFixedValue();
            HashSet<ulong> splittableOffsets = [];
            ulong currentBegin = 0, currentEnd = 0;
            foreach (var slice in slices.Slices)
            {
                if (slice.BeginOffset <= currentBegin && slice.EndOffset <= currentEnd) continue;
                if (slice.BeginOffset >= currentEnd) splittableOffsets.Add(slice.BeginOffset);
                if (currentEnd > slice.BeginOffset && currentEnd < slice.EndOffset) splittableOffsets.Remove(currentEnd);
                currentBegin = slice.BeginOffset;
                if (slice.EndOffset <= currentEnd) continue;
                currentEnd = slice.EndOffset;
                splittableOffsets.Add(currentEnd);
            }
            foreach (var slice in slices.Slices)
            {
                if (!slice.IsSplittable) continue;
                if ((slice.BeginOffset > allocaSize || splittableOffsets.Contains(slice.BeginOffset)) &&
                    (slice.EndOffset > allocaSize || splittableOffsets.Contains(slice.EndOffset))) continue;
                if (slice.Use!.User is not (LlvmLoadInstruction or LlvmStoreInstruction)) continue;
                slice.MakeUnsplittable();
                sorted = false;
            }
            if (!sorted) slices.SortSlices();
            ulong partitions = 0;
            foreach (var partition in slices.Partitions())
            {
                var rewritten = RewritePartition(alloca, slices, partition);
                changed |= rewritten.Alloca is not null;
                ++partitions;
            }
            Count("sroa.partitions", partitions);
            if (Metrics is not null) Metrics["sroa.partitions.max_per_alloca"] =
                Math.Max(Metrics.GetValueOrDefault("sroa.partitions.max_per_alloca"), partitions);
            return changed;
        }

        private bool DeleteDeadInstructions(LlvmAllocaInstruction alloca, HashSet<LlvmAllocaInstruction> deleted)
        {
            bool changed = false;
            while (DeadInsts.Count != 0)
            {
                using var handle = DeadInsts.PopBack();
                if (handle.Value is not LlvmInstruction instruction) continue;
                if (instruction is LlvmAllocaInstruction deadAlloca) deleted.Add(deadAlloca);
                instruction.ReplaceAllUsesWith(LlvmUndefValue.Get(instruction.Type));
                foreach (var operand in instruction.OperandUses)
                {
                    if (operand.Value is not LlvmInstruction producer) continue;
                    operand.Set(null);
                    if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(producer, Metrics)) AddDead(producer);
                }
                Count("sroa.instructions.deleted");
                instruction.EraseFromParent();
                changed = true;
            }
            if (!deleted.Contains(alloca) && alloca.HasNUndroppableUses(0))
            {
                deleted.Add(alloca);
                alloca.DropDroppableUses();
                Count("sroa.instructions.deleted");
                alloca.EraseFromParent();
                changed = true;
            }
            return changed;
        }
    }
}
