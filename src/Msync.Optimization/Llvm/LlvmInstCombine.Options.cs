// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombine.h and InstCombineCLOptions.td.
namespace Msync.Optimization.Llvm;

public sealed record LlvmInstCombineOptions
{
    public bool VerifyFixpoint { get; init; }
    public uint MaxIterations { get; init; } = 1;
}

public sealed record LlvmInstCombineTuning
{
    public bool CodeSinking { get; init; } = true;
    public uint GuardWideningWindow { get; init; } = 3;
    public bool LowerDbgDeclare { get; init; } = true;
    public uint MaxAllocsiteRemovableUsers { get; init; } = 2048;
    public uint MaxCopiedFromConstantUsers { get; init; } = 300;
    public uint MaxNumPhis { get; init; } = 512;
    public uint MaxSinkUsers { get; init; } = 32;
    public uint MaxTypeEvalDepth { get; init; } = 128;
    public uint MaxArraySize { get; init; } = 1024;
    public bool NegatorEnabled { get; init; } = true;
    public uint NegatorMaxDepth { get; init; } = uint.MaxValue;
    public uint SimplifyVectorEltsDepth { get; init; } = 10;
    public bool VerifyKnownBits { get; init; }
}
