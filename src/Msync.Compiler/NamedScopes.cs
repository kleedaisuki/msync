using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<ScopeExpr, Info> namedScopeInfos = new(ReferenceEqualityComparer.Instance);

    private Binding ReserveScope(Frame frame, string name, SourceSpan? span)
    {
        Binding binding = new(frame.Allocate(), new(TypeRef.Scope),
            Origin: DeclarationOrigin(frame, span), Constraint: TypeRef.Scope);
        var site = (span?.Start ?? -1, name);
        if (frame.Declarations.TryGetValue(site, out var saved))
            binding = binding with { Identity = saved.Identity, Storage = saved.Storage };
        else frame.Declarations.Add(site, binding);
        frame.Bind(name, binding);
        frame.Emit(OpCode.Null);
        frame.Emit(OpCode.InitializeLocal, binding.Slot);
        return binding;
    }

    private Info ScopeDeclarationValue(Frame frame, ScopeDeclaration declaration)
    {
        var binding = ReserveScope(frame, declaration.Name, declaration.NameSpan);
        var info = Scope(frame, declaration.Scope, named: true, self: binding);
        RecordSessionValue(info);
        if (declaration.NameSpan is { } span) Observe(frame, span, info, declaration.Name);
        if (binding.Origin is { } origin) CaptureDefinition(frame, origin.Span, [origin]);
        return info;
    }

    private Info DeclareScope(Frame frame, ScopeDeclaration declaration)
    {
        ScopeDeclarationValue(frame, declaration);
        frame.Emit(OpCode.Pop);
        frame.Emit(OpCode.Null);
        return new(TypeRef.Null);
    }

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
