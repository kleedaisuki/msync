using System.Collections.Concurrent;
using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal static class SemanticStandardCatalog
{
    private static readonly Lazy<IReadOnlyList<SemanticMember>> builtins = new(CreateBuiltins);
    private static readonly Lazy<IReadOnlyList<SemanticMember>> concepts = new(CreateConcepts);
    private static readonly ConcurrentDictionary<string, NativePresentation> native = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IReadOnlyList<SemanticMember>> modules = new(StringComparer.Ordinal);

    public static IReadOnlyList<SemanticMember> Builtins => builtins.Value;
    public static IReadOnlyList<SemanticMember> Concepts => concepts.Value;

    private static IReadOnlyList<SemanticMember> CreateBuiltins()
    {
        SemanticMember[] modules = StandardSourceCatalog.Read().Keys.Where(name => !name.StartsWith('_'))
            .Order(StringComparer.Ordinal).Select(name => new SemanticMember(name, "Scope", $"scope {name}",
                "A source-backed standard-library namespace.", SemanticRole: "type")).ToArray();
        List<SemanticMember> result =
        [
            new("$std", "Scope", "scope std", "Source-backed standard-library namespaces.", modules, SemanticRole: "type")
        ];
        foreach (string name in new[] { "local", "wsl", "pwd", "file", "env", "path" })
        {
            ConnectionKind kind = name switch
            {
                "wsl" => ConnectionKind.Wsl,
                "env" => ConnectionKind.Environment,
                "path" => ConnectionKind.SearchPath,
                _ => ConnectionKind.Local
            };
            string address = name is "pwd" or "file" ? name : "";
            result.Add(new("$" + name, "Connection", $"${name}: Connection",
                "A physical endpoint. Qualify a path with `connection @ path`.",
                Endpoint: new(kind, address)));
        }
        return result.ToArray();
    }

    public static NativePresentation? Native(string name)
    {
        if (native.TryGetValue(name, out var cached)) return cached;
        int separator = name.IndexOf('.');
        if (separator < 0 || !StandardSourceCatalog.Read().TryGetValue(name[..separator], out var module)) return null;
        NativeDeclaration? declaration = module.Document.Statements.OfType<NativeDeclaration>()
            .FirstOrDefault(item => item.Name == name[(separator + 1)..]);
        if (declaration is null) return null;
        return DescribeNative(name, module, declaration);
    }

    private static NativePresentation DescribeNative(string name, StandardSource module, NativeDeclaration declaration)
    {
        NativeSignature signature = NativeSignatures.Get(name);
        string result = module.Source[declaration.ReturnType.Span.Start..declaration.ReturnType.Span.End];
        string formatted = $"fn {declaration.Name}({string.Join(", ", declaration.Parameters.Select((parameter, index) =>
            $"{(signature.Variadic && index >= signature.Parameters.Count ? "..." : "")}{parameter.Name}{(parameter.Nullable ? "?" : "")}: {(parameter.Type is null ? "inferred at call" : module.Source[parameter.Type.Span.Start..parameter.Type.Span.End])}"))}) -> {result}";
        string documentation = Documentation(module.Source, declaration.Span.Start);
        var found = new NativePresentation(formatted, $"Native standard-library function `{name}`."
            + (documentation.Length == 0 ? "" : "\n\n" + documentation));
        return native.GetOrAdd(name, found);
    }

    // Closed-document inference can omit unused exports. The missing completion
    // labels come from the embedded declarations, not a second semantic run.
    internal static IReadOnlyList<SemanticMember> ModuleMembers(string module) =>
        modules.GetOrAdd(module, CreateModuleMembers);

    private static IReadOnlyList<SemanticMember> CreateModuleMembers(string module)
    {
        StandardSource library = StandardSourceCatalog.Read()[module];
        Dictionary<string, SemanticMember> members = new(StringComparer.Ordinal);
        foreach (Statement statement in library.Document.Statements)
            if (Member(statement) is { } member) members[member.Name] = member;
        return members.Values.ToArray();

        SemanticMember? Member(Statement statement)
        {
            switch (statement)
            {
                case NativeDeclaration declaration:
                    NativePresentation presentation = DescribeNative(module + "." + declaration.Name, library, declaration);
                    return new(declaration.Name, "Function", presentation.Signature, presentation.Description, SemanticRole: "function");
                case FunctionDeclaration function:
                    string parameters = string.Join(", ", function.Function.Parameters.Select(parameter =>
                        parameter.Name + (parameter.Nullable ? "?" : "") + ": " + Type(parameter.Type, "inferred at call")));
                    string result = Type(function.Function.ReturnType, "inferred at call");
                    return new(function.Name, "Function", $"fn {function.Name}({parameters}) -> {result}",
                        Help(statement, "function"), SemanticRole: "function");
                case ScopeDeclaration scope:
                    return new(scope.Name, "Scope", $"scope {scope.Name}", Help(statement, "scope"), SemanticRole: "type");
                case LetStatement { Pattern: NamePattern name } binding:
                    string type = Type(binding.Type, binding.Value switch
                    {
                        NumberExpr => "Number", StringExpr => "String", BoolExpr => "Bool", PathExpr => "Path",
                        NullExpr => "Null", _ => "inferred from initializer"
                    });
                    return new(name.Name, type, $"{name.Name}: {type}", Help(statement, "binding"));
                default:
                    return null;
            }
        }

        string Type(TypeSyntax? type, string fallback) => type is null ? fallback
            : library.Source[type.Span.Start..type.Span.End];

        string Help(Statement statement, string kind)
        {
            string documentation = Documentation(library.Source, statement.Span.Start);
            return documentation.Length == 0 ? $"Standard-library {kind} in `$std::{module}`." : documentation;
        }
    }

    private static IReadOnlyList<SemanticMember> CreateConcepts()
    {
        if (!StandardSourceCatalog.Read().TryGetValue("concepts", out var module)) return [];
        return module.Document.Statements.OfType<ConceptDeclaration>().Select(concept =>
        {
            SemanticMember[] members = concept.Members.Select(member => new SemanticMember(member.Name,
                member.Parameters is null && member.Type is { } type
                    ? module.Source[type.Span.Start..type.Span.End] : "Function",
                module.Source[member.Span.Start..member.Span.End], "A required member of this structural concept.",
                SemanticRole: member.Parameters is null ? "variable" : "function")).ToArray();
            string description = Documentation(module.Source, concept.Span.Start);
            description += (description.Length == 0 ? "" : "\n\n")
                + "A named structural constraint. Required fields and callable signatures are checked at specialization.";
            if (concept.Bases.Count != 0) description += "\n\nIncludes: " + string.Join(", ", concept.Bases.Select(item => $"`{item.Name}`")) + ".";
            if (members.Length != 0) description += "\n\nRequired members:\n"
                + string.Join('\n', members.Select(item => $"- `{item.Signature}`"));
            return new SemanticMember("$std::concepts::" + concept.Name, "Type", $"concept {concept.Name}",
                description, members, SemanticRole: "type")
            { IsConstraint = true };
        }).ToArray();
    }

    internal static string Documentation(string source, int position)
    {
        int end = position;
        while (end > 0 && source[end - 1] is ' ' or '\t') end--;
        if (end > 0 && source[end - 1] == '\n') end--;
        if (end > 0 && source[end - 1] == '\r') end--;
        List<string> lines = [];
        while (end > 0)
        {
            int start = source.LastIndexOf('\n', end - 1) + 1;
            ReadOnlySpan<char> line = source.AsSpan(start, end - start).Trim();
            if (!line.StartsWith("#", StringComparison.Ordinal)) break;
            lines.Add(line[1..].TrimStart().ToString());
            end = start > 0 ? start - 1 : 0;
            if (end > 0 && source[end - 1] == '\r') end--;
        }
        lines.Reverse();
        return string.Join('\n', lines);
    }
}
