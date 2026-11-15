// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IntrinsicInst.h getDest/getSource.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static class LlvmMemIntrinsicPointerViews
{
    public static LlvmValue GetDest(this LlvmMemIntrinsic memory) => LlvmValueTracking.StripPointerCasts(memory.RawDest);
    public static LlvmValue GetSource(this LlvmMemIntrinsic memory) => LlvmValueTracking.StripPointerCasts(memory.RawSource);
}
