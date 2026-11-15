using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<int, int> addressViewLayouts = [];

        internal int AddressViewLayout(int pointee)
        {
            if (addressViewLayouts.TryGetValue(pointee, out int layout)) return layout;
            layout = Layouts.Count;
            Layouts.Add(new([new("", pointee)]));
            addressViewLayouts.Add(pointee, layout);
            return layout;
        }
    }

    private sealed partial class Body
    {
        private int addressViews;

        private int AddressView(int source, int type)
        {
            int result = Register(type);
            int layout = owner.AddressViewLayout(owner.Types[type].Pointee);
            // LLVM pointers are untyped addresses. NativeAddress additionally carries
            // its load/store view; selecting that view does not change the address.
            Add(new(NativeOp.AddressOffset, result, source, layout, 0, Source: instructionSource));
            ++addressViews;
            return result;
        }
    }
}
