using Msync.Core.Native;
using Msync.Core;

namespace Msync.Runtime.Native;

internal readonly record struct NativeProjectionField(int Layout, NativeFieldLayout Field);

internal readonly record struct NativeExecutionOperand(NativeType? Type, NativeLayout? Layout,
    NativeFieldLayout Field, NativeProjectionField[]? Projection, Msync.Core.NativeSignature? Signature, bool NeedsLayoutCheck = true)
{
    internal static NativeExecutionOperand Link(NativeImage image, NativeFunctionCode function, NativeInstruction code)
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
        bool needsLayoutCheck = true;
        if (code.Op is NativeOp.Narrow or NativeOp.Convert && code.A >= 0 && code.Type >= 0)
        {
            NativeCodeType src = image.Program.Types[function.Slots[code.A].Type];
            NativeCodeType dst = image.Program.Types[code.Type];
            if (src.Pointee < 0 && dst.Pointee < 0
                && (dst.Layouts is null || (src.Layouts is { } srcLayouts && dst.Layouts is { } dstLayouts
                    && srcLayouts.All(layoutId => dstLayouts.Contains(layoutId)))))
                needsLayoutCheck = false;
        }
        return new(type, layout, field, projection, signature, needsLayoutCheck);
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
