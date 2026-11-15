using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private void ReturnSlot(Activation activation, int slot)
    {
        NativeAddress destination = activation.ReturnDestination;
        if (destination.IsNull)
        {
            activation.Result = slot < 0 ? default : activation.Frame.GetDatum(slot);
            return;
        }
        if (slot < 0) memory.Write(destination, default);
        else memory.CopyCompiled(activation.Frame.Location(slot), destination);
        if (profile is not null) activation.Work.Counters.DirectReturn(scalar: false);
    }

    private void ReturnScalar(Activation activation, NativeKind kind, ulong bits)
    {
        NativeAddress destination = activation.ReturnDestination;
        if (destination.IsNull)
        {
            activation.Result = new(kind == NativeKind.Number
                ? NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(bits)) : NativeValue.FromBool(bits != 0));
            return;
        }
        memory.RemovePending(destination);
        NativeStorage.StoreScalarTrusted(destination, kind, bits);
        if (profile is not null) activation.Work.Counters.DirectReturn(scalar: true);
    }
}
