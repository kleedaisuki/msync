// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Function.cpp
// Function::renumberBlocks / validateBlockNumbers.
using System.Collections;
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmFunction
{
    private uint blockNumberEpoch;
    public uint GetBlockNumberEpoch() => blockNumberEpoch;

    public void RenumberBlocks(Dictionary<string, ulong>? metrics = null)
    {
        ValidateBlockNumbers();
        NextBlockNum = 0;
        foreach (LlvmBasicBlock block in blocks) block.Number = NextBlockNum++;
        blockNumberEpoch = unchecked(blockNumberEpoch + 1);
        if (metrics is null) return;
        metrics["function.block_numbers.renumbered"] = metrics.GetValueOrDefault("function.block_numbers.renumbered") + 1;
        metrics["function.block_numbers.blocks"] = metrics.GetValueOrDefault("function.block_numbers.blocks") + (ulong)blocks.Count;
    }

    [Conditional("DEBUG")]
    private void ValidateBlockNumbers()
    {
        var numbers = new BitArray((int)NextBlockNum);
        foreach (LlvmBasicBlock block in blocks)
        {
            Debug.Assert(block.Number < NextBlockNum);
            Debug.Assert(!numbers[(int)block.Number]);
            numbers[(int)block.Number] = true;
        }
    }
}
