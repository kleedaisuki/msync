// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/SimplifyQuery.h.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmCondContext(LlvmValue condition)
{
    public LlvmValue Cond { get; set; } = condition;
    public bool Invert { get; set; }
    public HashSet<LlvmValue> AffectedValues { get; } = new(ReferenceEqualityComparer.Instance);
}
