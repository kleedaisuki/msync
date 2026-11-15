// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SCCPSolver.cpp handleCallResult's ConstantRange-supported intrinsic branch.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSccpSolver
{
    private void VisitCall(LlvmCallBase call)
    {
        if (call.CalledFunction?.IntrinsicDescriptor is { } descriptor && LlvmConstantRange.IsIntrinsicSupported(descriptor.Kind))
        {
            Count("intrinsic.registered_calls");
            if (metrics is not null) Count("intrinsic.kind." + descriptor.Kind.Name);
            var ranges = new List<LlvmConstantRange>(call.ArgumentCount);
            foreach (var argument in call.Arguments)
            {
                Count("intrinsic.arguments_visited");
                var state = State(argument);
                if (state.IsUnknownOrUndef) { Count("intrinsic.wait_unknown_or_undef"); return; }
                ranges.Add(state.AsConstantRange(argument.Type, false));
            }
            var result = LlvmConstantRange.Intrinsic(descriptor.Kind, ranges, metrics);
            Merge(State(call), call, LlvmValueLatticeElement.GetRange(result));
            Count("intrinsic.range_transfers");
            if (result.IsSingleElement) Count("intrinsic.singleton_results");
            return;
        }
        if (call.Type.IsIntegerTy() && call.CalledFunction is { IsDeclaration: true, IntrinsicDescriptor: { } intrinsic })
        {
            var operands = new List<LlvmConstant>(call.ArgumentCount);
            foreach (var argument in call.Arguments)
            {
                var state = State(argument);
                if (state.IsUnknownOrUndef) { Count("intrinsic.fold_wait_unknown_or_undef"); return; }
                if (IsOverdefined(state)) { MarkOverdefined(call); return; }
                operands.Add(Constant(state, argument.Type)!);
            }
            if (IsOverdefined(State(call))) { MarkOverdefined(call); return; }
            if (LlvmConstantFolding.ConstantFoldIntrinsic(intrinsic.Kind, operands, call.Type, metrics) is { } folded)
            { MarkConstant(call, folded); Count("intrinsic.constant_call_folds"); return; }
        }
        // Current ordinary declarations/bodies are not TLI-foldable or tracked interprocedurally.
        if (!call.Type.IsVoidTy()) MarkOverdefined(call);
        Count("visit.calls_untracked");
    }
}
