using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private readonly record struct LayoutOrigin(int Layout, LlvmArgument? Parameter = null);
    private readonly record struct LayoutVariant(LlvmFunction Function, int Argument, int Layout);

    private sealed partial class Lowering
    {
        private readonly Dictionary<LlvmFunction, LayoutOrigin?> layoutReturns = [];
        private readonly Dictionary<LlvmFunction, int[]> layoutArguments = [];
        private readonly Dictionary<LayoutVariant, int> layoutVariantIds = [];
        private readonly List<LayoutVariant> layoutVariants = [];
        private int layoutVariantBase;

        private void PrepareLayoutArguments()
        {
            if (Session is not null || !Input.ValueOperations.Values.Any(binding => binding.Operation == LlvmRuntimeValueOperation.HasLayout))
                return;
            Dictionary<LlvmArgument, List<LlvmArgument>> callers = [];
            HashSet<LlvmArgument> needed = [];
            Queue<LlvmArgument> pending = new();
            foreach (var (function, source) in Input.SourceFunctions)
            foreach (var block in function.BasicBlocks)
            foreach (var instruction in block.Instructions)
            {
                if (instruction is not LlvmCallInstruction call) continue;
                if (IsLayoutTest(call))
                {
                    if (LayoutOf(call.GetArgument(0))?.Parameter is { } receiver && receiver.Parent == function
                        && receiver.ArgNo >= source.CaptureCount && needed.Add(receiver)) pending.Enqueue(receiver);
                    continue;
                }
                if (call.CalledFunction is not { } target || !Input.SourceFunctions.TryGetValue(target, out var info)) continue;
                for (int index = info.CaptureCount; index < call.ArgumentCount; index++)
                {
                    if (LayoutOf(call.GetArgument(index))?.Parameter is not { } forwarded || forwarded.Parent != function
                        || forwarded.ArgNo < source.CaptureCount) continue;
                    LlvmArgument formal = target.Arguments[index];
                    if (!callers.TryGetValue(formal, out var sites)) callers.Add(formal, sites = []);
                    sites.Add(forwarded);
                }
            }
            int roots = needed.Count;
            while (pending.TryDequeue(out var formal))
                if (callers.TryGetValue(formal, out var sites))
                    foreach (LlvmArgument caller in sites)
                        if (needed.Add(caller)) pending.Enqueue(caller);
            foreach (var group in needed.GroupBy(argument => argument.Parent!))
                layoutArguments.Add(group.Key, group.Select(argument => (int)argument.ArgNo).Order().ToArray());
            trace?.Metric("calls.layout_guard_formals", roots);
            trace?.Metric("calls.layout_forwarding_formals", needed.Count - roots);
        }

        internal int SpecializeLayoutCall(LlvmCallInstruction call, LlvmFunction target,
            LayoutVariant? caller)
        {
            if (!layoutArguments.TryGetValue(target, out int[]? arguments)) return Functions[target];
            foreach (int argument in arguments)
            {
                if (ResolveLayout(call.GetArgument(argument), caller) is not int layout) continue;
                LayoutVariant key = new(target, argument, layout);
                if (layoutVariantIds.TryGetValue(key, out int index)) return index;
                index = layoutVariantBase + layoutVariants.Count;
                layoutVariantIds.Add(key, index);
                layoutVariants.Add(key);
                return index;
            }
            return Functions[target];
        }

        internal bool IsLayoutTest(LlvmCallInstruction call) => call.CalledFunction is { } target
            && Input.ValueOperations.TryGetValue(target, out var binding)
            && binding.Operation == LlvmRuntimeValueOperation.HasLayout;

        internal int? ResolveLayout(LlvmValue value, LayoutVariant? variant)
        {
            if (LayoutOf(value) is not { } result) return null;
            if (result.Parameter is not { } parameter) return result.Layout;
            return variant is { } known && parameter.Parent == known.Function && parameter.ArgNo == known.Argument
                ? known.Layout : null;
        }

        private LayoutOrigin? LayoutOf(LlvmValue value) => value switch
        {
            LlvmHeapAllocInst { LogicalLayout: >= 0 } alloc => new(alloc.LogicalLayout),
            LlvmArgument argument when argument.LogicalType >= 0 && Types[argument.LogicalType] is
                { Kind: var kind, Pointee: < 0 } && (kind & TypeKind.Scope) != 0 => new(-1, argument),
            LlvmCallInstruction call when call.CalledFunction is { } target => CallLayout(call, target),
            _ => null
        };

        private LayoutOrigin? CallLayout(LlvmCallInstruction call, LlvmFunction target)
        {
            if (!Input.SourceFunctions.ContainsKey(target) || target.IsDeclaration) return null;
            if (!layoutReturns.TryGetValue(target, out var result))
            {
                // A recursive return is unknown until an independent producer is found.
                layoutReturns.Add(target, null);
                foreach (var block in target.BasicBlocks)
                {
                    if (block.GetTerminator() is not LlvmReturnInstruction { ReturnValue: { } returned }) continue;
                    LayoutOrigin? origin = LayoutOf(returned);
                    if (origin is null || result is not null && result != origin) return null;
                    result = origin;
                }
                layoutReturns[target] = result;
            }
            if (result?.Parameter is { } parameter) return LayoutOf(call.GetArgument((int)parameter.ArgNo));
            return result;
        }

        private NativeFunctionCode[] EmitLayoutVariants(NativeFunctionCode[] functions)
        {
            if (layoutVariants.Count == 0) return functions;
            List<NativeFunctionCode> bodies = new(functions);
            for (int index = 0; index < layoutVariants.Count; index++)
            {
                LayoutVariant variant = layoutVariants[index];
                var body = new Body(this, variant.Function, bodies.Count, trace?.Detail, variant);
                bodies.Add(trace is null ? body.Run() : trace.MeasureDetailed("compiler.llvm.target.layout_variant", body,
                    static state => state.Run(), attributes: FunctionAttributes(variant.Function.Name, bodies.Count,
                        variant.Function.BasicBlocks.FirstOrDefault()?.FirstInstruction?.Source)));
            }
            trace?.Metric("calls.layout_variants", layoutVariants.Count);
            return bodies.ToArray();
        }
    }

    private sealed partial class Body
    {
        private HashSet<LlvmBasicBlock>? layoutReachable;
        private List<LlvmBasicBlock>? layoutBlockOrder;
        private Dictionary<LlvmPhiInstruction, LlvmValue>? layoutPointerAliases;
        private LlvmBasicBlock? fallthrough;

        private void FindLayoutReachability()
        {
            if (layoutVariant is null) return;
            layoutReachable = [];
            layoutBlockOrder = [];
            Stack<LlvmBasicBlock> pending = new();
            pending.Push(function.BasicBlocks.First());
            while (pending.TryPop(out var block))
            {
                if (!layoutReachable.Add(block)) continue;
                layoutBlockOrder.Add(block);
                var end = block.GetTerminator();
                if (end is LlvmCondBrInstruction branch && TryLayoutTest(branch.Condition, out bool matches))
                    pending.Push(branch.GetSuccessor(matches ? 0 : 1));
                else
                    for (int index = end.SuccessorCount - 1; index >= 0; index--)
                        pending.Push(end.GetSuccessor(index));
            }
            trace?.Metric("addresses.layout_unreachable_blocks", function.BasicBlocks.Count - layoutReachable.Count);
        }

        private bool LayoutReachable(LlvmBasicBlock block) => layoutReachable?.Contains(block) ?? true;

        private bool FoldLayoutPointerPhi(LlvmPhiInstruction phi)
        {
            if (layoutReachable is null || !phi.Type.IsPointerTy()) return false;
            LlvmGetElementPtrInstruction? only = null;
            for (int i = 0; i < phi.IncomingCount; i++)
            {
                LlvmBasicBlock predecessor = phi.GetIncomingBlock(i);
                if (!layoutReachable.Contains(predecessor)) continue;
                if (predecessor.GetTerminator() is LlvmCondBrInstruction branch
                    && TryLayoutTest(branch.Condition, out bool matches) && branch.GetSuccessor(matches ? 0 : 1) != phi.Parent)
                    continue;
                if (only is not null || phi.GetIncomingValue(i) is not LlvmGetElementPtrInstruction address
                    || SelectedValueType(address) != SelectedValueType(phi)) return false;
                only = address;
            }
            if (only is null) return false;
            (layoutPointerAliases ??= []).Add(phi, only);
            trace?.Metric("addresses.layout_pointer_phis_folded", 1);
            return true;
        }

        private bool TryLayoutTest(LlvmValue check, out bool matches)
        {
            matches = false;
            if (check is not LlvmCallInstruction call || !owner.IsLayoutTest(call)
                || owner.ResolveLayout(call.GetArgument(0), layoutVariant) is not int layout) return false;
            matches = owner.Input.ValueOperations[call.CalledFunction!].Mode == layout;
            return true;
        }
    }
}
