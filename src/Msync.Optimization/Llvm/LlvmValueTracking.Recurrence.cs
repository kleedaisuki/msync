// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// matchTwoInputRecurrence and both matchSimpleRecurrence overloads.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmValueTracking
{
    public static bool MatchSimpleRecurrence(LlvmPhiInstruction phi, out LlvmBinaryOperator binary,
        out LlvmValue start, out LlvmValue step)
    {
        if (phi.IncomingCount == 2)
            for (int i = 0; i != 2; ++i)
                if (phi.GetIncomingValue(i) is LlvmBinaryOperator operation && (operation.Left == phi || operation.Right == phi))
                {
                    binary = operation; start = phi.GetIncomingValue(1 - i);
                    step = operation.Left == phi ? operation.Right : operation.Left; return true;
                }
        binary = null!; start = step = null!; return false;
    }
    public static bool MatchSimpleRecurrence(LlvmBinaryOperator binary, out LlvmPhiInstruction phi,
        out LlvmValue start, out LlvmValue step)
    {
        phi = (binary.Left as LlvmPhiInstruction ?? binary.Right as LlvmPhiInstruction)!;
        if (phi is not null && MatchSimpleRecurrence(phi, out var recurrence, out start, out step) && recurrence == binary) return true;
        start = step = null!; return false;
    }
}
