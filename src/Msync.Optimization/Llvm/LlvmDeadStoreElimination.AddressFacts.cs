// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Runtime.InteropServices;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        // One function, layout and query mode per State. Pointer types supply the
        // index width; results retain APInt arithmetic and the original base identity.
        private readonly Dictionary<LlvmValue, AddressFacts> addressFacts = new(ReferenceEqualityComparer.Instance);
        private struct AddressFacts
        {
            internal LlvmValue? Stripped, Underlying, Base;
            internal LlvmAPInt Offset;
        }
        private LlvmValue StripCasts(LlvmValue pointer)
        {
            CountAddress("dse.address.casts.requests");
            ref var facts = ref CollectionsMarshal.GetValueRefOrAddDefault(addressFacts, pointer, out _);
            if (facts.Stripped is { } stripped) { CountAddress("dse.address.casts.hits"); return stripped; }
            CountAddress("dse.address.casts.misses");
            return facts.Stripped = LlvmValueTracking.StripPointerCasts(pointer, Metrics);
        }
        private LlvmValue Underlying(LlvmValue pointer)
        {
            CountAddress("dse.address.underlying.requests");
            ref var facts = ref CollectionsMarshal.GetValueRefOrAddDefault(addressFacts, pointer, out _);
            if (facts.Underlying is { } underlying) { CountAddress("dse.address.underlying.hits"); return underlying; }
            CountAddress("dse.address.underlying.misses");
            return facts.Underlying = LlvmValueTracking.GetUnderlyingObject(pointer, metrics: Metrics);
        }
        private LlvmValue ConstantOffset(LlvmValue pointer, out LlvmAPInt offset)
        {
            CountAddress("dse.address.offsets.requests");
            ref var facts = ref CollectionsMarshal.GetValueRefOrAddDefault(addressFacts, pointer, out _);
            if (facts.Base is { } cached)
            { CountAddress("dse.address.offsets.hits"); offset = facts.Offset; return cached; }
            CountAddress("dse.address.offsets.misses");
            offset = LlvmAPInt.GetZero(layout.GetIndexTypeSizeInBits(pointer.Type));
            var result = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, layout, ref offset, metrics: Metrics);
            facts.Base = result;
            facts.Offset = offset;
            if (Metrics is not null && LlvmGepOperator.TryGet(result, out _)) CountAddress("dse.address.offsets.residual_gep");
            return result;
        }
        private void CountAddress(string key, ulong value = 1)
        {
            if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + value;
        }
        private void InvalidateAddressFacts(bool storeMerge = false)
        {
            ResetStoreRun();
            // Deletion rewrites operands, including dependencies of other cached
            // expressions. Clear the whole invocation-local table before mutation.
            CountAddress(storeMerge ? "dse.address.invalidation.store_merge" : "dse.address.invalidation.delete");
            CountAddress("dse.address.invalidated_entries", (ulong)addressFacts.Count);
            addressFacts.Clear();
        }
    }
}
