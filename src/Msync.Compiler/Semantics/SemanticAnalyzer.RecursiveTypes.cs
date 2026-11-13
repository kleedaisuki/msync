using System.Collections.Immutable;
using System.Text;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue ResolveSummaryTypes(SemanticValue value, SemanticContext context)
    {
        foreach (var pair in context.Values.ToArray())
            context.Values[pair.Key] = ResolveValue(pair.Value);
        foreach (var pair in context.Objects.ToArray())
        {
            SemanticScope scope = pair.Value;
            HirTypeId type = ResolveHirType(hir.Type(new HirType(Msync.Core.TypeRef.Scope, Shape: scope.Layout)), context);
            context.Objects[pair.Key] = scope with
            {
                Layout = hir.Types[type.Value].Shape,
                Fields = scope.Fields.ToImmutableDictionary(field => field.Key, field => ResolveValue(field.Value), StringComparer.Ordinal)
            };
        }
        return ResolveValue(value);

        SemanticValue ResolveValue(SemanticValue current) => current with
        {
            Type = current.Scope is not null || current.Element is not null ? FlowType(current, context)
                : TypeWitnessAvailable(current.Type, context) ? ResolveHirType(current.Type, context) : current.Type,
            StorageConstraint = current.StorageConstraint is { } constraint && TypeWitnessAvailable(constraint, context)
                ? ResolveHirType(constraint, context) : current.StorageConstraint
        };
    }

    private HirTypeId ResolveHirType(HirTypeId type, SemanticContext context)
    {
        Dictionary<int, HirTypeId> active = [];
        return ResolveGraph(ResolveHirTypeShallow(type, context));

        HirTypeId ResolveGraph(HirTypeId source)
        {
            if (!TypeWitnessAvailable(source, context)) return source;
            HirTypeId resolved = ResolveHirTypeShallow(source, context);
            HirType description = hir.Types[resolved.Value];
            if (description.Shape >= 0 && active.TryGetValue(description.Shape, out var recursive))
                return hir.Type(description with { Shape = hir.Types[recursive.Value].Shape });
            if (!NeedsResolution(resolved, [])) return resolved;
            if (description.Shape >= 0)
            {
                HirTypeId graph = hir.RecursiveScope("resolved:" + Identity(resolved), self =>
                {
                    active[description.Shape] = self;
                    return new(hir.ScopeLayouts[description.Shape].Fields.Select(field => field with
                    { StorageType = ResolveGraph(field.StorageType) }).ToImmutableArray());
                });
                HirTypeId result = hir.Type(description with { Shape = hir.Types[graph.Value].Shape });
                active[description.Shape] = result;
                return result;
            }
            int element = description.Element < 0 ? -1 : ResolveGraph(new(description.Element)).Value;
            var alternatives = description.Alternatives.IsDefaultOrEmpty ? description.Alternatives
                : description.Alternatives.Select(ResolveGraph).ToImmutableArray();
            return hir.Type(description with { Element = element, Alternatives = alternatives });
        }

        bool NeedsResolution(HirTypeId source, HashSet<HirTypeId> seen)
        {
            if (!seen.Add(source) || !TypeWitnessAvailable(source, context)) return false;
            HirTypeId resolved = ResolveHirTypeShallow(source, context);
            if (resolved != source) return true;
            HirType description = hir.Types[source.Value];
            return description.Element >= 0 && NeedsResolution(new(description.Element), seen)
                || description.Shape >= 0 && hir.ScopeLayouts[description.Shape].Fields.Any(field => NeedsResolution(field.StorageType, seen))
                || !description.Alternatives.IsDefaultOrEmpty && description.Alternatives.Any(alternative => NeedsResolution(alternative, seen));
        }

        string Identity(HirTypeId source)
        {
            StringBuilder key = new();
            Dictionary<HirTypeId, int> seen = [];
            Write(source);
            return key.ToString();

            void Write(HirTypeId input)
            {
                HirTypeId resolved = TypeWitnessAvailable(input, context) ? ResolveHirTypeShallow(input, context) : input;
                if (seen.TryGetValue(resolved, out int index)) { key.Append('^').Append(index); return; }
                seen.Add(resolved, seen.Count);
                HirType description = hir.Types[resolved.Value];
                key.Append('[').Append((ushort)description.RuntimeType.Kind).Append(':').Append(description.TypeParameter)
                    .Append(':').Append(description.Concept).Append(':').Append(description.Signature);
                key.Append(description.Shape >= 0 ? 's' : '-');
                if (description.Shape >= 0)
                    foreach (HirField field in hir.ScopeLayouts[description.Shape].Fields)
                    {
                        key.Append(field.Name.Length).Append(':').Append(field.Name).Append(field.AdmitsNull ? '?' : '!');
                        Write(field.StorageType);
                    }
                if (description.Element >= 0) Write(new(description.Element));
                foreach (HirTypeId alternative in description.Alternatives.AsSpan()) Write(alternative);
                key.Append(']');
            }
        }
    }
}
