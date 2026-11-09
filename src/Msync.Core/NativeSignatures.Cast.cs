namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterCast(IDictionary<string, NativeSignature> signatures) =>
        signatures.Add("cast", new("cast", [TypeRef.Any, TypeRef.Type], TypeRef.Any,
            ResultRule: NativeResultRule.CastTarget, Behavior: NativeBehavior.MayFail));
}

public static class ExplicitConversions
{
    public static ConnectionValue? ConnectionFromText(string text) => text switch
    {
        "local" or "$local" => new(ConnectionKind.Local),
        "wsl" or "$wsl" => new(ConnectionKind.Wsl),
        "env" or "$env" or "environment" => new(ConnectionKind.Environment),
        "path" or "$path" or "searchpath" => new(ConnectionKind.SearchPath),
        "pwd" or "$pwd" => new(ConnectionKind.Local, "pwd"),
        "file" or "$file" => new(ConnectionKind.Local, "file"),
        _ when text.StartsWith("ssh://", StringComparison.Ordinal) && text.Length > 6 => new(ConnectionKind.Ssh, text[6..]),
        _ when text.StartsWith("wsl://", StringComparison.Ordinal) => new(ConnectionKind.Wsl, text[6..]),
        _ => null
    };

    public static bool Possible(TypeRef source, TypeRef target)
    {
        if (source.Kind == TypeKind.Never) return true;
        if (source.Nullable && !target.Nullable) return false;
        if (target.Accepts(source)) return true;
        if (target.WithoutNull.Kind == TypeKind.Range && source.WithoutNull.Kind == TypeKind.Range)
            return target.Element is null || source.Element is null || Possible(source.Element, target.Element);
        return (source.WithoutNull.Kind & Sources(target.WithoutNull.Kind)) != 0;
    }

    private static TypeKind Sources(TypeKind target) => target switch
    {
        TypeKind.String => TypeKind.String | TypeKind.Path | TypeKind.Bool | TypeKind.Number | TypeKind.Connection | TypeKind.Type,
        TypeKind.Number or TypeKind.Bool => TypeKind.Number | TypeKind.Bool | TypeKind.String | TypeKind.Path,
        TypeKind.Path => TypeKind.Path | TypeKind.String,
        TypeKind.Connection => TypeKind.Connection | TypeKind.String,
        TypeKind.Scope or TypeKind.Function or TypeKind.Process or TypeKind.Type => target,
        _ => TypeKind.Never
    };
}
