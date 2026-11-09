using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Dictionary<string, HashSet<Statement>>? standardDemand;

    private void PrepareStandardDemand(Document document)
    {
        standardDemand = null;
        // Persistent sessions can request new exports in a later submission.
        if (submittedBindings is not null || editorContexts is not null || !source.Contains('$')
            || !document.Statements.Any(HasStandardReference)) return;
        standardDemand = new(StringComparer.Ordinal);
        foreach (var statement in document.Statements) DemandStandardReferences(statement);
    }

    private void DemandStandardReferences(Node node)
    {
        if (StandardPath(node, out string? module, out string? export))
        {
            if (module is null)
            {
                foreach (string name in StandardNames()) DemandStandardExport(name, null);
            }
            else DemandStandardExport(module, export);
            return;
        }
        foreach (var child in Children(node)) DemandStandardReferences(child);
    }

    private static bool StandardPath(Node node, out string? module, out string? export)
    {
        module = export = null;
        while (node is BinaryExpr { Operator: "::", Right: NameExpr member } selection)
        {
            export = module;
            module = member.Name;
            node = selection.Left;
        }
        return node is UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } };
    }

    private void DemandStandardExport(string name, string? export)
    {
        if (!libraries.Value.TryGetValue(name, out var library)) return;
        var declarations = library.Document.Statements;
        if (!standardDemand!.TryGetValue(name, out var selected))
        {
            selected = new(ReferenceEqualityComparer.Instance);
            standardDemand.Add(name, selected);
            for (int index = 0; index < declarations.Count; index++)
                if (!DeferredDeclaration(declarations[index])) Include(index);
        }
        if (export is null)
        {
            for (int index = 0; index < declarations.Count; index++) Include(index);
            return;
        }
        for (int index = declarations.Count - 1; index >= 0; index--)
        {
            if (StandardExportName(declarations[index]) != export) continue;
            Include(index);
            return;
        }

        void Include(int index)
        {
            var declaration = declarations[index];
            if (!selected.Add(declaration)) return;
            var names = Names(declaration).ToHashSet(StringComparer.Ordinal);
            if (declaration is FunctionDeclaration or ScopeDeclaration)
                names.Remove(StandardExportName(declaration)!);
            for (int prior = index - 1; prior >= 0; prior--)
            {
                string? dependency = StandardExportName(declarations[prior]);
                if (dependency is not null && names.Remove(dependency)) Include(prior);
            }
            DemandStandardReferences(declaration);
        }
    }

    private IEnumerable<Statement> StandardDeclarations(string name, Document document) =>
        standardDemand is not null && standardDemand.TryGetValue(name, out var selected)
            ? document.Statements.Where(selected.Contains) : document.Statements;

    private static string? StandardExportName(Statement statement) => statement switch
    {
        NativeDeclaration native => native.Name,
        FunctionDeclaration function => function.Name,
        ScopeDeclaration scope => scope.Name,
        LetStatement { Pattern: NamePattern name } => name.Name,
        _ => null
    };

    private static bool DeferredDeclaration(Statement statement) => statement switch
    {
        ConceptDeclaration or NativeDeclaration or FunctionDeclaration => true,
        ScopeDeclaration scope => PureConstruction(scope.Scope),
        LetStatement let => PureConstruction(let.Value),
        _ => false
    };

    private static bool PureConstruction(Node node) => node switch
    {
        FunctionExpr or NameExpr or StringExpr or PathExpr or BoolExpr or NullExpr or NumberExpr or TypeExpr => true,
        ScopeExpr scope => scope.Members.All(member => PureConstruction(member.Value))
            && (scope.Value is null || PureConstruction(scope.Value)),
        RangeExpr range => range.Items.All(PureConstruction),
        UnaryExpr unary when unary.Operator is "+" or "-" or "not" or "?" => PureConstruction(unary.Operand),
        BinaryExpr binary when binary.Operator is not ("->" or "|") => PureConstruction(binary.Left) && PureConstruction(binary.Right),
        _ => false
    };
}
