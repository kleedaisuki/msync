using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private string? PrivateSessionResultBoundary(NativeCodeType result)
        {
            if (result.Kind != TypeKind.Scope || result.Layouts is not { Count: 1 } choices)
                return "returned Scope geometry is not singular";
            int logical = choices[0];
            if (graph.SessionGeneration!.HpirLayouts?.LogicalIds.ContainsKey(logical) == true
                || graph.StaticScopes.Any(scope => scope.Layout == logical))
                return "the returned Scope shares established storage geometry";
            if (graph.FutureFields.Any(field => field.Layout != logical
                || graph.Types[graph.Layouts[field.Layout].Fields[field.Ordinal].Type].Kind is not (TypeKind.Number or TypeKind.Bool)))
                return "a pending field is outside the returned scalar Scope geometry";
            return entries.Keys.SelectMany(static function => function.BasicBlocks)
                .SelectMany(static block => block.Instructions)
                .OfType<LlvmStoreInstruction>().Any(store => RetainsScope(store, logical))
                ? "the returned Scope can enter retaining storage" : null;
        }

        private bool RetainsScope(LlvmStoreInstruction store, int layout)
        {
            if (!CanHoldScope(store.ValueOperand.Type)) return false;
            int logical = store.GetOperandUse(0).LogicalType;
            if (logical < 0) logical = store.ValueOperand.LogicalType;
            LlvmValue address = store.PointerOperand;
            while (address is LlvmGetElementPtrInstruction gep) address = gep.PointerOperand;
            return address is not LlvmAllocaInstruction && ScopeOrigin(store.ValueOperand, logical, layout);
        }

        private bool CanHoldScope(LlvmType type) => IsGuestPointer(type) || (Union(type) & TypeKind.Scope) != 0;

        private bool ScopeOrigin(LlvmValue value, int logical, int layout)
        {
            if (ScopeLayoutMatch(logical, layout) is { } admitted) return admitted;
            HashSet<LlvmValue> seen = [];
            Stack<LlvmValue> pending = new();
            pending.Push(value);
            while (pending.TryPop(out var candidate))
            {
                if (!seen.Add(candidate) || !CanHoldScope(candidate.Type)) continue;
                if (ScopeLayoutMatch(candidate.LogicalType, layout) is { } known)
                {
                    if (known) return true;
                    continue;
                }
                if (candidate is LlvmHeapAllocInst allocation)
                {
                    if (allocation.LogicalLayout == layout) return true;
                    continue;
                }
                if (candidate is LlvmLoadInstruction load)
                {
                    LlvmValue source = load.PointerOperand;
                    while (source is LlvmGetElementPtrInstruction gep) source = gep.PointerOperand;
                    // Any transfer into heap/global storage is checked at
                    // its actual write; an old cell cannot contain this layout.
                    if (source is LlvmAllocaInstruction local)
                        foreach (var store in LocalScopeWrites(local)) pending.Push(store.ValueOperand);
                    continue;
                }
                if (candidate is LlvmCallInstruction { ArgumentCount: 1 } convert
                    && RepresentationOperation(convert) is LlvmRuntimeConversionOperation.Represent or LlvmRuntimeConversionOperation.Narrow)
                {
                    pending.Push(convert.GetArgument(0));
                    continue;
                }
                if (RangeInputs(candidate) is { } inputs)
                    foreach (var input in inputs) pending.Push(input);
            }
            return false;
        }

        private bool? ScopeLayoutMatch(int logical, int layout)
        {
            if (logical < 0) return null;
            var type = graph.Types[logical];
            if ((type.Kind & TypeKind.Scope) == 0) return false;
            return type.Layouts is { Count: > 0 } layouts ? layouts.Contains(layout) : null;
        }

        private static IEnumerable<LlvmStoreInstruction> LocalScopeWrites(LlvmValue slot)
        {
            foreach (var user in slot.Users)
                if (user is LlvmStoreInstruction store && store.PointerOperand == slot) yield return store;
                else if (user is LlvmGetElementPtrInstruction gep && gep.PointerOperand == slot)
                    foreach (var nested in LocalScopeWrites(gep)) yield return nested;
        }

        private Dictionary<int, int> SessionLayoutsWithoutPresentedScope()
        {
            var persistent = new Dictionary<int, int>(layoutIds);
            foreach (var logical in futureFieldsByLayout.Keys) persistent.Remove(logical);
            return persistent;
        }
    }
}
