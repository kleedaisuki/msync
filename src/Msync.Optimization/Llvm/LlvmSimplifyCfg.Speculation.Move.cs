// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// Local.cpp hoistAllInstructionsInto.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private void HoistAllInstructionsInto(LlvmBasicBlock destination, LlvmInstruction insertPoint, LlvmBasicBlock source)
        {
            for (LlvmInstruction? current = source.FirstInstruction; current is not null && !current.IsTerminator;)
            {
                LlvmInstruction? next = current.Next;
                // LLVM debug records / UB-implying call-site attributes and
                // metadata are absent; project diagnostic SourceSpan is retained.
                current.RemoveFromParent();
                destination.InsertBefore(current, insertPoint);
                Count("speculation.instructions_hoisted");
                current = next;
            }
        }
    }
}
