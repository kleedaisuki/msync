using Msync.Core.Native;
using Msync.Core;

namespace Msync.Runtime.Native;

internal readonly record struct NativeProjectionField(int Layout, NativeFieldLayout Field);

internal readonly record struct NativeExecutionOperand(NativeType? Type, NativeLayout? Layout,
    NativeFieldLayout Field, NativeProjectionField[]? Projection, Msync.Core.NativeSignature? Signature)
{
    internal static NativeExecutionOperand Link(NativeImage image, NativeInstruction code)
    {
        NativeType? type = code.Type >= 0 ? image.Type(code.Type) : null;
        NativeLayout? layout = code.Op switch
        {
            NativeOp.Allocate => image.Layout(code.A),
            NativeOp.AddressOffset or NativeOp.EnvironmentAddress => image.Layout(code.B),
            NativeOp.IndexAddress or NativeOp.RangeFromMemory => image.Layout(code.C),
            NativeOp.CopyMemory or NativeOp.ClearMemory => image.Layout(checked((int)code.Immediate)),
            _ => null
        };
        NativeFieldLayout field = code.Op == NativeOp.AddressOffset ? layout![code.C] : default;
        NativeProjectionField[]? projection = code.Op == NativeOp.ProjectAddress
            ? image.Program.Projections[code.B].Cases.Select(item => new NativeProjectionField(item.Layout, image.Layout(item.Layout)[item.Field])).ToArray() : null;
        Msync.Core.NativeSignature? signature = code.Op == NativeOp.CallNative && NativeSignatures.TryGet(code.Symbol!, out Msync.Core.NativeSignature? found) ? found : null;
        return new(type, layout, field, projection, signature);
    }

    internal NativeAddress AddressField(NativeAddress address) => address.OffsetBy(Field.Offset, Field.Type);

    internal NativeAddress Project(NativeImage image, NativeAddress address)
    {
        NativeProjectionField[] cases = Projection!;
        if (cases.Length == 1) return address.OffsetBy(cases[0].Field.Offset, cases[0].Field.Type);
        int layout = image.LayoutIdentity(address);
        foreach (NativeProjectionField candidate in cases)
            if (candidate.Layout == layout) return address.OffsetBy(candidate.Field.Offset, candidate.Field.Type);
        throw new InvalidOperationException($"Allocation layout {layout} is outside its compiled address projection.");
    }
}
