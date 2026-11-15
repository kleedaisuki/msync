// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IROptions.td, i2p-p2i-opt.
namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    public bool IntToPtrPtrToIntOptimization { get; set; } = true;
}
