// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp: managed adapters for WeakVH/TrackingVH and PHI operands.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public abstract partial class LlvmMemoryAccess
{
    internal bool IsDeleted { get; set; }
    internal LlvmMemoryAccess? ReplacedBy { get; set; }
    internal static LlvmMemoryAccess? Track(LlvmMemoryAccess? access)
    {
        var original = access;
        while (access?.ReplacedBy is { } replacement) access = replacement;
        while (original?.ReplacedBy is { } next && !ReferenceEquals(next, access))
        { original.ReplacedBy = access; original = next; }
        return access is { IsDeleted: true } ? null : access;
    }
    internal static LlvmMemoryAccess? Weak(LlvmMemoryAccess? access) => access is { IsDeleted: false } ? access : null;
    internal void ReplaceTrackedUsesWith(LlvmMemoryAccess replacement)
    { ReplacedBy = replacement; ReplaceAllUsesWith(replacement); }
}

public sealed partial class LlvmMemoryPhi
{
    public void UnorderedDeleteIncomingBlock(LlvmBasicBlock block) => UnorderedDeleteIncomingIf((_, incoming) => ReferenceEquals(incoming, block));
    public void UnorderedDeleteIncomingIf(Func<LlvmMemoryAccess, LlvmBasicBlock, bool> predicate)
    {
        for (int index = 0; index < IncomingCount;)
        {
            if (predicate(GetIncomingValue(index), GetIncomingBlock(index))) UnorderedDeleteIncoming(index);
            else ++index;
        }
        System.Diagnostics.Debug.Assert(IncomingCount >= 1);
    }
}
