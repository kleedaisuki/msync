// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVMContextImpl constant-table ownership, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
namespace Msync.Core.Llvm;

public sealed partial class LlvmContext
{
    internal readonly Dictionary<uint, LlvmConstantInt> IntZeroConstants = new();
    internal readonly Dictionary<uint, LlvmConstantInt> IntOneConstants = new();
    internal readonly Dictionary<LlvmAPInt, LlvmConstantInt> IntConstants = new();
    internal LlvmConstantInt? TrueConstant;
    internal LlvmConstantInt? FalseConstant;
    internal readonly Dictionary<LlvmAPFloat, LlvmConstantFP> FPConstants = new();
    internal readonly Dictionary<LlvmType, LlvmUndefValue> UndefConstants = new();
    internal readonly Dictionary<LlvmType, LlvmPoisonValue> PoisonConstants = new();
    internal readonly Dictionary<LlvmType, LlvmConstantPointerNull> PointerNullConstants = new();
    internal readonly Dictionary<LlvmType, LlvmConstantAggregateZero> AggregateZeroConstants = new();
    internal readonly Dictionary<LlvmConstantDataKey, LlvmConstantDataArray> DataArrayConstants = new();
}
