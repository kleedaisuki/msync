// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: GenericDomTree.h recalculate.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public void Recalculate(LlvmFunction function, Dictionary<string, ulong>? metrics = null)
    {
        Function = function;
        CalculateFromScratch(null, metrics);
    }
}
