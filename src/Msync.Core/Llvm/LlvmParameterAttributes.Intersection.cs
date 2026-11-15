// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Attributes.cpp AttributeSet::intersectWith.
namespace Msync.Core.Llvm;

public readonly partial record struct LlvmParameterAttributes
{
    private const LlvmAttributeKind PreserveMask = LlvmAttributeKind.ByVal | LlvmAttributeKind.ByRef |
        LlvmAttributeKind.InAlloca | LlvmAttributeKind.Preallocated | LlvmAttributeKind.ElementType |
        LlvmAttributeKind.StructRet | LlvmAttributeKind.SExt | LlvmAttributeKind.ZExt |
        LlvmAttributeKind.SwiftSelf | LlvmAttributeKind.SwiftError | LlvmAttributeKind.Nest |
        LlvmAttributeKind.ImmArg | LlvmAttributeKind.Initializes;

    public bool TryIntersect(LlvmParameterAttributes other, out LlvmParameterAttributes result)
    {
        result = default;
        if ((Flags & PreserveMask) != (other.Flags & PreserveMask) || ByValType != other.ByValType ||
            ByRefType != other.ByRefType || InAllocaType != other.InAllocaType ||
            PreallocatedType != other.PreallocatedType || ElementType != other.ElementType ||
            StructRetType != other.StructRetType || !SameInitializes(other)) return false;
        if (Has(LlvmAttributeKind.ByVal) && Alignment != other.Alignment) return false;
        LlvmRangeAttribute? range = null;
        if (Range is { } leftRange && other.Range is { } rightRange)
        {
            var combined = leftRange.ToConstantRange().UnionWith(rightRange.ToConstantRange());
            if (!combined.IsFullSet) range = LlvmRangeAttribute.FromConstantRange(combined);
        }
        result = this with
        {
            Flags = Flags & other.Flags,
            Alignment = Alignment is { } left && other.Alignment is { } right ?
                (left.Value <= right.Value ? left : right) : null,
            DereferenceableBytes = Minimum(DereferenceableBytes, other.DereferenceableBytes),
            DereferenceableOrNullBytes = Minimum(DereferenceableOrNullBytes, other.DereferenceableOrNullBytes),
            DeadOnReturnBytes = Minimum(DeadOnReturnBytes, other.DeadOnReturnBytes),
            CaptureInfo = CaptureInfo is { } lc && other.CaptureInfo is { } rc ? lc | rc : null,
            NoFPClass = NoFPClass & other.NoFPClass,
            Range = range
        };
        return true;
    }
    private static ulong? Minimum(ulong? left, ulong? right) => left.HasValue && right.HasValue ? Math.Min(left.Value, right.Value) : null;
    private bool SameInitializes(LlvmParameterAttributes other) => ReferenceEquals(Initializes, other.Initializes) ||
        Initializes is not null && other.Initializes is not null && Initializes.SequenceEqual(other.Initializes);
    internal bool SourceEquals(LlvmParameterAttributes other) =>
        (this with { Initializes = null }) == (other with { Initializes = null }) && SameInitializes(other);
}
