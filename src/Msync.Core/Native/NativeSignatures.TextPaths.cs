namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterTextPaths(IDictionary<string, NativeSignature> result)
    {
        void Add(string name, TypeRef output, NativeBehavior behavior, params TypeRef[] inputs) =>
            result.Add("string." + name, new("string." + name, inputs, output, Behavior: behavior));
        Add("length", TypeRef.Number, NativeBehavior.None, TypeRef.String);
        Add("utf8_size", TypeRef.Number, NativeBehavior.None, TypeRef.String);
        Add("is_ascii", TypeRef.Bool, NativeBehavior.None, TypeRef.String);
        Add("chars", new(TypeKind.Range, TypeRef.String), NativeBehavior.None, TypeRef.String);
        Add("lines", new(TypeKind.Range, TypeRef.String), NativeBehavior.None, TypeRef.String);
        Add("split_whitespace", new(TypeKind.Range, TypeRef.String), NativeBehavior.None, TypeRef.String);
        Add("char_at", TypeRef.String, NativeBehavior.MayFail, TypeRef.String, TypeRef.Number);
        Add("substring", TypeRef.String, NativeBehavior.MayFail, TypeRef.String, TypeRef.Number, TypeRef.Number);
        Add("find", TypeRef.Number.WithNull, NativeBehavior.None, TypeRef.String, TypeRef.String);
        Add("rfind", TypeRef.Number.WithNull, NativeBehavior.None, TypeRef.String, TypeRef.String);
        Add("compare", TypeRef.Number, NativeBehavior.None, TypeRef.String, TypeRef.String);
        Add("repeat", TypeRef.String, NativeBehavior.MayFail, TypeRef.String, TypeRef.Number);
        Add("join", TypeRef.String, NativeBehavior.Enumerates | NativeBehavior.MayFail, new(TypeKind.Range, TypeRef.String), TypeRef.String);
    }
}
