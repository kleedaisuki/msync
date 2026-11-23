namespace Msync.Core.Hpir;

public static class HpirWord32Imports
{
    public static bool TryCreate(string name, out HpirImport import)
    {
        if (name.StartsWith("bits.", StringComparison.Ordinal) && NativeSignatures.TryGet(name, out var signature)
            && signature.Numeric is >= NumericIntrinsic.And and <= NumericIntrinsic.ReverseBytes)
        {
            import = new(HpirService.Word32, (int)signature.Numeric, HpirWidth.U64,
                Arity(signature.Numeric) == 1 ? [HpirWidth.U64] : [HpirWidth.U64, HpirWidth.U64]);
            return true;
        }
        import = null!;
        return false;
    }

    internal static bool Valid(HpirImport import)
    {
        if (import.Binding < (int)NumericIntrinsic.And || import.Binding > (int)NumericIntrinsic.ReverseBytes
            || import.Result != HpirWidth.U64) return false;
        return Arity((NumericIntrinsic)import.Binding) == 1
            ? import.Arguments is [HpirWidth.U64] : import.Arguments is [HpirWidth.U64, HpirWidth.U64];
    }

    private static int Arity(NumericIntrinsic operation) =>
        operation is NumericIntrinsic.Not or NumericIntrinsic.PopCount or NumericIntrinsic.LeadingZeroCount
            or NumericIntrinsic.TrailingZeroCount or NumericIntrinsic.ReverseBytes ? 1 : 2;
}
