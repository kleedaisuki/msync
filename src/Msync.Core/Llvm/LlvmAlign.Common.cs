// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/Alignment.h commonAlignment;
// MathExtras.h MinAlign.
using System.Numerics;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmAlign
{
    public static LlvmAlign CommonAlignment(LlvmAlign alignment, ulong offset)
    {
        ulong combined = alignment.Value | offset;
        return new(1UL << BitOperations.TrailingZeroCount(combined));
    }
    public static LlvmAlign CommonAlignment(LlvmAlign left, LlvmAlign right) => left < right ? left : right;
}
