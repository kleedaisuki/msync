namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterEnvironment(IDictionary<string, NativeSignature> result)
    {
        void Add(string name, TypeRef output, NativeEffect effect, params TypeRef[] inputs) =>
            result.Add("environment." + name, new("environment." + name, inputs, output, effect));
        Add("get", TypeRef.String.WithNull, NativeEffect.Read, TypeRef.String);
        Add("set", TypeRef.Null, NativeEffect.Write, TypeRef.String, TypeRef.String);
        Add("unset", TypeRef.Null, NativeEffect.Write, TypeRef.String);
        Add("variables", new(TypeKind.Range, TypeRef.Scope), NativeEffect.Read);
        Add("working_directory", TypeRef.Path, NativeEffect.Read);
        Add("home_directory", TypeRef.Path, NativeEffect.Read);
        Add("operating_system", TypeRef.String, NativeEffect.Read);
        Add("newline", TypeRef.String, NativeEffect.Read);
        Add("path_separator", TypeRef.String, NativeEffect.Read);
        foreach (string name in new[] { "get", "set", "unset", "variables" })
            result["environment." + name] = result["environment." + name] with { Behavior = NativeBehavior.MayFail };
    }
}
