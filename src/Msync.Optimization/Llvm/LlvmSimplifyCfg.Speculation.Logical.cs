// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp createLogicalOp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private LlvmValue CreateLogicalOp(LlvmTransformBuilder builder, LlvmOpcode opcode,
            LlvmValue left, LlvmValue right, string name = "")
        {
            if (LlvmValueTracking.ImpliesPoison(right, left, Metrics))
            { Count("logical_op.bitwise_relaxed"); return builder.CreateBinOp(opcode, left, right, name); }
            Count("logical_op.poison_blocking");
            return opcode switch
            {
                LlvmOpcode.And => builder.CreateLogicalAnd(left, right, name),
                LlvmOpcode.Or => builder.CreateLogicalOr(left, right, name),
                _ => throw new InvalidOperationException("Invalid logical opcode in source createLogicalOp.")
            };
        }
    }
}
