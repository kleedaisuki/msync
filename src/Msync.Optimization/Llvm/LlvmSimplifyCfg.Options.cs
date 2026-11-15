// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFGOptions.h and SimplifyCFG.cpp command-line defaults.
namespace Msync.Optimization.Llvm;

public sealed record LlvmSimplifyCfgOptions
{
    public int BonusInstThreshold { get; init; } = 1;
    public bool ForwardSwitchCondToPhi { get; init; }
    public bool ConvertSwitchRangeToICmp { get; init; }
    public bool ConvertSwitchToArithmetic { get; init; }
    public bool ConvertSwitchToLookupTable { get; init; }
    public bool NeedCanonicalLoop { get; init; } = true;
    public bool HoistCommonInsts { get; init; }
    public bool HoistLoadsStoresWithCondFaulting { get; init; }
    public bool SinkCommonInsts { get; init; }
    public bool SimplifyCondBranch { get; init; } = true;
    public bool SpeculateBlocks { get; init; } = true;
    public bool SpeculateUnpredictables { get; init; }
    public LlvmAssumptionCache? AC { get; init; }
}

public sealed record LlvmSimplifyCfgTuning
{
    public uint PhiNodeFoldingThreshold { get; init; } = 2;
    public uint TwoEntryPhiNodeFoldingThreshold { get; init; } = 4;
    public bool HoistCommon { get; init; } = true;
    public bool HoistLoadsWithCondFaulting { get; init; } = true;
    public bool HoistStoresWithCondFaulting { get; init; } = true;
    public uint HoistLoadsStoresWithCondFaultingThreshold { get; init; } = 6;
    public uint HoistCommonSkipLimit { get; init; } = 20;
    public bool SinkCommon { get; init; } = true;
    public bool HoistCondStores { get; init; } = true;
    public bool MergeCondStores { get; init; } = true;
    public bool MergeCondStoresAggressively { get; init; }
    public bool SpeculateOneExpensiveInst { get; init; } = true;
    public uint MaxSpeculationDepth { get; init; } = 10;
    public int MaxSmallBlockSize { get; init; } = 10;
    public uint BranchFoldThreshold { get; init; } = 2;
    public uint BranchFoldToCommonDestVectorMultiplier { get; init; } = 2;
    public bool EnableMergeCompatibleInvokes { get; init; } = true;
    public uint MaxSwitchCasesPerResult { get; init; } = 16;
    public uint MaxLoopHeaderThreadingSearchBlocks { get; init; } = 1024;
    public uint MaxJumpThreadingLiveBlocks { get; init; } = 24;
}
