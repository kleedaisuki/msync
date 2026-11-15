// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/AliasAnalysis.h CaptureAnalysis.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public abstract class LlvmCaptureAnalysis
{
    public abstract LlvmCaptureComponents GetCapturesBefore(LlvmValue value, LlvmInstruction? instruction,
        bool orAt, bool returnCaptures);
}
