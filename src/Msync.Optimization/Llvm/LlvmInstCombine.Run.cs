// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp InstCombinerImpl::run.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        internal bool Run()
        {
            if (Trace is null) return RunCore();
            Dictionary<string, ulong> before = MetricsSnapshot();
            return Trace.MeasureDetailed("optimizer.llvm-instcombine.worklist", this, static work => work.RunCore(),
                attributes: Attributes(), metrics: _ => MetricsDifference(before));
        }
        private bool RunCore()
        {
            while (!Worklist.IsEmpty)
            {
                while (Worklist.PopDeferred() is { } deferred)
                {
                    if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(deferred, Metrics))
                    { EraseInstFromFunction(deferred); Count("run.dead-instructions"); continue; }
                    Worklist.Push(deferred);
                }
                LlvmInstruction? instruction = Worklist.RemoveOne();
                if (instruction is null) continue;
                if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                { EraseInstFromFunction(instruction); Count("run.dead-instructions"); continue; }
                if (GetOptionalSinkBlockForInst(instruction) is { } destination && TryToSinkInstruction(instruction, destination))
                {
                    MadeIRChange = true;
                    Count("run.instructions-sunk");
                    foreach (LlvmUse use in instruction.OperandUses) Worklist.PushValue(use.Value!);
                }
                Builder.SetInsertPoint(instruction);
                Builder.CurrentSource = instruction.Source;
                AnnotationMetadataSource = instruction;
                Count("run.instructions.visited");
                LlvmInstruction? result = VisitWithTrace(instruction);
                if (result is null) continue;
                Count("run.instructions-combined");
                if (result != instruction)
                {
                    result.Source ??= instruction.Source;
                    if (result.LogicalType < 0) result.LogicalType = instruction.LogicalType;
                    instruction.ReplaceAllUsesWith(result);
                    result.TakeName(instruction);
                    LlvmBasicBlock parent = instruction.Parent!;
                    LlvmInstruction insertion = instruction;
                    if ((result is LlvmPhiInstruction) != (instruction is LlvmPhiInstruction))
                        insertion = instruction is LlvmPhiInstruction ? FirstInsertionPoint(parent) : FirstNonPhi(parent);
                    Debug.Assert(result.Parent is null);
                    parent.InsertBefore(result, insertion);
                    if (result is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } assume)
                        AC.RegisterAssumption(assume);
                    Worklist.PushUsersToWorkList(result);
                    Worklist.Push(result);
                    EraseInstFromFunction(instruction);
                }
                else if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics)) EraseInstFromFunction(instruction);
                else
                {
                    Worklist.PushUsersToWorkList(instruction);
                    Worklist.Push(instruction);
                }
                MadeIRChange = true;
            }
            Worklist.Zap();
            return MadeIRChange;
        }
        private LlvmInstruction? VisitWithTrace(LlvmInstruction instruction)
        {
            if (Trace is null) return Visit(instruction);
            Dictionary<string, ulong> before = MetricsSnapshot();
            return Trace.MeasureDetailed("optimizer.llvm-instcombine.visit", (Work: this, Instruction: instruction),
                static state => state.Work.Visit(state.Instruction), attributes: VisitAttributes(instruction),
                metrics: _ => MetricsDifference(before));
        }
        private LlvmBasicBlock? GetOptionalSinkBlockForInst(LlvmInstruction instruction)
        {
            if (!CLOpts.CodeSinking) return null;
            LlvmBasicBlock block = instruction.Parent!;
            LlvmBasicBlock? userParent = null;
            uint users = 0;
            foreach (LlvmUse use in instruction.Uses)
            {
                Count("sink.users.visited");
                LlvmUser user = use.User;
                if (user.IsDroppable())
                {
                    var droppable = (LlvmCallInstruction)user;
                    if (droppable.CalledFunction?.IntrinsicDescriptor?.IsAssume != true ||
                        !droppable.OperandBundles.Any(bundle => bundle.Tag == "dereferenceable")) continue;
                }
                if (users > CLOpts.MaxSinkUsers) { Count("sink.retained.user-limit"); return null; }
                var consumer = (LlvmInstruction)user;
                LlvmBasicBlock userBlock = consumer is LlvmPhiInstruction phi ? phi.GetIncomingBlock(use.OperandNo) : consumer.Parent!;
                if (userParent is not null && userParent != userBlock) { Count("sink.retained.multiple-user-blocks"); return null; }
                userParent = userBlock;
                if (users == 0)
                {
                    if (userParent == block) { Count("sink.retained.same-block"); return null; }
                    if (!DT.IsReachableFromEntry(userParent)) { Count("sink.retained.unreachable-user"); return null; }
                    if (LlvmBasicBlockUtils.GetUniquePredecessor(userParent) != block && userParent.GetTerminator().SuccessorCount != 0)
                    { Count("sink.retained.control-frequency"); return null; }
                    Debug.Assert(DT.Dominates(block, userParent));
                }
                ++users;
            }
            return userParent;
        }
        private static LlvmInstruction FirstNonPhi(LlvmBasicBlock block)
        {
            LlvmInstruction instruction = block.FirstInstruction!;
            while (instruction is LlvmPhiInstruction) instruction = instruction.Next!;
            return instruction;
        }
        private static LlvmInstruction FirstInsertionPoint(LlvmBasicBlock block)
        {
            LlvmInstruction first = FirstNonPhi(block);
            return first.IsEHPad ? first.Next! : first;
        }
        private Dictionary<string, string> VisitAttributes(LlvmInstruction instruction)
        {
            Dictionary<string, string> attributes = new()
            {
                ["function.name"] = F.Name, ["block.name"] = instruction.Parent!.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString()
            };
            if (instruction.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
