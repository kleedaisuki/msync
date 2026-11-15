// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LICM.cpp hoistGEP.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicmArithmetic
{
    private static bool HoistGep(LlvmInstruction instruction, in Query query)
    {
        if (instruction is not LlvmGetElementPtrInstruction gep || gep.HasAllConstantIndices ||
            gep.PointerOperand is not LlvmGetElementPtrInstruction source || !source.HasOneUse ||
            !query.Loop.Contains(source.Parent!)) return false;
        if (!query.Loop.IsLoopInvariant(source.PointerOperand)) return false;
        for (int index = 0; index < gep.IndexCount; ++index)
            if (!query.Loop.IsLoopInvariant(gep.GetIndex(index))) return false;
        bool allSourceInvariant = true;
        for (int index = 0; index < source.IndexCount; ++index)
            allSourceInvariant &= query.Loop.IsLoopInvariant(source.GetIndex(index));
        if (allSourceInvariant) return false;
        bool inbounds = source.NoWrapFlags.IsInBounds && gep.NoWrapFlags.IsInBounds;
        if (inbounds)
        {
            for (int index = 0; index < source.IndexCount; ++index)
                if (!LlvmKnownBitsAnalysis.IsKnownNonNegative(source.GetIndex(index), context: gep, tree: query.Tree, metrics: query.Metrics))
                { inbounds = false; break; }
        }
        if (inbounds)
        {
            for (int index = 0; index < gep.IndexCount; ++index)
                if (!LlvmKnownBitsAnalysis.IsKnownNonNegative(gep.GetIndex(index), context: gep, tree: query.Tree, metrics: query.Metrics))
                { inbounds = false; break; }
        }
        var flags = inbounds ? LlvmGepNoWrapFlags.InBounds : LlvmGepNoWrapFlags.None;
        var newSource = new LlvmGetElementPtrInstruction(gep.SourceElementType, source.PointerOperand, gep.Indices.ToArray(), flags)
        { Source = instruction.Source };
        newSource.SetName("invariant.gep");
        query.InsertPoint.Parent!.InsertBefore(newSource, query.InsertPoint);
        var result = new LlvmGetElementPtrInstruction(source.SourceElementType, newSource, source.Indices.ToArray(), flags)
        { Source = instruction.Source };
        result.SetName("gep");
        gep.Parent!.InsertBefore(result, gep);
        gep.ReplaceAllUsesWith(result);
        query.Erase(gep);
        query.Erase(source);
        Count(query, inbounds ? "gep.inbounds_preserved" : "gep.inbounds_dropped");
        return true;
    }
}
