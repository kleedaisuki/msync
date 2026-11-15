// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Diagnostics for the source AddSub instruction-combining family.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private T RecordAddSubRewrite<T>(T instruction, string decision) where T : LlvmInstruction
        {
            if (Metrics is not null) Count("instcombine.addsub.rewrite." + decision);
            return instruction;
        }
    }
}
