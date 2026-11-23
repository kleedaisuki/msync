using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<HpirFailure, int> failureImports = [];
        private readonly Dictionary<int, int> failureDiagnostics = [];

        internal LlvmCallableRuntimeBinding? Callable(LlvmFunction target) =>
            graph.CallableCalls.GetValueOrDefault(target);

        internal int Captures(LlvmFunction target) =>
            graph.SourceFunctions.TryGetValue(target, out var info) ? info.CaptureCount : 0;

        internal LlvmFunction? ReadyUnaryCallback(LlvmValue value) =>
            UnaryCallback(value) is { } target && !FutureResult(target) && !TextResult(target) ? target : null;

        internal LlvmFunction? UnaryCallback(LlvmValue value) =>
            FixedUnaryCallback(value) is { } target && !FutureArguments(target) ? target : null;

        internal LlvmFunction? FixedUnaryCallback(LlvmValue value)
        {
            if (value is not LlvmCallInstruction { CalledFunction: { } adapt }
                || Callable(adapt) is not { Operation: LlvmCallableRuntimeOperation.Adapt, Targets.Count: 1 } binding
                || binding.SessionSlot >= 0 && !IsPersistentSession)
                return null;
            var target = binding.Targets.Values.First();
            return entries.ContainsKey(target) && target.Arguments.Count == Captures(target) + 1 ? target : null;
        }

        internal int FailureImport(HpirFailure failure)
        {
            if (failureImports.TryGetValue(failure, out int id)) return id;
            id = Imports.Count;
            Imports.Add(new(HpirService.Fail, (int)failure, HpirWidth.Void,
                failure == HpirFailure.RangeIndex ? [HpirWidth.U64] : []));
            failureImports.Add(failure, id);
            return id;
        }

        internal int FailureImport(string message)
        {
            int literal = TextLiteral(message);
            if (failureDiagnostics.TryGetValue(literal, out int id)) return id;
            id = Imports.Count;
            Imports.Add(new(HpirService.FailDiagnostic, literal, HpirWidth.Void, []));
            failureDiagnostics.Add(literal, id);
            return id;
        }
    }

    private sealed partial class Body
    {
        private int dynamicSites, dynamicArms, fixedCallableSites;

        private bool TryCallable(LlvmCallInstruction call)
        {
            if (call.CalledFunction is not { } target || geometry.Callable(target) is not { } binding) return false;
            if (binding.Targets.Count == 0)
                throw Error("lowering an open callable domain", call);
            LlvmValue descriptor = call.GetArgument(0);
            if (binding.SessionSlot < 0 && binding.Targets.Count == 1)
            {
                EmitCallable(call, descriptor, binding.Operation, binding.Targets.First().Value);
                ++fixedCallableSites;
                return true;
            }
            int identity = LeafLocation(descriptor, 0);
            int probe = Reserve(8, 8), condition = Reserve(1, 1);
            int[] ends = new int[binding.Targets.Count];
            int arm = 0;
            foreach (var pair in binding.Targets)
            {
                Add(new(HpirOp.ConstU64, probe, Immediate: geometry.CodeIdentity(pair.Key)), call.Source);
                Add(new(HpirOp.EqU64, condition, identity, probe), call.Source);
                int branch = code.Count;
                Add(new(HpirOp.Branch, A: condition, B: branch + 1), call.Source);
                EmitCallable(call, descriptor, binding.Operation, pair.Value);
                ends[arm++] = code.Count;
                Add(new(HpirOp.Jump), call.Source);
                code[branch] = code[branch] with { D = code.Count };
            }
            EmitFailure(call, HpirFailure.UnknownCallable);
            foreach (int edge in ends) code[edge] = code[edge] with { A = code.Count };
            ++dynamicSites;
            dynamicArms += arm;
            return true;
        }

        private void EmitCallable(LlvmCallInstruction call, LlvmValue descriptor, LlvmCallableRuntimeOperation operation,
            LlvmFunction target)
        {
            if (operation == LlvmCallableRuntimeOperation.Invoke) InvokeCallable(call, descriptor, target);
            else AdaptCallable(call, descriptor, target);
        }

        private void InvokeCallable(LlvmCallInstruction call, LlvmValue descriptor, LlvmFunction target)
        {
            if (geometry.FutureResult(target) || geometry.FutureArguments(target))
                throw Error("finite dispatch to a future-capable guest signature", call);
            int captures = geometry.Captures(target);
            int[] captured = captures == 0 ? [] : ReadCaptureAddresses(call, LeafLocation(descriptor, 8), captures);
            Call(call, ids[target], call.Arguments.Skip(1), captured, target);
        }

        private int[] ReadPlanCaptures(LlvmInstruction site, int plan, int offset, LlvmFunction target)
        {
            int count = geometry.Captures(target);
            if (count == 0) return [];
            int environment = Reserve(8, 8);
            Copy(HpirWidth.U64, environment, Indirect(plan, offset, site), site.Source);
            return ReadCaptureAddresses(site, environment, count);
        }

        private int[] ReadCaptureAddresses(LlvmInstruction site, int environment, int count)
        {
            var captures = new int[count];
            for (int index = 0; index < count; ++index)
            {
                captures[index] = Reserve(8, 8);
                Copy(HpirWidth.U64, captures[index], Indirect(environment, checked(index * 8), site), site.Source);
            }
            return captures;
        }

        private void AdaptCallable(LlvmCallInstruction call, LlvmValue descriptor, LlvmFunction target)
        {
            var leaves = geometry.Leaves(call.Type);
            Add(new(HpirOp.ConstU64, LeafLocation(call, 0), Immediate: geometry.CodeIdentity(target)), call.Source);
            for (int index = 1; index < leaves.Length; ++index)
            {
                var leaf = leaves[index];
                Copy(Width(leaf.Type), LeafLocation(call, leaf.Offset), LeafLocation(descriptor, leaf.Offset), call.Source);
            }
        }
    }
}
