// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp SimplifyQuery::DL.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static readonly LlvmDataLayout DefaultQueryLayout = new();
    private static LlvmDataLayout GetQueryLayout(LlvmValue value, LlvmInstruction? context) =>
        (value switch
        {
            LlvmInstruction instruction => instruction.Parent?.Parent?.Parent,
            LlvmGlobalValue global => global.Parent,
            LlvmArgument argument => argument.Parent?.Parent,
            _ => null
        })?.DataLayout ?? context?.Parent?.Parent?.Parent?.DataLayout ?? DefaultQueryLayout;
}
