// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// analyzeKnownBitsFromAndXorOr / getKnownBitsFromAndXorOr, scalar path.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static LlvmKnownBits AnalyzeKnownBitsFromAndXorOr(LlvmBinaryOperator instruction,
        LlvmKnownBits left, LlvmKnownBits right, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        Count(metrics, "known_bits.bitwise_analysis.queries");
        var result = instruction.Opcode switch
        {
            LlvmOpcode.And => left & right,
            LlvmOpcode.Or => left | right,
            LlvmOpcode.Xor => left ^ right,
            _ => throw new InvalidOperationException("Invalid opcode for analyzeKnownBitsFromAndXorOr")
        };
        return RefineBitwise(instruction, left, right, result,
            new(depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
    }
    public static LlvmKnownBits AdjustKnownBitsForSelectArm(LlvmKnownBits known, LlvmValue condition,
        LlvmValue arm, bool invert, uint depth = 0, LlvmInstruction? context = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null) =>
        AdjustSelectArm(known, condition, arm, invert, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);

}
