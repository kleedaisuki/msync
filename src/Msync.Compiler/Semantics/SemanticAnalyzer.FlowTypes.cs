using System.Collections.Immutable;
using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private HirTypeId FlowType(SemanticValue value, SemanticContext context)
    {
        Dictionary<(int Region, int Layout), HirTypeId> scopes = [];
        return Project(value);

        HirTypeId Project(SemanticValue current)
        {
            HirType type = hir.Types[current.Type.Value];
            if (current.Scope is not { } initial)
                return current.Element is { } element ? hir.Type(type with { Element = Project(element).Value }) : current.Type;
            if (context.InputFields?.OwnsScope(initial.Region) == true)
                return ResolveHirType(current.Type, context);
            SemanticScope scope = context.Objects.GetValueOrDefault(initial.Region, initial);
            var identity = (scope.Region, scope.Region >= 0 ? -1 : scope.Layout);
            if (scopes.TryGetValue(identity, out var recursive))
                return hir.Type(type with { Shape = hir.Types[recursive.Value].Shape });
            HirTypeId row = hir.RecursiveScope("observed:" + Identity(current), self =>
            {
                scopes[identity] = self;
                return new(hir.ScopeLayouts[scope.Layout].Fields.Select(field =>
                {
                    SemanticValue member = Member(scope, field);
                    HirTypeId storage = scope.Locations.TryGetValue(field.Name, out var location)
                        ? StorageType(location, context) : field.StorageType;
                    HirTypeId projected = member.Scope is not null || member.Element is not null ? Project(member)
                        : member.StorageConstraint ?? storage;
                    return field with { StorageType = field.AdmitsNull ? Nullable(projected) : projected };
                }).ToImmutableArray());
            });
            scopes[identity] = row;
            return hir.Type(type with { Shape = hir.Types[row.Value].Shape, Alternatives = default });
        }

        SemanticValue Member(SemanticScope scope, HirField field)
        {
            if (scope.Locations.TryGetValue(field.Name, out var location) && context.Values.TryGetValue(location, out var stored))
                return stored;
            return scope.Fields.GetValueOrDefault(field.Name)
                ?? ValueForType(field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType);
        }

        string Identity(SemanticValue root)
        {
            StringBuilder key = new();
            Dictionary<(int Region, int Layout), int> seen = [];
            Write(root);
            return key.ToString();

            void Write(SemanticValue current)
            {
                if (current.Scope is not { } initial)
                {
                    key.Append('t').Append(current.StorageConstraint?.Value ?? current.Type.Value);
                    if (current.Element is { } element) { key.Append('('); Write(element); key.Append(')'); }
                    return;
                }
                SemanticScope scope = context.Objects.GetValueOrDefault(initial.Region, initial);
                var identity = (scope.Region, scope.Region >= 0 ? -1 : scope.Layout);
                if (seen.TryGetValue(identity, out int index)) { key.Append('^').Append(index); return; }
                seen.Add(identity, seen.Count);
                key.Append('{');
                foreach (HirField field in hir.ScopeLayouts[scope.Layout].Fields)
                {
                    key.Append(field.Name.Length).Append(':').Append(field.Name).Append(field.AdmitsNull ? '?' : '!');
                    Write(Member(scope, field));
                }
                key.Append('}');
            }
        }
    }
}
