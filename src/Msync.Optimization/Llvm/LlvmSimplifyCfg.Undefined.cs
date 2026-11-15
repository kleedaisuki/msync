// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp 9211-9418.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool PassingValueIsAlwaysUndefined(LlvmValue value, LlvmInstruction instruction,
            bool ptrValueMayBeModified = false)
        {
            Count("undefined-value.queries");
            if (value is not LlvmConstant constant || instruction.UseEmpty ||
                !constant.IsNullValue && constant is not LlvmUndefValue) return false;
            LlvmUse? witness = null;
            foreach (LlvmUse use in instruction.Uses)
            {
                Count("undefined-value.uses");
                if (use.User is not LlvmInstruction user || user.Parent != instruction.Parent || user == instruction ||
                    user.ComesBefore(instruction)) continue;
                if (user.Opcode is LlvmOpcode.GetElementPtr or LlvmOpcode.Ret or LlvmOpcode.BitCast or LlvmOpcode.Load or
                    LlvmOpcode.Store or LlvmOpcode.Call or LlvmOpcode.UDiv or LlvmOpcode.URem or LlvmOpcode.SDiv or LlvmOpcode.SRem)
                { witness = use; break; }
            }
            if (witness is null || witness.User is not LlvmInstruction consumer) return false;
            for (LlvmInstruction? cursor = instruction.Next; cursor != consumer; cursor = cursor!.Next)
                if (!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(cursor!, Metrics)) return false;
            if (consumer is LlvmGetElementPtrInstruction gep && gep.PointerOperand == instruction)
            {
                if (gep.Type.IsVectorTy()) return false;
                if (gep.Indices.Any(index => index is not LlvmConstantInt { IsZero: true }) &&
                    (!gep.NoWrapFlags.IsInBounds || NullPointerDefined(gep, gep.PointerOperand))) ptrValueMayBeModified = true;
                return PassingValueIsAlwaysUndefined(value, gep, ptrValueMayBeModified);
            }
            // The represented function/call attribute carriers currently have no
            // return noundef/nonnull or parameter nonnull attributes.
            if (consumer is LlvmLoadInstruction { IsVolatile: false } load) return !NullPointerDefined(load, load.PointerOperand);
            if (consumer is LlvmStoreInstruction { IsVolatile: false } store)
                return !NullPointerDefined(store, store.PointerOperand) && store.PointerOperand == instruction;
            if (consumer is LlvmCallInstruction call)
            {
                if (call.CalledFunction?.IntrinsicDescriptor is { IsAssume: true } &&
                    call.GetArgument(0) == instruction) return true;
                if (constant.IsNullValue && LlvmFunction.NullPointerIsDefined(call.Parent!.Parent)) return false;
                if (call.CalledOperand == instruction) return true;
                if (constant is LlvmUndefValue && witness.OperandNo < call.ArgumentCount &&
                    call.IsPassingUndefUB(witness.OperandNo)) return true;
            }
            return consumer is LlvmBinaryOperator binary && binary.GetOperand(1) == instruction &&
                binary.Opcode is LlvmOpcode.UDiv or LlvmOpcode.URem or LlvmOpcode.SDiv or LlvmOpcode.SRem;
        }
        private static bool NullPointerDefined(LlvmInstruction instruction, LlvmValue pointer) =>
            LlvmFunction.NullPointerIsDefined(instruction.Parent!.Parent, ((LlvmPointerType)pointer.Type).AddressSpace);

        private bool RemoveUndefIntroducingPredecessor(LlvmBasicBlock block)
        {
            for (LlvmInstruction? cursor = block.FirstInstruction; cursor is LlvmPhiInstruction phi; cursor = cursor.Next)
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    if (!PassingValueIsAlwaysUndefined(phi.GetIncomingValue(index), phi)) continue;
                    LlvmBasicBlock predecessor = phi.GetIncomingBlock(index);
                    LlvmInstruction terminator = predecessor.GetTerminator();
                    var builder = new LlvmTransformBuilder(terminator, Metrics) { CurrentSource = terminator.Source };
                    if (terminator is LlvmUncondBrInstruction)
                    {
                        RemovePredecessor(block, predecessor);
                        builder.CreateUnreachable();
                    }
                    else if (terminator is LlvmCondBrInstruction branch)
                    {
                        RemovePredecessor(block, predecessor);
                        if (branch.GetSuccessor(0) == branch.GetSuccessor(1))
                        { RemovePredecessor(block, predecessor); builder.CreateUnreachable(); }
                        else
                        {
                            CreateAssumption(builder, branch.GetSuccessor(0) == block ? builder.CreateNot(branch.Condition) : branch.Condition);
                            builder.CreateBr(branch.GetSuccessor(0) == block ? branch.GetSuccessor(1) : branch.GetSuccessor(0));
                        }
                    }
                    else if (terminator is LlvmSwitchInstruction @switch)
                    {
                        var unreachable = new LlvmBasicBlock(block.Context, "unreachable", block.Parent, block);
                        builder.SetInsertPoint(unreachable);
                        builder.CreateUnreachable();
                        for (int caseIndex = 0; caseIndex < @switch.CaseCount; ++caseIndex)
                            if (@switch.GetCaseDestination(caseIndex) == block)
                            { RemovePredecessor(block, predecessor); @switch.SetSuccessor(caseIndex + 1, unreachable); }
                        if (@switch.DefaultDestination == block)
                        { RemovePredecessor(block, predecessor); @switch.DefaultDestination = unreachable; }
                        DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, predecessor, unreachable),
                            new(LlvmCfgUpdateKind.Delete, predecessor, block)]);
                        Count("undefined-value.switch-edges-redirected");
                        return true;
                    }
                    else continue;
                    terminator.EraseFromParent();
                    DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, predecessor, block)]);
                    Count("undefined-value.predecessors-removed");
                    return true;
                }
            return false;
        }
        private void RemovePredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor) =>
            LlvmBasicBlockUtils.RemovePredecessor(block, predecessor, trace: Trace, metrics: Metrics);
        private void CreateAssumption(LlvmTransformBuilder builder, LlvmValue condition)
        {
            LlvmCallInstruction assumption = LlvmIntrinsic.CreateAssumption(condition, builder.InsertBlock!.Module!, metrics: Metrics);
            builder.InsertBlock.InsertBefore(assumption, builder.InsertBefore);
            assumption.Source = builder.CurrentSource;
            Options.AC?.RegisterAssumption(assumption);
            Count("assumptions.created");
        }
    }
}
