// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombiner.h and InstCombineInternal.h common state/mutation protocol.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        internal readonly LlvmFunction F;
        internal readonly LlvmAAResults AA;
        internal readonly LlvmAssumptionCache AC;
        internal readonly LlvmTargetLibraryInfo TLI;
        internal readonly LlvmDominatorTree DT;
        internal readonly LlvmDataLayout DL;
        internal readonly LlvmTransformBuilder Builder;
        internal readonly LlvmInstructionWorklist Worklist;
        internal readonly LlvmInstructionSimplify.BinaryQuery SQ;
        internal readonly LlvmDomConditionCache DC;
        internal readonly bool MinimizeSize;
        internal readonly LlvmInstCombineTuning CLOpts;
        internal readonly IReadOnlyList<LlvmBasicBlock> RPOT;
        internal readonly CompilationTrace? Trace;
        internal readonly Dictionary<string, ulong>? Metrics;
        internal readonly HashSet<(LlvmBasicBlock From, LlvmBasicBlock To)> DeadEdges = [];
        internal readonly Dictionary<LlvmBasicBlock, List<LlvmBasicBlock>> PredOrder = [];
        private readonly HashSet<(LlvmBasicBlock From, LlvmBasicBlock To)> BackEdges = [];
        private readonly LlvmTargetTransformInfo targetInfo;
        private bool computedBackEdges;
        internal bool MadeIRChange;
        private LlvmInstruction? AnnotationMetadataSource;

        internal Work(LlvmInstructionWorklist worklist, LlvmFunction function, LlvmAAResults alias,
            LlvmAssumptionCache assumptions, LlvmTargetLibraryInfo libraryInfo, LlvmTargetTransformInfo targetInfo,
            LlvmDominatorTree dominators, IReadOnlyList<LlvmBasicBlock> reversePostorder,
            LlvmInstCombineTuning tuning, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
        {
            Worklist = worklist; F = function; AA = alias; AC = assumptions; TLI = libraryInfo;
            DT = dominators; DL = function.Parent!.DataLayout; RPOT = reversePostorder;
            MinimizeSize = function.HasMinSize; CLOpts = tuning; Trace = trace; Metrics = metrics;
            this.targetInfo = targetInfo;
            DC = new(Metrics);
            SQ = new(DL, null, Metrics, DT, Assumptions: AC, DC: DC, TLI: TLI);
            Builder = new(function.Context, DL, Metrics, OnInsert, targetFolder: new LlvmTargetFolder(DL, Metrics));
        }
        internal void Count(string key, ulong amount = 1)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount; }
        private void OnInsert(LlvmInstruction instruction)
        {
            Worklist.Add(instruction);
            instruction.Source ??= AnnotationMetadataSource?.Source;
            if (instruction is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.IsAssume: true } assume)
                AC.RegisterAssumption(assume);
            // Source annotation metadata has no graph carrier; Source is set by
            // the builder's insertion point and remains diagnostic context.
        }
        private Dictionary<string, ulong> MetricsSnapshot() => new(Metrics!);
        private Dictionary<string, ulong> MetricsDifference(IReadOnlyDictionary<string, ulong> before)
        {
            Dictionary<string, ulong> difference = [];
            foreach (var (key, value) in Metrics!)
            {
                ulong prior = before.GetValueOrDefault(key);
                if (value != prior) difference[key] = value - prior;
            }
            return difference;
        }
        private LlvmInstructionSimplify.BinaryQuery GetSimplifyQuery() => SQ;
        private LlvmInstructionSimplify.BinaryQuery Query(LlvmInstruction? context) => SQ with { Context = context };
        private LlvmInstruction? ReplaceInstUsesWith(LlvmInstruction instruction, LlvmValue replacement)
        {
            if (instruction.UseEmpty) return null;
            Worklist.PushUsersToWorkList(instruction);
            if (instruction == replacement) replacement = LlvmPoisonValue.Get(instruction.Type);
            if (replacement is LlvmInstruction { UseEmpty: true } result)
            {
                if (result.LogicalType < 0) result.LogicalType = instruction.LogicalType;
                if (!result.HasName && instruction.HasName) result.TakeName(instruction);
            }
            instruction.ReplaceAllUsesWith(replacement);
            Count("rewrites.uses-replaced");
            return instruction;
        }
        private LlvmInstruction ReplaceOperand(LlvmInstruction instruction, int operand, LlvmValue replacement)
        {
            LlvmValue old = instruction.GetOperand(operand)!;
            instruction.SetOperand(operand, replacement);
            Worklist.HandleUseCountDecrement(old);
            Count("rewrites.operands-replaced");
            return instruction;
        }
        private void ReplaceUse(LlvmUse use, LlvmValue replacement)
        {
            LlvmValue old = use.Value!;
            use.Set(replacement);
            Worklist.HandleUseCountDecrement(old);
            Count("rewrites.uses-replaced");
        }
        private LlvmInstruction? EraseInstFromFunction(LlvmInstruction instruction)
        {
            Debug.Assert(instruction.UseEmpty);
            LlvmValue[] operands = instruction.Operands.Select(value => value!).ToArray();
            Worklist.Remove(instruction);
            DC.RemoveValue(instruction);
            instruction.EraseFromParent();
            foreach (LlvmValue operand in operands) Worklist.HandleUseCountDecrement(operand);
            MadeIRChange = true;
            Count("rewrites.instructions-erased");
            return null;
        }
        private T InsertNewInstBefore<T>(T instruction, LlvmInstruction before) where T : LlvmInstruction
        {
            Debug.Assert(instruction.Parent is null);
            before.Parent!.InsertBefore(instruction, before);
            Worklist.Add(instruction);
            Count("rewrites.instructions-inserted");
            return instruction;
        }
        private T InsertNewInstWith<T>(T instruction, LlvmInstruction before) where T : LlvmInstruction
        {
            instruction.Source = before.Source;
            return InsertNewInstBefore(instruction, before);
        }
        private void CreateNonTerminatorUnreachable(LlvmInstruction before)
        {
            var store = new LlvmStoreInstruction(LlvmConstantInt.GetTrue(before.Context),
                LlvmPoisonValue.Get(LlvmPointerType.Get(before.Context)), new LlvmAlign(1));
            InsertNewInstWith(store, before);
        }
        private LlvmSelectInstruction CreateSelectInstWithUnknownProfile(LlvmValue condition, LlvmValue whenTrue,
            LlvmValue whenFalse, string name = "", LlvmInstruction? insertBefore = null)
        {
            var select = new LlvmSelectInstruction(condition, whenTrue, whenFalse);
            select.SetName(name);
            if (insertBefore is not null)
            {
                select.Source = insertBefore.Source;
                insertBefore.Parent!.InsertBefore(select, insertBefore);
            }
            return select;
        }
        private bool IsBackEdge(LlvmBasicBlock from, LlvmBasicBlock to)
        {
            if (!computedBackEdges) ComputeBackEdges();
            return BackEdges.Contains((from, to));
        }
        private void ComputeBackEdges()
        {
            HashSet<LlvmBasicBlock> visited = [];
            foreach (LlvmBasicBlock block in RPOT)
            {
                visited.Add(block);
                foreach (LlvmBasicBlock successor in block.GetTerminator().Successors)
                    if (visited.Contains(successor)) BackEdges.Add((block, successor));
            }
            computedBackEdges = true;
        }
        private bool IsValidAddrSpaceCast(uint from, uint to) => targetInfo.IsValidAddrSpaceCast(from, to);
        private static uint GetComplexity(LlvmValue value)
        {
            if (value is LlvmConstant) return value is LlvmUndefValue ? 0U : 1U;
            if (value is LlvmCastInstruction or LlvmUnaryOperator { Opcode: LlvmOpcode.FNeg }) return 2;
            if (value is not LlvmBinaryOperator binary) return 3;
            if (binary.Opcode == LlvmOpcode.Sub && binary.Left is LlvmConstantInt { IsZero: true }) return 2;
            if (binary.Opcode == LlvmOpcode.Xor && (binary.Left is LlvmConstantInt { IsMinusOne: true } ||
                binary.Right is LlvmConstantInt { IsMinusOne: true })) return 2;
            return binary.Opcode == LlvmOpcode.FSub && binary.Left is LlvmConstantFP { Value.IsZero: true, Value.IsNegative: true } ? 2U : 3U;
        }
        private static bool IsCanonicalPredicate(LlvmCmpPredicate predicate) => predicate is not
            (LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSle or
             LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.FCmpOne or
             LlvmCmpPredicate.FCmpOle or LlvmCmpPredicate.FCmpOge);
    }
}
