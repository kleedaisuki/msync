// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// sinkCommonCodeFromPredecessors/canSinkInstructions/sinkLastInstruction.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool SinkCommonCodeFromPredecessors(LlvmBasicBlock block)
        {
            var sinker = new CommonCodeSinker(this, block);
            return Trace is null ? sinker.Run() : Trace.MeasureDetailed("optimizer.llvm-simplifycfg.sink-common-code",
                sinker, static state => state.Run(), attributes: Attributes(block.GetTerminator()), metrics: _ => sinker.Metrics!);
        }

        private sealed class CommonCodeSinker(Work owner, LlvmBasicBlock block)
        {
            private CompilationTrace? Trace => owner.Trace;
            internal readonly Dictionary<string, ulong>? Metrics = owner.Trace is null ? null : [];
            private readonly Dictionary<LlvmUse, List<LlvmValue>> phiOperands = new(ReferenceEqualityComparer.Instance);
            private void Count(string name)
            {
                owner.Count(name);
                if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1;
            }
            private bool Reject(string reason) { if (Metrics is not null) Count("sink.reject." + reason); return false; }

            internal bool Run()
            {
                List<LlvmBasicBlock> predecessors = [];
                bool otherPredecessors = false;
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                {
                    Count("sink.predecessor");
                    if (predecessor.GetTerminator() is LlvmUncondBrInstruction) predecessors.Add(predecessor);
                    else otherPredecessors = true;
                }
                if (predecessors.Count < 2) return Reject("too-few-unconditional-predecessors");
                foreach (var phi in SinkPhis(block))
                {
                    var values = new List<LlvmValue>(predecessors.Count);
                    foreach (var predecessor in predecessors) values.Add(phi.GetIncomingValue(phi.GetBasicBlockIndex(predecessor)));
                    phiOperands.Add(phi.GetOperandUse(phi.GetBasicBlockIndex(predecessors[0])), values);
                }
                List<LlvmInstruction[]> candidates = [];
                var cursor = new SinkLockstep(predecessors);
                HashSet<LlvmValue> toSink = new(ReferenceEqualityComparer.Instance);
                while (cursor.Valid && CanSink(cursor.Current))
                {
                    var current = cursor.Current.ToArray(); candidates.Add(current);
                    foreach (var instruction in current) toSink.Add(instruction);
                    Count("sink.candidate_group"); cursor.Previous();
                }
                int count = candidates.Count;
                if (count == 0) return Reject("no-sinkable-instruction");
                bool deoptOrUnreachable = SinkFollowedByUnreachable(block);
                if (!deoptOrUnreachable)
                {
                    HashSet<LlvmValue> profitable = new(ReferenceEqualityComparer.Instance);
                    int index = 0;
                    while (index < count && Profitable(candidates[index], toSink))
                    {
                        foreach (var instruction in candidates[index]) profitable.Add(instruction);
                        ++index;
                    }
                    if (index == 0) return Reject("phi-cost");
                    if (index < count)
                    {
                        count = index; toSink = new(profitable, ReferenceEqualityComparer.Instance);
                        --index;
                        while (index >= 0)
                        {
                            foreach (var instruction in candidates[index]) profitable.Remove(instruction);
                            if (!Profitable(candidates[index], toSink))
                            { count = index; toSink = new(profitable, ReferenceEqualityComparer.Instance); }
                            --index;
                        }
                    }
                    if (count == 0) return Reject("backward-phi-cost");
                }
                if (otherPredecessors)
                {
                    if (!deoptOrUnreachable && !candidates.Take(count).Any(group =>
                        !LlvmValueTracking.IsSafeToSpeculativelyExecute(group[0], metrics: Metrics)))
                        return Reject("partial-predecessors-profitable-only-for-nonspeculatable");
                    if (LlvmBasicBlockUtils.SplitBlockPredecessors(block, predecessors, ".sink.split", owner.DominatorUpdater) is null)
                        return Reject("split-predecessors");
                    Count("sink.split_predecessors");
                }
                for (int index = 0; index < count; ++index) SinkLast(predecessors);
                return true;
            }

            private bool CanSink(IReadOnlyList<LlvmInstruction> instructions)
            {
                int? uses = null;
                foreach (var instruction in instructions)
                {
                    Count("sink.instruction_checked");
                    // InlineAsm and MMRAMetadata have no carriers in this IR.
                    if (instruction is LlvmPhiInstruction or LlvmAllocaInstruction || instruction.IsEHPad || instruction.Type.IsTokenTy()) return false;
                    if (instruction.Parent!.GetTerminator().SuccessorCount == 1 &&
                        instruction.Parent.GetTerminator().GetSuccessor(0) == instruction.Parent) return false;
                    if (instruction is LlvmCallBase call && (call.CannotMerge || call.IsConvergent)) return false;
                    int count = instruction.GetNumUses();
                    if (uses.HasValue && uses.Value != count) return false;
                    uses = count;
                }
                var first = instructions[0];
                if (instructions.Any(instruction => !instruction.IsSameOperationAs(first,
                    LlvmOperationEquivalenceFlags.CompareUsingIntersectedAttrs))) return false;
                foreach (var use in first.Uses)
                {
                    Count("sink.use_checked");
                    if (!phiOperands.TryGetValue(use, out var values) || values.Count != instructions.Count) return false;
                    for (int index = 0; index < instructions.Count; ++index)
                        if (values[index] != instructions[index]) return false;
                }
                if (first is LlvmCallBase)
                {
                    bool indirect = instructions.Cast<LlvmCallBase>().Any(call => call.CalledFunction is null);
                    if (indirect && instructions.Cast<LlvmCallBase>().Any(call => call.CalledFunction is not null)) return false;
                    if (!indirect && instructions.Cast<LlvmCallBase>().Any(call => call.CalledOperand != ((LlvmCallBase)first).CalledOperand)) return false;
                }
                for (int operand = 0; operand < first.OperandCount; ++operand)
                {
                    var value = first.GetOperand(operand)!;
                    if (instructions.All(instruction => instruction.GetOperand(operand) == value)) continue;
                    if (value is LlvmConstant &&
                        ((first.Opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem) && operand == 1 ||
                         first is LlvmCallBase intrinsic && intrinsic.CalledFunction?.IntrinsicDescriptor is not null)) return false;
                    if (instructions.Any(instruction => !LlvmLocal.CanReplaceOperandWithVariable(instruction, operand))) return false;
                    var values = new List<LlvmValue>(instructions.Count);
                    foreach (var instruction in instructions) values.Add(instruction.GetOperand(operand)!);
                    phiOperands[first.GetOperandUse(operand)] = values;
                }
                return true;
            }

            private bool Profitable(LlvmInstruction[] instructions, HashSet<LlvmValue> toSink)
            {
                uint phis = 0;
                var first = instructions[0];
                foreach (var use in first.OperandUses)
                {
                    if (!phiOperands.TryGetValue(use, out var values) || values.All(toSink.Contains)) continue;
                    ++phis;
                    if ((first is LlvmLoadInstruction && use.OperandNo == 0 || first is LlvmStoreInstruction && use.OperandNo == 1) &&
                        values.Any(value => value is LlvmGetElementPtrInstruction)) return false;
                }
                Count("sink.profitability_checked");
                if (Metrics is not null) Metrics["sink.required_phis"] = Metrics.GetValueOrDefault("sink.required_phis") + phis;
                return phis <= 1;
            }

            private void SinkLast(IReadOnlyList<LlvmBasicBlock> predecessors)
            {
                if (Trace is null) { SinkLastCore(predecessors); return; }
                var attributes = Attributes(predecessors[0].GetTerminator().Previous!);
                attributes["sink.predecessor_count"] = predecessors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Trace.Run("optimizer.llvm-simplifycfg.sink-instruction-group", (Sinker: this, Predecessors: predecessors),
                    static state => state.Sinker.SinkLastCore(state.Predecessors), attributes);
            }

            private void SinkLastCore(IReadOnlyList<LlvmBasicBlock> predecessors)
            {
                var destination = predecessors[0].GetTerminator().GetSuccessor(0);
                var instructions = predecessors.Select(predecessor => predecessor.GetTerminator().Previous!).ToArray();
                var common = instructions[0];
                List<LlvmValue> operands = new(common.OperandCount);
                for (int operand = 0; operand < common.OperandCount; ++operand)
                {
                    var value = common.GetOperand(operand)!;
                    if (instructions.All(instruction => instruction.GetOperand(operand) == value))
                    { operands.Add(value); continue; }
                    var phi = new LlvmPhiInstruction(value.Type, predecessors.Count)
                    {
                        LogicalType = OperandAbi(common.GetOperandUse(operand)),
                        Source = common.Source
                    };
                    phi.SetName(value.Name + ".sink");
                    destination.InsertBefore(phi, destination.FirstInstruction);
                    foreach (var instruction in instructions)
                    {
                        LlvmUse use = instruction.GetOperandUse(operand);
                        phi.AddIncoming(use.Value!, instruction.Parent!);
                        phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = OperandAbi(use);
                    }
                    if (phi.LogicalType >= 0) Count("sink.phi_operand_abi_copied");
                    operands.Add(phi); Count("sink.phi_created");
                }
                for (int operand = 0; operand < operands.Count; ++operand) common.SetOperand(operand, operands[operand]);
                common.RemoveFromParent(); destination.InsertBefore(common, SinkFirstNonPhi(destination));
                foreach (var instruction in instructions)
                    if (instruction != common)
                    {
                        LlvmLocal.CombineMetadataForCse(common, instruction, doesMove: true, Metrics);
                        common.AndIRFlags(instruction); Count("sink.flags_intersected");
                        if (common is LlvmCallBase call && instruction is LlvmCallBase otherCall)
                        {
                            bool success = call.TryIntersectAttributes(otherCall);
                            Debug.Assert(success);
                        }
                    }
                foreach (var phi in common.Users.Cast<LlvmPhiInstruction>().Distinct().ToArray())
                { phi.ReplaceAllUsesWith(common); phi.EraseFromParent(); Count("sink.phi_removed"); }
                foreach (var instruction in instructions)
                    if (instruction != common)
                    { instruction.ReplaceAllUsesWith(common); instruction.EraseFromParent(); Count("sink.instruction_erased"); }
                Count("sink.instruction_group_sunk");
            }

            // New SSA values retain the originating operand's project codegen view.
            private static int OperandAbi(LlvmUse use) => use.LogicalType >= 0 ? use.LogicalType
                : use.Value is LlvmConstant ? -1 : use.Value!.LogicalType;
        }

    }

    private static IEnumerable<LlvmPhiInstruction> SinkPhis(LlvmBasicBlock block)
    {
        for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next) yield return phi;
    }
    private static LlvmInstruction? SinkFirstNonPhi(LlvmBasicBlock block)
    {
        var instruction = block.FirstInstruction;
        while (instruction is LlvmPhiInstruction) instruction = instruction.Next;
        return instruction;
    }
    private sealed class SinkLockstep(IReadOnlyList<LlvmBasicBlock> blocks)
    {
        internal LlvmInstruction[] Current { get; private set; } = blocks.Select(block => block.GetTerminator().Previous!).ToArray();
        internal bool Valid => Current.All(instruction => instruction is not null);
        internal void Previous() => Current = Current.Select(instruction => instruction.Previous!).ToArray();
    }
    private static bool SinkFollowedByUnreachable(LlvmBasicBlock block)
    {
        HashSet<LlvmBasicBlock> visited = new(ReferenceEqualityComparer.Instance);
        // BasicBlockUtils.cpp's MaxDeoptOrUnreachableSuccessorCheckDepth default.
        // Terminating deoptimize intrinsic descriptors are not represented.
        for (uint depth = 0; depth < 8 && visited.Add(block); ++depth)
        {
            var terminator = block.GetTerminator();
            if (terminator.Opcode == LlvmOpcode.Unreachable) return true;
            if (terminator.SuccessorCount == 0) return false;
            var next = terminator.GetSuccessor(0);
            for (int index = 1; index < terminator.SuccessorCount; ++index)
                if (terminator.GetSuccessor(index) != next) return false;
            block = next;
        }
        return false;
    }
}
