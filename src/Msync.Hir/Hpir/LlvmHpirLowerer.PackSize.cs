using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal bool SizeOnlyPack(LlvmCallInstruction call) => NativeRangeCall(call, "ranges.of")
            && call.Users.All(user => user is LlvmCallInstruction { ArgumentCount: 1 } size
                && NativeRangeCall(size, "ranges.size") && size.GetArgument(0) == call);
    }

    private sealed partial class Body
    {
        private int fixedPackSizes;

        private bool TryFixedPackSize(LlvmCallInstruction call)
        {
            if (geometry.SizeOnlyPack(call)) return true;
            if (call.ArgumentCount != 1 || !geometry.NativeRangeCall(call, "ranges.size")
                || call.GetArgument(0) is not LlvmCallInstruction pack || !geometry.SizeOnlyPack(pack)) return false;
            Add(new(HpirOp.ConstU64, Location(call), Immediate: BitConverter.DoubleToInt64Bits(pack.ArgumentCount)), call.Source);
            ++fixedPackSizes;
            return true;
        }
    }
}
