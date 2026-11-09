using Msync.Compiler.Syntax;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<ScopeExpr, Info> namedScopeInfos = new(ReferenceEqualityComparer.Instance);

    private static void CheckSingletonShape(Frame frame, Info expected, Info actual, SourceSpan span, string path)
    {
        if (expected.Scope is not { } schema || actual.Scope is not { } value) return;
        foreach (var (name, field) in schema.Members)
        {
            if (!value.Members.TryGetValue(name, out var member) || schema.Storage[name].Type is { } constraint && !constraint.Accepts(member.Type))
                throw Error(frame, $"Named Scope field '{path}::{name}' has a different storage schema in this specialization.", span,
                    label: $"expected field '{name}' with type {schema.Storage[name].Type ?? field.Type}",
                    help: "A named Scope is one shared instance. Keep its field types consistent across calls, or use an anonymous scope for per-call instances.", code: "E0203");
            CheckSingletonShape(frame, field, member, span, path + "::" + name);
        }
    }
}
