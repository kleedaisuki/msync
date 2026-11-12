namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterBits(IDictionary<string, NativeSignature> result)
    {
        void Add(string member, NumericIntrinsic operation, int arity)
        {
            string name = "bits." + member;
            result.Add(name, new(name, arity == 1 ? [TypeRef.Number] : [TypeRef.Number, TypeRef.Number],
                TypeRef.Number, Behavior: NativeBehavior.MayFail) { Numeric = operation });
        }

        Add("bit_and", NumericIntrinsic.And, 2);
        Add("bit_or", NumericIntrinsic.Or, 2);
        Add("bit_xor", NumericIntrinsic.Xor, 2);
        Add("bit_not", NumericIntrinsic.Not, 1);
        Add("shift_left", NumericIntrinsic.ShiftLeft, 2);
        Add("shift_right", NumericIntrinsic.ShiftRight, 2);
        Add("shift_right_signed", NumericIntrinsic.ShiftRightSigned, 2);
        Add("rotate_left", NumericIntrinsic.RotateLeft, 2);
        Add("rotate_right", NumericIntrinsic.RotateRight, 2);
        Add("pop_count", NumericIntrinsic.PopCount, 1);
        Add("leading_zero_count", NumericIntrinsic.LeadingZeroCount, 1);
        Add("trailing_zero_count", NumericIntrinsic.TrailingZeroCount, 1);
        Add("reverse_bytes", NumericIntrinsic.ReverseBytes, 1);
    }
}
