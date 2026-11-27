using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private void FillTypeHoles(HirTypeId annotation, SemanticValue value, HirSourceRange source, SemanticContext context)
    {
        var type = hir.Types[annotation.Value];
        if (program.LocalTypeHoles.Contains(type.TypeParameter))
        {
            if (Runtime(value).Kind == TypeKind.Never)
                throw Error(source, "Cannot infer the type represented by '_'.", "E0202");
            context.Substitutions[annotation] = value.Type;
            Trace?.Metric("inference.local_hole", 1);
        }
        if (type.Element >= 0 && value.Element is { } element)
            FillTypeHoles(new(type.Element), element, source, context);
    }

    private Dictionary<HirTypeId, HirTypeId> InferGenerics(HirTemplateId id, HirTemplate template,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext caller, HirTypeId? expectedResult)
    {
        var variables = program.Templates[id.Value].Generics;
        Dictionary<int, BoundGenericParameter> indexed = variables.ToDictionary(
            variable => hir.Types[variable.Type.Value].TypeParameter);
        Dictionary<HirTypeId, SemanticValue> evidence = [];
        Dictionary<HirTypeId, HirTypeId> result = [];
        for (int i = 0; i < arguments.Count; i++)
            Collect(hir.Locations[template.Parameters[i].Value].StorageType, arguments[i], fromExpected: false);
        if (expectedResult is { } expected && template.ResultAnnotation is { } annotation)
            Collect(annotation, ValueForType(ResolveHirType(expected, caller)), fromExpected: true);

        var query = caller.QueryFork();
        foreach (var variable in variables)
        {
            if (!evidence.TryGetValue(variable.Type, out var supplied))
                throw Error(source, $"Cannot infer type parameter '{variable.Name}' from this call.", "E0202");
            result.Add(variable.Type, supplied.Type);
            query.Substitutions[variable.Type] = supplied.Type;
        }
        foreach (var variable in variables)
        {
            if (variable.Bound is not { } domain) continue;
            var supplied = evidence[variable.Type];
            HirTypeId constraint = ResolveHirType(domain, query);
            if (hir.Types[constraint.Value].Concept is not (>= 0 and var concept))
            {
                Require(supplied, constraint, source, context: query);
                continue;
            }
            if (supplied.BranchValue is { Arms.Length: > 1 })
                throw Error(source, $"Type parameter '{variable.Name}' has unresolved correlated Object alternatives.", "E0214");
            var associated = RequireConcept(supplied, concept, source, caller);
            foreach (var (projection, linked) in program.AssociatedProjections)
            {
                if (linked.Parameter != variable.Type || !associated.TryGetValue(linked.Associated, out var concrete)) continue;
                result.Add(hir.Type(new HirType(TypeRef.Any, TypeParameter: projection)), concrete);
            }
        }
        Trace?.Metric("inference.parameters", variables.Length);
        Trace?.Metric("inference.resolved", result.Count);
        return result;

        void Collect(HirTypeId syntax, SemanticValue actual, bool fromExpected)
        {
            var annotation = hir.Types[syntax.Value];
            if (annotation.Element >= 0 && (Runtime(actual).Kind & TypeKind.Range) != 0)
            {
                var supplied = actual.Element ?? ValueForType(hir.Type(Runtime(actual).Element ?? TypeRef.Any));
                Collect(new(annotation.Element), supplied, fromExpected);
            }
            if (!indexed.TryGetValue(annotation.TypeParameter, out var variable)
                || Runtime(actual).Kind == TypeKind.Never)
                return;
            if (annotation.Nullable)
            {
                if (Runtime(actual) == TypeRef.Null) return;
                actual = Retype(actual, Runtime(actual).WithoutNull);
            }
            if (evidence.TryGetValue(variable.Type, out var previous))
            {
                if (!fromExpected && previous.Type != actual.Type)
                    evidence[variable.Type] = Join(previous, actual);
            }
            else evidence.Add(variable.Type, actual);
        }
    }
}
