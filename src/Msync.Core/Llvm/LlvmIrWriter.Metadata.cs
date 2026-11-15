// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: LLVMContext MD_invariant_load,
// Instructions.cpp LoadInst::cloneImpl and AsmWriter.cpp metadata attachments.
namespace Msync.Core.Llvm;

public static partial class LlvmIrWriter
{
    private sealed partial class Writer
    {
        private bool hasInvariantLoadMetadata;

        private string InvariantLoadMetadata()
        {
            hasInvariantLoadMetadata = true;
            return ", !invariant.load !0";
        }
    }
}
