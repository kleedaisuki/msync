// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp processNode,
// AvailableInvariants and isOperatingOnInvariantMemAt.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private sealed partial class Work
    {
        private readonly ScopedTable<LlvmMemoryLocation, uint> invariants = new(EqualityComparer<LlvmMemoryLocation>.Default);

        private bool ProcessSpecialIntrinsic(LlvmInstruction instruction)
        {
            if (instruction is not LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } call) return false;
            if (descriptor.IsAssume)
            {
                if (call.GetArgument(0) is LlvmInstruction condition && CanHandleSimple(condition))
                {
                    values.Insert(condition, LlvmConstantInt.GetTrue(function.Context));
                    Count("assume.condition_inserted");
                }
                else Count("assume.condition_not_simple");
                return true;
            }
            if (descriptor.Kind != LlvmIntrinsicKind.InvariantStart) return false;
            if (!call.UseEmpty) { Count("invariant.start.finite_scope"); return true; }
            RecordInvariant(LlvmMemoryLocation.GetForArgument(call, 1), "invariant.start.scope_inserted");
            return true;
        }

        private void RecordInvariant(LlvmMemoryLocation location, string insertedMetric)
        {
            if (invariants.Contains(location)) { Count("invariant.scope.already_available"); return; }
            invariants.Insert(location, generation);
            Count(insertedMetric);
        }

        private bool IsOperatingOnInvariantMemoryAt(LlvmInstruction instruction, uint generationAt)
        {
            Count("invariant.queries");
            if (instruction is LlvmLoadInstruction { HasInvariantLoadMetadata: true })
            { Count("invariant.load_metadata"); return true; }
            if (LlvmMemoryLocation.GetOrNone(instruction, Layout) is not { } location)
            { Count("invariant.no_memory_location"); return false; }
            if (!invariants.Contains(location)) { Count("invariant.scope.unavailable"); return false; }
            bool invariant = invariants.Lookup(location) <= generationAt;
            Count(invariant ? "invariant.scope.available_at_generation" : "invariant.scope.starts_later");
            return invariant;
        }
    }
}
