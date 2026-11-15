// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Value.cpp
// stripPointerCastsAndOffsets and its six PointerStripKind instantiations.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    private enum PointerStripKind { ZeroIndices, ZeroIndicesAndAliases, ZeroIndicesSameRepresentation, ForAliasAnalysis, InBoundsConstantIndices, InBounds }
    public static LlvmValue StripPointerCasts(LlvmValue value, Dictionary<string, ulong>? metrics = null) =>
        StripPointerCastsAndOffsets(value, PointerStripKind.ZeroIndices, null, metrics);
    public static LlvmValue StripPointerCastsAndAliases(LlvmValue value, Dictionary<string, ulong>? metrics = null) =>
        StripPointerCastsAndOffsets(value, PointerStripKind.ZeroIndicesAndAliases, null, metrics);
    public static LlvmValue StripPointerCastsSameRepresentation(LlvmValue value, Dictionary<string, ulong>? metrics = null) =>
        StripPointerCastsAndOffsets(value, PointerStripKind.ZeroIndicesSameRepresentation, null, metrics);
    public static LlvmValue StripPointerCastsForAliasAnalysis(LlvmValue value, Dictionary<string, ulong>? metrics = null) =>
        StripPointerCastsAndOffsets(value, PointerStripKind.ForAliasAnalysis, null, metrics);
    public static LlvmValue StripInBoundsConstantOffsets(LlvmValue value, Dictionary<string, ulong>? metrics = null) =>
        StripPointerCastsAndOffsets(value, PointerStripKind.InBoundsConstantIndices, null, metrics);
    public static LlvmValue StripInBoundsOffsets(LlvmValue value, Action<LlvmValue>? callback = null,
        Dictionary<string, ulong>? metrics = null) => StripPointerCastsAndOffsets(value, PointerStripKind.InBounds, callback, metrics);

    private static LlvmValue StripPointerCastsAndOffsets(LlvmValue value, PointerStripKind kind,
        Action<LlvmValue>? callback, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "value_tracking.pointer_casts.queries");
        if (!value.Type.IsPointerTy()) return value;
        HashSet<LlvmValue> visited = new(ReferenceEqualityComparer.Instance) { value };
        do
        {
            Count(metrics, "value_tracking.pointer_casts.steps");
            callback?.Invoke(value);
            if (value is LlvmGetElementPtrInstruction gep)
            {
                if (kind is PointerStripKind.InBounds or PointerStripKind.InBoundsConstantIndices)
                {
                    if (kind == PointerStripKind.InBoundsConstantIndices && !gep.HasAllConstantIndices) return value;
                    if (!gep.NoWrapFlags.IsInBounds) return value;
                }
                else if (!gep.HasAllZeroIndices) return value;
                value = gep.PointerOperand;
            }
            else if (value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast)
            {
                if (!cast.Operand.Type.IsPointerTy()) return value;
                value = cast.Operand;
            }
            else if (kind != PointerStripKind.ZeroIndicesSameRepresentation &&
                value is LlvmCastInstruction { Opcode: LlvmOpcode.AddrSpaceCast } addressCast)
                value = addressCast.Operand;
            else if (kind == PointerStripKind.ForAliasAnalysis && value is LlvmPhiInstruction { IncomingCount: 1 } phi)
                value = phi.GetIncomingValue(0);
            else
            {
                // GlobalAlias and returned-argument/invariant-group call identities are absent.
                return value;
            }
        } while (visited.Add(value));
        Count(metrics, "value_tracking.pointer_casts.cycle");
        return value;
    }
}
