using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class SemanticObservationBuilder
{
    internal static SemanticObservationView DescribeSession(BoundProgram program, SemanticContext context)
    {
        SemanticObservationBuilder projection = new(program);
        var bindings = ImmutableDictionary.CreateBuilder<string, SemanticBindingView>(StringComparer.Ordinal);
        foreach (var (name, original) in program.Exports)
        {
            HirLocationId location = context.Location(original);
            SemanticValue value = context.Values.GetValueOrDefault(location) ?? projection.BoundValue(original);
            bindings.Add(name, new(name, location, projection.Value(value, context), projection.Parameter(original)));
        }
        SemanticTypeTables tables = SemanticTypeTables.From(program.Hir) with
        {
            ConceptNames = projection.ConceptNames(),
            AssociatedTypes = projection.AssociatedTypes()
        };
        // A prompt needs the accepted lexical roots, not another source analysis
        // or navigation observations for every historical/private declaration.
        SemanticVisibility visible = new(new SourceSpan(0, 0), true, 0,
            bindings.ToImmutable(), projection.Concepts(program.RootConcepts));
        return new(tables, projection.source, projection.values.ToImmutableArray(), [], [visible], [], [], [], []);
    }
}
