using System.Collections.Immutable;
using System.Text;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<HirTypeId, bool> exportTypeParameters = [];

    private SemanticValue ResolveExportValue(SemanticValue value, SemanticContext context)
    {
        var type = ExportType(value.Type);
        var storage = value.StorageConstraint is { } constraint ? ExportType(constraint) : value.StorageConstraint;
        return type == value.Type && storage == value.StorageConstraint ? value
            : value with { Type = type, StorageConstraint = storage };

        HirTypeId ExportType(HirTypeId type)
        {
            if (!HasExportTypeParameters(type) || !TypeWitnessAvailable(type, context)) return type;
            Trace?.Metric("export.dependent_types", 1);
            return ResolveHirType(type, context);
        }
    }

    private bool HasExportTypeParameters(HirTypeId type)
    {
        var description = hir.Types[type.Value];
        if (description.TypeParameter >= 0) return true;
        if (description.Shape < 0 && description.Element < 0 && description.Alternatives.IsDefaultOrEmpty) return false;
        if (exportTypeParameters.TryGetValue(type, out bool open)) return open;
        HashSet<HirTypeId> seen = [];
        open = Visit(type);
        exportTypeParameters.Add(type, open);
        return open;

        bool Visit(HirTypeId id)
        {
            if (!seen.Add(id)) return false;
            if (exportTypeParameters.TryGetValue(id, out bool open)) return open;
            var candidate = hir.Types[id.Value];
            return candidate.TypeParameter >= 0
                || candidate.Element >= 0 && Visit(new(candidate.Element))
                || candidate.Shape >= 0 && hir.ScopeLayouts[candidate.Shape].Fields.Any(field => Visit(field.StorageType))
                || !candidate.Alternatives.IsDefaultOrEmpty && candidate.Alternatives.Any(Visit);
        }
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
