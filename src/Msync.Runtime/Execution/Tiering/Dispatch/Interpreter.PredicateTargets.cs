using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using Relation = Msync.Optimization.Execution.ExecutionTargets.Relation;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private ExecutionTargets executionTargets = null!;

    private bool TryPredicateCall<TFrame>(ExecutionTargets.Target plan, int target, FunctionValue function,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (function.Captures is { Count: not 0 }) return false;
        cancellationToken.ThrowIfCancellationRequested();
        Relation relation;
        if (plan.Numeric)
        {
            if (!stack.TryPeekNumber(1, out double left) || !stack.TryPeekNumber(0, out double right))
                return PredicateMiss(target);
            if (profile is null && plan.Mask is Less or LessOrRightNaN)
            {
                bool less = left < right || (plan.Mask == LessOrRightNaN && double.IsNaN(right) && !double.IsNaN(left));
                stack.CompleteCall(2, less ? Value.True : Value.False);
                return true;
            }
            relation = NumericRelation(left, right);
        }
        else
        {
            var left = PredicateText(stack.PeekReference(1));
            var right = PredicateText(stack.PeekReference(0));
            if (left is null || right is null) return PredicateMiss(target);
            int order = TextSemantics.CompareOrdinal(left, right);
            relation = order < 0 ? Relation.Less : order == 0 ? Relation.Equal : Relation.Greater;
        }

        if (profile is { } recorder) recorder.Predicate(module, target, plan.Path(relation), taken: true);
        stack.CompleteCall(2, plan.Test(relation) ? Value.True : Value.False);
        return true;
    }

    private const int Less = 1 << (int)Relation.Less;
    private const int LessOrRightNaN = Less | (1 << (int)Relation.RightNaN);

    private static Relation NumericRelation(double left, double right)
    {
        if (left < right) return Relation.Less;
        if (left == right) return Relation.Equal;
        if (left > right) return Relation.Greater;
        if (!double.IsNaN(left)) return Relation.RightNaN;
        return double.IsNaN(right) ? Relation.BothNaN : Relation.LeftNaN;
    }

    private bool PredicateMiss(int target)
    {
        profile?.Predicate(module, target, [], taken: false);
        return false;
    }

    private static string? PredicateText(Value? value) => value switch
    {
        StringValue text => text.Data,
        PathValue path => path.NativeText,
        _ => null
    };
}
