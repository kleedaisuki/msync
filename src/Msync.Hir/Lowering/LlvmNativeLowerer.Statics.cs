using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private int staticTests, staticAddresses, staticPublications;

        private int StaticObjectSlot(LlvmGlobalVariable global, int declaration)
        {
            if (!values.TryGetValue(global, out int slot))
            {
                slot = Register(owner.Type(global));
                values.Add(global, slot);
            }
            // A global address is available at every use. Materialize it here,
            // not on first use in a different control-flow branch or at entry.
            Add(new(NativeOp.StaticAddress, slot, declaration, Source: instructionSource));
            ++staticAddresses;
            return slot;
        }
        private bool StaticLoad(LlvmLoadInstruction load)
        {
            if (ZeroOffsetPointerRoot(load.PointerOperand) is not LlvmGlobalVariable flag || !owner.StaticFlags.TryGetValue(flag, out int declaration))
                return false;
            Add(new(NativeOp.TestStatic, Slot(load), declaration, Source: load.Source));
            ++staticTests;
            return true;
        }
        private bool StaticStore(LlvmStoreInstruction store)
        {
            if (ZeroOffsetPointerRoot(store.PointerOperand) is not LlvmGlobalVariable flag || !owner.StaticFlags.TryGetValue(flag, out int declaration))
                return false;
            if (store.ValueOperand is not LlvmConstantInt { IsOne: true })
                throw Unsupported(store, "non-publication write to singleton ready flag");
            Add(new(NativeOp.PublishStatic, A: declaration, Source: store.Source));
            ++staticPublications;
            return true;
        }
    }
}
