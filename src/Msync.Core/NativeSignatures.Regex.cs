namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterRegex(IDictionary<string, NativeSignature> result)
    {
        void Add(string name, TypeRef output, params TypeRef[] inputs) =>
            result.Add("regex." + name, new("regex." + name, inputs, output, Behavior: NativeBehavior.MayFail));
        Add("is_match", TypeRef.Bool, TypeRef.String, TypeRef.String);
        Add("find", TypeRef.Scope.WithNull, TypeRef.String, TypeRef.String);
        Add("matches", new(TypeKind.Range, TypeRef.Scope), TypeRef.String, TypeRef.String);
        Add("replace", TypeRef.String, TypeRef.String, TypeRef.String, TypeRef.String);
        Add("split", new(TypeKind.Range, TypeRef.String), TypeRef.String, TypeRef.String);
        Add("escape", TypeRef.String, TypeRef.String);
        result["regex.escape"] = result["regex.escape"] with { Behavior = NativeBehavior.None };
    }
}
