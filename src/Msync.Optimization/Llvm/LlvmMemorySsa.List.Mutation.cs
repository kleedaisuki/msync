// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSA.cpp intrusive access-list insertion adapter.
namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemoryAccessList
{
    internal void InsertBefore(LlvmMemoryAccess access, LlvmMemoryAccess? where)
    {
        if (where is null) { AddLast(access); return; }
        var previous = definitionsOnly ? where.PreviousDef : where.Previous;
        if (definitionsOnly)
        {
            access.PreviousDef = previous; access.NextDef = where; where.PreviousDef = access;
            if (previous is not null) previous.NextDef = access;
        }
        else
        {
            access.Previous = previous; access.Next = where; where.Previous = access;
            if (previous is not null) previous.Next = access;
        }
        if (previous is null) First = access;
        ++Count;
    }
}
