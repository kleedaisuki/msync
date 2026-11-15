// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: lib/Analysis/MemorySSA.cpp
// createDefinedAccess, insertIntoLists*, prepareForMoveTo/moveTo, removeFrom*.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmMemorySsaInsertionPlace { Beginning, End, BeforeTerminator }

public sealed partial class LlvmMemorySsa
{
    internal LlvmMemoryUseOrDef? CreateDefinedAccess(LlvmInstruction instruction, LlvmMemoryAccess? definition,
        LlvmMemoryUseOrDef? template = null, bool creationMustSucceed = true)
    {
        Debug.Assert(instruction is not LlvmPhiInstruction);
        var access = CreateNewAccess(instruction, new LlvmBatchAAResults(AA), template);
        Debug.Assert(!creationMustSucceed || access is not null);
        if (access is not null)
        {
            Debug.Assert(definition is not LlvmMemoryUse);
            access.SetDefiningAccess(definition);
        }
        return access;
    }

    internal void InsertIntoListsForBlock(LlvmMemoryAccess access, LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point)
    {
        var accesses = Accesses(block);
        if (point == LlvmMemorySsaInsertionPlace.Beginning)
        {
            if (access is LlvmMemoryPhi)
            { accesses.AddFirst(access); Definitions(block).AddFirst(access); }
            else
            {
                var where = accesses.First;
                while (where is LlvmMemoryPhi) where = where.Next;
                accesses.InsertBefore(access, where);
                if (access is not LlvmMemoryUse)
                {
                    var definition = Definitions(block).First;
                    while (definition is LlvmMemoryPhi) definition = definition.NextDef;
                    Definitions(block).InsertBefore(access, definition);
                }
            }
        }
        else
        {
            accesses.AddLast(access);
            if (access is not LlvmMemoryUse) Definitions(block).AddLast(access);
        }
        numberingValid.Remove(block);
    }

    internal void InsertIntoListsBefore(LlvmMemoryAccess access, LlvmBasicBlock block, LlvmMemoryAccess? where)
    {
        Accesses(block).InsertBefore(access, where);
        if (access is not LlvmMemoryUse)
        {
            var nextDefinition = where;
            while (nextDefinition is not null && nextDefinition is not LlvmMemoryDef) nextDefinition = nextDefinition.Next;
            Definitions(block).InsertBefore(access, nextDefinition);
        }
        numberingValid.Remove(block);
    }

    private void PrepareForMoveTo(LlvmMemoryAccess access, LlvmBasicBlock block)
    {
        RemoveFromLists(access, false);
        if (access is LlvmMemoryDef definition) definition.ResetOptimized();
        access.Block = block;
    }

    internal void MoveTo(LlvmMemoryUseOrDef access, LlvmBasicBlock block, LlvmMemoryAccess? where)
    {
        PrepareForMoveTo(access, block);
        InsertIntoListsBefore(access, block, where);
    }

    internal void MoveTo(LlvmMemoryAccess access, LlvmBasicBlock block, LlvmMemorySsaInsertionPlace point)
    {
        if (access is LlvmMemoryPhi)
        {
            Debug.Assert(point == LlvmMemorySsaInsertionPlace.Beginning && GetMemoryAccess(block) is null);
            valueToAccess.Remove(access.Block); valueToAccess.Add(block, access);
        }
        PrepareForMoveTo(access, block);
        InsertIntoListsForBlock(access, block, point);
    }

    internal void RemoveFromLookups(LlvmMemoryAccess access)
    {
        Debug.Assert(access.UseEmpty);
        numbering.Remove(access);
        if (access is LlvmMemoryUseOrDef useOrDef) useOrDef.SetDefiningAccess(null);
        if (access is not LlvmMemoryUse) GetWalker().InvalidateInfo(access);
        LlvmValue key = access is LlvmMemoryUseOrDef mud ? mud.MemoryInstruction! : access.Block;
        if (valueToAccess.TryGetValue(key, out var current) && ReferenceEquals(current, access)) valueToAccess.Remove(key);
    }

    internal void RemoveFromLists(LlvmMemoryAccess access, bool shouldDelete = true)
    {
        var block = access.Block;
        if (access is not LlvmMemoryUse)
        {
            var definitions = perBlockDefs[block]; definitions.Remove(access);
            if (definitions.Count == 0) perBlockDefs.Remove(block);
        }
        var accesses = perBlockAccesses[block]; accesses.Remove(access);
        if (shouldDelete) { access.IsDeleted = true; access.DropAllReferences(); }
        if (accesses.Count == 0) { perBlockAccesses.Remove(block); numberingValid.Remove(block); }
    }
}
