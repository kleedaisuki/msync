namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterMath(IDictionary<string, NativeSignature> result)
    {
        void Add(string names, TypeRef output, NativeBehavior behavior, params TypeRef[] inputs)
        {
            foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add("math." + name, new("math." + name, inputs, output, Behavior: behavior));
        }
        Add("abs floor ceil trunc round round_even sqrt cbrt exp exp2 expm1 log log2 log10 log1p sin cos tan asin acos atan sinh cosh tanh asinh acosh atanh",
            TypeRef.Number, NativeBehavior.None, TypeRef.Number);
        Add("is_finite is_nan is_infinite is_integer is_safe_integer signbit", TypeRef.Bool, NativeBehavior.None, TypeRef.Number);
        Add("divide fmin fmax pow atan2 hypot fmod remainder copysign nextafter", TypeRef.Number, NativeBehavior.None, TypeRef.Number, TypeRef.Number);
        Add("fma", TypeRef.Number, NativeBehavior.None, TypeRef.Number, TypeRef.Number, TypeRef.Number);
        Add("clamp", TypeRef.Number, NativeBehavior.MayFail, TypeRef.Number, TypeRef.Number, TypeRef.Number);
    }
}
