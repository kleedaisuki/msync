using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private int functionProjectionExactAddresses, functionProjectionNullableAddresses;
        private int functionProjectionValueHints, functionProjectionUseHints, functionProjectionCanonicalHints;
        private int functionProjectionCarrierCopies;

        private int FunctionMemoryProjectionType(LlvmLoadInstruction load, int component)
        {
            var structure = LlvmStructType.GetTypeByName(function.Context, "msync.function")!;
            return FunctionProjectionType(load, AggregateShapeFor(structure).Leaves[component]);
        }

        private int FunctionProjectionType(LlvmValue value, AggregateLeaf leaf)
        {
            if (leaf.FunctionComponent != 1) return leaf.NativeType;
            if (IsEnvironmentCarrier(value.LogicalType))
            {
                ++functionProjectionValueHints;
                return value.LogicalType;
            }
            foreach (var use in value.Uses)
                if (IsEnvironmentCarrier(use.LogicalType))
                {
                    ++functionProjectionUseHints;
                    return use.LogicalType;
                }
            ++functionProjectionCanonicalHints;
            return leaf.NativeType;
        }

        private bool IsEnvironmentCarrier(int type)
        {
            if (type < 0) return false;
            var carrier = owner.Types[type];
            return !carrier.IsMachine && carrier.Pointee < 0 && carrier.Kind != TypeKind.Never
                && (carrier.Kind & ~(TypeKind.Scope | TypeKind.Null)) == 0;
        }

        private void RecordFunctionProjection(int type, int component)
        {
            ++functionProjections;
            if (component != 1) return;
            if (owner.Types[type].Kind == TypeKind.Scope) ++functionProjectionExactAddresses;
            else ++functionProjectionNullableAddresses;
        }
    }
}
