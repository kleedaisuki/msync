using Msync.Core;

namespace Msync.Compiler.Semantics;

internal static class SemanticControl
{
    public static bool Terminates(TypeRef type) => type.Kind == TypeKind.Never;
    public static TypeRef Discard(TypeRef type) => Terminates(type) ? type : TypeRef.Null;
}
