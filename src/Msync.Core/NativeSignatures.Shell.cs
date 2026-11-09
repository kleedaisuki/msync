namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterShell(IDictionary<string, NativeSignature> result)
    {
        TypeRef lines = new(TypeKind.Range, TypeRef.String);
        result.Add("shell.tail_lines", new("shell.tail_lines", [lines, TypeRef.Number], lines,
            ResultRule: NativeResultRule.FirstArgument, Behavior: NativeBehavior.Deferred | NativeBehavior.MayFail));
        result.Add("shell.unique_lines", new("shell.unique_lines", [lines], lines,
            ResultRule: NativeResultRule.FirstArgument, Behavior: NativeBehavior.Deferred));
        result.Add("shell.which", new("shell.which", [TypeRef.String], TypeRef.Path.WithNull,
            NativeEffect.Read, Behavior: NativeBehavior.MayFail));
        result.Add("shell.process_table", new("shell.process_table", [], new(TypeKind.Range, TypeRef.Scope),
            NativeEffect.Read, Behavior: NativeBehavior.EagerRange | NativeBehavior.MayFail));
    }
}
