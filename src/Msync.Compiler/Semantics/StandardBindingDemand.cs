using Msync.Compiler.Syntax;
using static Msync.Compiler.Syntax.SyntaxTraversal;

namespace Msync.Compiler.Semantics;

// This is a source dependency plan, not a cache of bound scopes or caller facts.
internal sealed class StandardBindingDemand
{
    private readonly IReadOnlyDictionary<string, StandardSource> libraries;
    private readonly Dictionary<string, HashSet<Statement>> selected = new(StringComparer.Ordinal);
    private readonly CancellationToken cancellationToken;

    public StandardBindingDemand(Document document, IReadOnlyDictionary<string, StandardSource> libraries,
        CancellationToken cancellationToken = default)
    {
        this.libraries = libraries;
        this.cancellationToken = cancellationToken;
        foreach (var statement in document.Statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            References(statement);
        }
    }

    public IEnumerable<Statement> Declarations(string module, Document document) =>
        selected.TryGetValue(module, out var declarations) ? document.Statements.Where(declarations.Contains)
            : document.Statements;

    private void References(Node node)
    {
        if (StandardPath(node, out var module, out var export))
        {
            if (module is null)
            {
                foreach (string name in libraries.Keys) Demand(name, null);
            }
            else Demand(module, export);
            return;
        }
        foreach (var child in Children(node)) References(child);
    }

    private void Demand(string name, string? export)
    {
        if (!libraries.TryGetValue(name, out var module)) return;
        var declarations = module.Document.Statements;
        if (!selected.TryGetValue(name, out var included))
        {
            included = new(ReferenceEqualityComparer.Instance);
            selected.Add(name, included);
            for (int index = 0; index < declarations.Count; index++)
                if (!Deferred(declarations[index])) Include(index);
        }
        if (export is not null)
            for (int index = declarations.Count - 1; index >= 0; index--)
                if (Name(declarations[index]) == export) { Include(index); return; }

        // An escaped namespace needs its full value. A missing export needs the
        // same names for useful diagnostics, including while the user types it.
        for (int index = 0; index < declarations.Count; index++) Include(index);

        void Include(int index)
        {
            var declaration = declarations[index];
            if (!included.Add(declaration)) return;
            cancellationToken.ThrowIfCancellationRequested();
            var names = Names(declaration).ToHashSet(StringComparer.Ordinal);
            if (declaration is FunctionDeclaration or ScopeDeclaration) names.Remove(Name(declaration)!);
            for (int prior = index - 1; prior >= 0; prior--)
                if (Name(declarations[prior]) is { } dependency && names.Remove(dependency)) Include(prior);
            References(declaration);
        }
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

    private static string? Name(Statement statement) => statement switch
    {
        NativeDeclaration native => native.Name,
        FunctionDeclaration function => function.Name,
        ScopeDeclaration scope => scope.Name,
        LetStatement { Pattern: NamePattern name } => name.Name,
        _ => null
    };

    private static bool Deferred(Statement statement) => statement switch
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
