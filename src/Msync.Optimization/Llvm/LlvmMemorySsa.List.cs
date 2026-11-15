// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.h dual intrusive
// AllAccessTag/DefsOnlyTag lists. Managed links replace LLVM ilist allocation.
using System.Collections;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemoryAccessList : IReadOnlyCollection<LlvmMemoryAccess>
{
    private readonly bool definitionsOnly;
    internal LlvmMemoryAccessList(bool definitionsOnly) => this.definitionsOnly = definitionsOnly;
    public LlvmMemoryAccess? First { get; private set; }
    public LlvmMemoryAccess? Last { get; private set; }
    public int Count { get; private set; }
    internal void AddLast(LlvmMemoryAccess access)
    {
        if (definitionsOnly) { access.PreviousDef = Last; if (Last is not null) Last.NextDef = access; }
        else { access.Previous = Last; if (Last is not null) Last.Next = access; }
        if (First is null) First = access;
        Last = access; ++Count;
    }
    internal void AddFirst(LlvmMemoryAccess access)
    {
        if (definitionsOnly) { access.NextDef = First; if (First is not null) First.PreviousDef = access; }
        else { access.Next = First; if (First is not null) First.Previous = access; }
        if (Last is null) Last = access;
        First = access; ++Count;
    }
    internal void Remove(LlvmMemoryAccess access)
    {
        LlvmMemoryAccess? previous = definitionsOnly ? access.PreviousDef : access.Previous;
        LlvmMemoryAccess? next = definitionsOnly ? access.NextDef : access.Next;
        if (previous is null) First = next;
        else if (definitionsOnly) previous.NextDef = next;
        else previous.Next = next;
        if (next is null) Last = previous;
        else if (definitionsOnly) next.PreviousDef = previous;
        else next.Previous = previous;
        if (definitionsOnly) { access.PreviousDef = null; access.NextDef = null; }
        else { access.Previous = null; access.Next = null; }
        --Count;
    }
    public IEnumerator<LlvmMemoryAccess> GetEnumerator()
    {
        for (LlvmMemoryAccess? access = First; access is not null; access = definitionsOnly ? access.NextDef : access.Next)
            yield return access;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
