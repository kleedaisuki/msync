// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp markAliveBlocks / removeUnreachableBlocks.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocalCfg
{
    public static bool RemoveUnreachableBlocks(LlvmFunction function, LlvmDomTreeUpdater? updater = null,
        LlvmMemorySsaUpdater? memoryUpdater = null, bool foldInstsToUnreachable = true,
        CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        trace = trace?.Detail;
        if (trace is null) return RemoveUnreachableBlocksCore(function, updater, memoryUpdater, foldInstsToUnreachable, null, metrics);
        metrics ??= [];
        return trace.MeasureDetailed("optimizer.llvm-local.remove-unreachable",
            (function, updater, memoryUpdater, foldInstsToUnreachable, trace, metrics),
            static state => RemoveUnreachableBlocksCore(state.function, state.updater, state.memoryUpdater,
                state.foldInstsToUnreachable, state.trace, state.metrics),
            attributes: new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["fold.instructions"] = foldInstsToUnreachable ? "true" : "false"
            }, metrics: _ => metrics);
    }

    private static bool RemoveUnreachableBlocksCore(LlvmFunction function, LlvmDomTreeUpdater? updater,
        LlvmMemorySsaUpdater? memoryUpdater, bool foldInstsToUnreachable, CompilationTrace? trace,
        Dictionary<string, ulong>? metrics)
    {
        bool[] reachable = new bool[function.GetMaxBlockNumber()];
        bool changed = MarkAliveBlocks(function, reachable, updater, foldInstsToUnreachable, trace, metrics);
        List<LlvmBasicBlock> remove = [];
        foreach (LlvmBasicBlock block in function.BasicBlocks)
        {
            if (reachable[block.Number]) continue;
            if (updater?.IsBlockPendingDeletion(block) == true)
            { Count(metrics, "local_cfg.unreachable.already_pending"); continue; }
            remove.Add(block);
        }
        if (remove.Count == 0) return changed;
        Count(metrics, "local_cfg.unreachable.blocks_removed", (ulong)remove.Count);
        memoryUpdater?.RemoveBlocks(remove.ToHashSet());
        LlvmBasicBlockUtils.DeleteDeadBlocks(remove, updater, trace: trace, metrics: metrics);
        return true;
    }

    private static bool MarkAliveBlocks(LlvmFunction function, bool[] reachable, LlvmDomTreeUpdater? updater,
        bool foldInstsToUnreachable, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        List<LlvmBasicBlock> work = [function.BasicBlocks[0]];
        reachable[work[0].Number] = true;
        bool changed = false;
        do
        {
            LlvmBasicBlock block = work[^1];
            work.RemoveAt(work.Count - 1);
            Count(metrics, "local_cfg.unreachable.reachable_blocks");
            if (foldInstsToUnreachable)
            {
                changed |= FoldInstructionsToUnreachable(block, updater, trace, metrics);
                changed |= ConstantFoldTerminator(block, true, updater, metrics);
            }
            foreach (LlvmBasicBlock successor in block.GetTerminator().Successors)
            {
                Count(metrics, "local_cfg.unreachable.edges_examined");
                if (reachable[successor.Number]) continue;
                work.Add(successor);
                reachable[successor.Number] = true;
            }
        } while (work.Count != 0);
        return changed;
    }

    private static bool FoldInstructionsToUnreachable(LlvmBasicBlock block, LlvmDomTreeUpdater? updater,
        CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        foreach (LlvmInstruction instruction in block.Instructions)
        {
            Count(metrics, "local_cfg.unreachable.instructions_examined");
            if (instruction is LlvmCallInstruction call)
            {
                string? intrinsic = call.CalledFunction?.IntrinsicDescriptor?.Kind.Name;
                if (intrinsic == "llvm.assume" &&
                    call.GetArgument(0) is LlvmUndefValue or LlvmConstant { IsNullValue: true })
                {
                    ObserveUnreachableRewrite(call, "false-or-undef-assumption", updater, trace, metrics);
                    return true;
                }
                if (intrinsic == "llvm.experimental.guard" &&
                    call.GetArgument(0) is LlvmConstant { IsNullValue: true } && call.Next is not LlvmUnreachableInstruction)
                {
                    ObserveUnreachableRewrite(call.Next!, "false-guard", updater, trace, metrics);
                    return true;
                }
                // Only a real registered intrinsic descriptor supplies the
                // identity above; ordinary names never acquire that meaning.
                if (call.CalledOperand is not LlvmFunction &&
                    (call.CalledOperand is LlvmUndefValue ||
                     call.CalledOperand is LlvmConstantPointerNull nullCallee &&
                     !LlvmFunction.NullPointerIsDefined(block.Parent, nullCallee.PointerType.AddressSpace)))
                {
                    ObserveUnreachableRewrite(instruction, "invalid-callee", updater, trace, metrics);
                    return true;
                }
                if (!call.DoesNotReturn || call.TailCallKind == LlvmTailCallKind.MustTail) continue;
                if (call.Next is LlvmUnreachableInstruction) return false;
                ObserveUnreachableRewrite(call.Next!, "noreturn-call", updater, trace, metrics);
                return true;
            }
            if (instruction is not LlvmStoreInstruction { IsVolatile: false } store) continue;
            LlvmValue pointer = store.PointerOperand;
            if (pointer is not LlvmUndefValue &&
                (pointer is not LlvmConstantPointerNull nullPointer ||
                 LlvmFunction.NullPointerIsDefined(block.Parent, nullPointer.PointerType.AddressSpace))) continue;
            ObserveUnreachableRewrite(store, "invalid-store-pointer", updater, trace, metrics);
            return true;
        }
        return false;
    }

    private static void ObserveUnreachableRewrite(LlvmInstruction first, string cause,
        LlvmDomTreeUpdater? updater, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        if (metrics is not null) Count(metrics, "local_cfg.unreachable.fold." + cause);
        if (trace is null) { ChangeToUnreachable(first, false, updater, metrics: metrics); return; }
        Dictionary<string, string> attributes = new()
        {
            ["function.name"] = first.Parent!.Parent!.Name,
            ["block.name"] = first.Parent.Name,
            ["instruction.opcode"] = first.Opcode.ToString(),
            ["fold.cause"] = cause
        };
        if (first.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        trace.Run("optimizer.llvm-local.fold-to-unreachable", (first, updater, metrics),
            static state => ChangeToUnreachable(state.first, false, state.updater, metrics: state.metrics), attributes);
    }
}
