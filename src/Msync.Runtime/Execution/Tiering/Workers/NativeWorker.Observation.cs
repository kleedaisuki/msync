using System.Collections.Immutable;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Op = Msync.Optimization.Execution.NativeWorkerOp;
using Relation = Msync.Optimization.Execution.ExecutionTargets.Relation;

namespace Msync.Runtime;

internal static partial class NativeWorker
{
    // A native comparison may collapse a branching source predicate. Expand
    // relation counters only in the observed version, not in the hot code.
    internal static NativeWorkerPlan Observe(NativeWorkerPlan plan, out bool[]? physicalOperations)
    {
        physicalOperations = null;
        if (plan.Predicates.IsDefaultOrEmpty || plan.LogicalInstructions.IsDefault) return plan;

        var predicates = plan.Predicates.ToDictionary(item => item.Operation);
        int scratch = plan.Slots.Length;
        int[] labels = new int[plan.Instructions.Length];
        List<NativeWorkerInstruction> code = [];
        List<ImmutableArray<NativeWorkerPc>> logical = [];
        List<bool> physical = [];
        Span<int> branches = stackalloc int[5];
        Span<int> done = stackalloc int[6];
        for (int pc = 0; pc < labels.Length; pc++)
        {
            labels[pc] = code.Count;
            var instruction = plan.Instructions[pc];
            Append(instruction, plan.LogicalInstructions[pc], real: true);
            if (!predicates.TryGetValue(pc, out var predicate)) continue;

            branches[0] = Compare(Op.NumberLess, instruction.Left, instruction.Right);
            branches[1] = Compare(Op.NumberEqual, instruction.Left, instruction.Right);
            branches[2] = Compare(Op.NumberLess, instruction.Right, instruction.Left);
            branches[3] = Compare(Op.NumberEqual, instruction.Left, instruction.Left);
            branches[4] = Compare(Op.NumberEqual, instruction.Right, instruction.Right);
            done[0] = Path(predicate, Relation.BothNaN);
            done[1] = Path(predicate, Relation.LeftNaN, branches[4]);
            done[2] = Path(predicate, Relation.RightNaN, branches[3]);
            done[3] = Path(predicate, Relation.Greater, branches[2]);
            done[4] = Path(predicate, Relation.Equal, branches[1]);
            done[5] = Path(predicate, Relation.Less, branches[0]);
            foreach (int branch in done) code[branch] = code[branch] with { Data = code.Count };
        }

        foreach (int label in labels)
        {
            var instruction = code[label];
            if (instruction.Op is Op.Jump or Op.BranchIfFalse or Op.BranchIfTrue)
                code[label] = instruction with { Data = labels[instruction.Data] };
        }

        physicalOperations = [.. physical];
        return plan with
        {
            Instructions = [.. code],
            Slots = plan.Slots.Add(NativeWorkerType.Bool),
            LogicalInstructions = [.. logical],
            Predicates = []
        };

        int Compare(Op operation, int left, int right)
        {
            Append(new(operation, scratch, left, right));
            return Append(new(Op.BranchIfTrue, Left: scratch));
        }

        int Path(NativeWorkerPredicate predicate, Relation relation, int branch = -1)
        {
            if (branch >= 0) code[branch] = code[branch] with { Data = code.Count };
            var path = predicate.Target.Path(relation);
            var visits = ImmutableArray.CreateBuilder<NativeWorkerPc>(path.Length);
            foreach (int pc in path) visits.Add(new(predicate.Function, pc));
            Append(new(Op.Nop), visits.MoveToImmutable());
            return Append(new(Op.Jump));
        }

        int Append(NativeWorkerInstruction instruction, ImmutableArray<NativeWorkerPc> visits = default, bool real = false)
        {
            int index = code.Count;
            code.Add(instruction);
            logical.Add(visits.IsDefault ? [] : visits);
            physical.Add(real);
            return index;
        }
    }
}
