using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record SemanticMember(string Name, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null)
{
    public bool IsConstraint { get; init; }
}
public sealed record SemanticOccurrence(SourceSpan Span, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null);
public sealed record EditorAnalysis(IReadOnlyList<SemanticOccurrence> Occurrences, SourceDiagnostic? Diagnostic,
    IReadOnlyList<SemanticMember>? RecoveryBindings = null, int? RecoveryStart = null)
{
    internal EditorContextCollector? Contexts { get; init; }
    internal IReadOnlyList<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> AssociatedTypes { get; init; } = [];
    internal IReadOnlyList<(SourceSpan Span, EditorLocation Target)> AssociatedDefinitions { get; init; } = [];
    public IReadOnlyList<SourceSpan> RegexSpans { get; init; } = [];
    public IReadOnlyList<EditorSymbol> Symbols { get; init; } = [];
    public IReadOnlyList<EditorLocation> DefinitionsAt(int offset)
    {
        var associated = AssociatedDefinitions.Where(reference => reference.Span.Start <= offset && offset <= reference.Span.End)
            .Select(reference => reference.Target).Distinct().ToArray();
        return associated.Length == 0 ? Contexts?.DefinitionsAt(offset) ?? [] : associated;
    }
    public IReadOnlyList<SourceSpan> TypeSpans { get; init; } = [];
    public bool IsTypePosition(int offset)
    {
        foreach (SourceSpan span in TypeSpans)
            if (span.Start <= offset && offset <= span.End) return true;
        return false;
    }
    public EditorContext At(int offset)
    {
        EditorContext context = Contexts?.At(offset) ?? new(RecoveryBindings ?? [], null);
        return IsTypePosition(offset) ? context with
        {
            IsTypePosition = true,
            CanDeclareAssociatedType = AssociatedTypes.Any(scope => scope.Span.Start <= offset && offset <= scope.Span.End),
            Bindings = AssociatedTypes.Where(scope => scope.Span.Start <= offset && offset <= scope.Span.End)
                .OrderBy(scope => scope.Span.Length).SelectMany(scope => scope.Types)
                .Concat(Contexts?.ConceptsAt(offset) ?? [])
                .Concat(context.Bindings.Any(member => member.Name == "std") ? [] : EditorCompiler.StandardConceptBindings)
                .Concat(context.Bindings)
                .DistinctBy(member => member.Name).ToArray()
        } : context;
    }
    public SemanticMember? Resolve(int offset, string name) => Contexts is { } contexts ? contexts.Resolve(offset, name)
        : RecoveryBindings?.FirstOrDefault(binding => binding.Name == name);
}

public static class EditorCompiler
{
    private static readonly Lazy<IReadOnlyList<SemanticMember>> builtins = new(CreateBuiltinBindings);
    private static readonly Lazy<IReadOnlyList<SemanticMember>> standardConcepts = new(() =>
        new Compilation("", "<standard-concepts>", 0).StandardConceptDefinitions()
            .Select(pair => EditorContextCollector.DescribeConcept(pair.Value) with { Name = pair.Key })
            .ToArray());
    private static readonly Lazy<IReadOnlyDictionary<string, (NativeDeclaration Declaration, string Source)>> nativeDeclarations =
        new(Compilation.StandardNativeDeclarations);
    private static readonly Lazy<SemanticMember[]> processMembers = new(() => ProcessCapabilities.Members.Select(pair =>
        new SemanticMember(pair.Key, pair.Value.ToString(), $"{pair.Key}: {pair.Value}", pair.Key switch
        {
            "stdout" => "Captured standard output.",
            "stderr" => "Captured standard error.",
            "exit_code" => "The process exit status.",
            _ => "True when the process exit status is zero."
        })).ToArray());
    public static IReadOnlyList<SemanticMember> BuiltinBindings => builtins.Value;
    public static IReadOnlyList<SemanticMember> StandardConceptBindings => standardConcepts.Value;

    private static IReadOnlyList<SemanticMember> CreateBuiltinBindings()
    {
        Compilation compilation = new("", "<builtins>", 0);
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        return compilation.BuiltinInfos().Select(pair => DescribeMember(pair.Key, pair.Value, scopes, members: true)).ToArray();
    }

    internal static EditorContext DescribeContext(IReadOnlyList<System.Collections.Immutable.ImmutableDictionary<string, Binding>> environments,
        IReadOnlyList<Info> receivers, EditorReceiverKind kind = EditorReceiverKind.Member,
        ConnectionValue? physicalEndpoint = null, bool hasPhysicalOwner = false)
    {
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        SemanticMember[] bindings = environments.Count switch
        {
            0 => [],
            1 => DescribeBindings(environments[0], scopes),
            _ => environments[0].Keys.Where(name => !name.StartsWith('$') && environments.All(environment => environment.ContainsKey(name)))
                .Select(name => DescribeBindingChoices(name, environments.Select(environment => environment[name]), scopes)).ToArray()
        };
        SemanticMember? receiver = receivers.Count == 0 ? null : DescribeMember("", receivers, scopes, members: true);
        return new(bindings, receiver, kind, physicalEndpoint, hasPhysicalOwner);
    }

    internal static IReadOnlyList<SemanticMember> DescribeBindings(IReadOnlyDictionary<string, Binding> bindings)
    {
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        return DescribeBindings(bindings, scopes);
    }

    internal static SemanticMember? ResolveContext(IReadOnlyList<System.Collections.Immutable.ImmutableDictionary<string, Binding>> environments,
        IReadOnlyList<Info> receivers, EditorReceiverKind kind, string name)
    {
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        if (kind == EditorReceiverKind.Member && receivers.Count != 0)
        {
            if (receivers.Any(receiver => receiver.EditorUnknown)) return null;
            if (receivers.All(receiver => receiver.Type == TypeRef.Process))
                return processMembers.Value.FirstOrDefault(member => member.Name == name);
            var shapes = receivers.Where(receiver => receiver.Scope is not null).Select(receiver => receiver.Scope!).ToArray();
            return shapes.Length == 0 || shapes.Any(shape => !shape.Members.ContainsKey(name)) ? null
                : DescribeMember(name, shapes.Select(shape => shape.Members[name]), scopes, members: true);
        }
        if (name.StartsWith('$') || environments.Count == 0 || environments.Any(environment => !environment.ContainsKey(name))) return null;
        return environments.Count == 1 ? DescribeBinding(name, environments[0][name], scopes)
            : DescribeBindingChoices(name, environments.Select(environment => environment[name]), scopes);
    }

    private static SemanticMember[] DescribeBindings(IReadOnlyDictionary<string, Binding> bindings,
        Dictionary<ScopeInfo, ScopeDescription> scopes) => bindings.Where(pair => !pair.Key.StartsWith('$'))
        .Select(pair => DescribeBinding(pair.Key, pair.Value, scopes)).ToArray();

    private static SemanticMember DescribeBinding(string name, Binding binding, Dictionary<ScopeInfo, ScopeDescription> scopes)
    {
        SemanticMember member = DescribeMember(name, binding.Info, scopes, members: true);
        return binding.IsParameter && binding.Info.Type.WithoutNull == TypeRef.Scope
            ? member with { SemanticRole = "variable" } : member;
    }

    private static SemanticMember DescribeBindingChoices(string name, IEnumerable<Binding> bindings,
        Dictionary<ScopeInfo, ScopeDescription> scopes)
    {
        Binding[] choices = bindings.ToArray();
        SemanticMember member = DescribeMember(name, choices.Select(binding => binding.Info), scopes, members: true);
        return choices.All(binding => binding.IsParameter && binding.Info.Type.WithoutNull == TypeRef.Scope)
            ? member with { SemanticRole = "variable" } : member;
    }

    public static EditorAnalysis Analyze(string source, string sourceName) => Analyze(source, sourceName, null);

    internal static EditorAnalysis Analyze(string source, Compilation session, Func<int, Value?>? resolveValue = null) =>
        Analyze(source, session.EditorSourceName, session, resolveValue);

    private static EditorAnalysis Analyze(string source, string sourceName, Compilation? session, Func<int, Value?>? resolveValue = null)
    {
        Dictionary<SourceSpan, SemanticOccurrence> syntax = [];
        Dictionary<SourceSpan, Observation> inferred = [];
        SourceDiagnostic? diagnostic = null;
        IReadOnlyList<SemanticMember>? recovery = null;
        int? recoveryStart = null;
        Compilation? compilation = null;
        IReadOnlyList<SourceSpan> regexSpans = [];
        IReadOnlyList<EditorSymbol> symbols = [];
        List<SourceSpan> typeSpans = [];
        IReadOnlyList<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> associatedTypes = [];
        IReadOnlyList<(SourceSpan Span, EditorLocation Target)> associatedDefinitions = [];
        try
        {
            EditorParseResult parsed = Parser.ParseForEditor(source, sourceName);
            regexSpans = parsed.RegexSpans;
            Document document = parsed.Document;
            symbols = EditorSymbols.Collect(document.Statements);
            associatedTypes = ConceptTypeScopes(document.Statements).ToArray();
            associatedDefinitions = AssociatedTypeDefinitions(document.Statements, sourceName, source).ToArray();
            diagnostic = parsed.Error?.Diagnostic;
            Seed(document.Statements, source, syntax, typeSpans);
            Action<SourceSpan, Info, string?, bool> observer = (span, info, name, concrete) =>
            {
                if (!inferred.TryGetValue(span, out var observation)) inferred[span] = observation = new(name);
                observation.Add(info, concrete);
            };
            compilation = session is null ? new(source, sourceName, 0, observer) : session.ForkForEditor(source, observer, resolveValue);
            compilation.AnalyzeDocument(document);
            diagnostic ??= compilation.EditorDiagnostic;
            int incomplete = parsed.IncompleteStart ?? parsed.Error?.Span.Start ?? source.Length;
            if (parsed.Error is not null && (compilation.EditorDiagnostic is null
                || compilation.EditorDiagnostic.SourceName == sourceName && compilation.EditorDiagnostic.Span.Start >= incomplete))
            {
                recoveryStart = document.Statements.Where(statement => statement.Span.Start < incomplete)
                    .Select(statement => statement.Span.End).DefaultIfEmpty(0).Max();
                recovery = compilation.EditorContexts!.At(recoveryStart.Value).Bindings;
            }
        }
        catch (CompileException error) { diagnostic ??= error.Diagnostic; }
        catch (SyntaxException error) { diagnostic ??= error.Diagnostic; }
        Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions = [];
        foreach (var (span, observation) in inferred)
        {
            syntax[span] = observation.Multiple is { } alternatives
                ? Describe(span, observation.Name, alternatives.Values, scopeDescriptions)
                : Describe(span, observation.Name, observation.Single!, scopeDescriptions);
            if (syntax[span].SemanticRole == "type"
                && (observation.Single?.Type.WithoutNull == TypeRef.Scope
                    || observation.Multiple?.Values.All(item => item.Type.WithoutNull == TypeRef.Scope) == true)
                && compilation?.IsParameterOccurrence(span) == true)
                syntax[span] = syntax[span] with { SemanticRole = "variable" };
            if (observation.Single is { } single && compilation?.ConceptRequirementSignature(single) is { } requirement)
                syntax[span] = syntax[span] with
                {
                    Signature = requirement,
                    Description = "A required member of this structural concept. The concrete operation is checked at specialization."
                };
        }
        if (compilation?.EditorContexts is { } contexts)
            foreach (var (span, occurrence) in syntax.ToArray())
            {
                if (occurrence.Type != "Type" || occurrence.SemanticRole != "type"
                    || LanguageTypes.TryResolve(occurrence.Signature, out _)) continue;
                SemanticMember? concept = contexts.ConceptsAt(span.Start).Concat(StandardConceptBindings)
                    .FirstOrDefault(item => item.Name == occurrence.Signature);
                if (concept is not null) syntax[span] = occurrence with
                {
                    Signature = concept.Signature, Description = concept.Description, Members = concept.Members
                };
            }
        return new(syntax.Values.OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToArray(), diagnostic, recovery, recoveryStart)
        { Contexts = compilation?.EditorContexts, RegexSpans = regexSpans, TypeSpans = typeSpans, Symbols = symbols,
            AssociatedTypes = associatedTypes, AssociatedDefinitions = associatedDefinitions };
    }

    private static IEnumerable<(SourceSpan Span, EditorLocation Target)> AssociatedTypeDefinitions(IEnumerable<Node> nodes,
        string sourceName, string source)
    {
        foreach (Node node in nodes)
        {
            if (node is ConceptDeclaration concept)
            {
                var declarations = concept.Types.Where(type => type.NameSpan is { Length: > 0 }).DistinctBy(type => type.Name).ToDictionary(type => type.Name,
                    type => new EditorLocation(sourceName, source, type.NameSpan!.Value), StringComparer.Ordinal);
                foreach (TypeSyntax type in RequirementTypes(concept.Members))
                    if (declarations.TryGetValue(type.Name, out var target))
                        yield return (EditorTypeNames.Parts(source, type).Last().Span, target);
            }
            foreach (var reference in AssociatedTypeDefinitions(Compilation.Children(node), sourceName, source)) yield return reference;
        }

        static IEnumerable<TypeSyntax> RequirementTypes(IEnumerable<Node> requirements)
        {
            foreach (Node node in requirements)
            {
                foreach (TypeSyntax type in TypeSyntaxFacts.Annotations(node))
                    foreach (TypeSyntax nested in TypeTree(type)) yield return nested;
            }
        }

        static IEnumerable<TypeSyntax> TypeTree(TypeSyntax type)
        {
            yield return type;
            foreach (TypeSyntax child in type.Arguments.Concat(type.Alternatives))
                foreach (TypeSyntax nested in TypeTree(child)) yield return nested;
        }
    }

    private static IEnumerable<(SourceSpan Span, IReadOnlyList<SemanticMember> Types)> ConceptTypeScopes(IEnumerable<Node> nodes)
    {
        foreach (Node node in nodes)
        {
            if (node is ConceptDeclaration concept)
                yield return (concept.Span, concept.Types.Where(type => type.NameSpan is { Length: > 0 })
                    .Select(type => new SemanticMember(type.Name, "Type", $"typename {type.Name}",
                    "An associated type selected from the constrained Scope.", SemanticRole: "type") { IsConstraint = true }).ToArray());
            foreach (var scope in ConceptTypeScopes(Compilation.Children(node))) yield return scope;
        }
    }

    private sealed class Observation(string? name)
    {
        public string? Name { get; } = name;
        private bool concrete;
        public Info? Single { get; private set; }
        public Dictionary<(TypeRef, int?, string?, ScopeInfo?, TypeRef?, ConnectionValue?, PathValue?, string?), Info>? Multiple { get; private set; }

        public void Add(Info info, bool isConcrete)
        {
            if (!isConcrete && concrete) return;
            if (isConcrete && !concrete) { Single = null; Multiple = null; }
            concrete |= isConcrete;
            if (Multiple is { } alternatives) { alternatives[Key(info)] = info; return; }
            if (Single is null || Key(Single) == Key(info)) { Single = info; return; }
            Multiple = new() { [Key(Single)] = Single, [Key(info)] = info };
            Single = null;
        }

        private static (TypeRef, int?, string?, ScopeInfo?, TypeRef?, ConnectionValue?, PathValue?, string?) Key(Info info) =>
            (info.Type, info.Function?.Id, info.NativeName, info.Scope, info.TypeValue, info.KnownConnection, info.KnownPath, info.KnownText);
    }

    private const string ScopeExplanation = "A lexical Scope. Select a declared member with `::`; a Scope is not a physical endpoint.";
    private sealed record ScopeDescription(SemanticMember[] Members, string Text)
    {
        public string Description { get; } = ScopeExplanation + Text;
    }

    private static SemanticOccurrence Describe(SourceSpan span, string? name, Info info,
        Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = true)
    {
        string type = info.Type.ToString();
        string signature = Signature(name, info);
        string description = Description(info.Type, info.EditorUnknown);
        SemanticMember[]? contents = members && !info.EditorUnknown && info.Type == TypeRef.Process ? processMembers.Value : null;
        if (members && !info.EditorUnknown && info.Scope is { } shape)
        {
            if (!scopeDescriptions.TryGetValue(shape, out var summary))
            {
                var items = shape.Members.Select(pair => DescribeMember(pair.Key, pair.Value, scopeDescriptions, members: true)).ToArray();
                scopeDescriptions[shape] = summary = new(items, MemberList(items));
            }
            contents = summary.Members;
            description = info.Type == TypeRef.Scope ? summary.Description : description + summary.Text;
        }
        if (!info.EditorUnknown && info.NativeName is { } native) description += NativeDocumentation(native);
        string role = info.EditorUnknown ? "variable" : info.Type.WithoutNull == TypeRef.Function ? "function"
            : info.Type == TypeRef.Type || info.Type.WithoutNull == TypeRef.Scope && info.Scope?.Named == true ? "type" : "variable";
        return new(span, info.EditorUnknown ? "unknown" : type, signature, description, contents,
            info.KnownConnection, role, info.EditorUnknown ? null : info.KnownPath, info.EditorUnknown ? null : info.KnownText);
    }

    private static SemanticOccurrence Describe(SourceSpan span, string? name, IEnumerable<Info> values,
        Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = true)
    {
        Info[] choices = values.ToArray();
        if (choices.Length == 1) return Describe(span, name, choices[0], scopeDescriptions, members);
        bool unknown = choices.Any(value => value.EditorUnknown);
        TypeRef type = choices.Select(value => value.Type).Aggregate(TypeRef.Union);
        string signature = string.Join("\n", choices.Select(value => Signature(name, value)).Distinct(StringComparer.Ordinal));
        string description = Description(type, unknown: false);
        var native = choices.Select(value => value.NativeName).FirstOrDefault(value => value is not null);
        if (native is not null) description += NativeDocumentation(native);
        SemanticMember[]? contents = null;
        if (members && !unknown && type == TypeRef.Process) contents = processMembers.Value;
        if (members && !unknown && choices.Any(value => value.Scope is not null))
        {
            ScopeInfo[] scopes = choices.Where(value => value.Scope is not null).Select(value => value.Scope!).ToArray();
            if (scopes.Length == 1)
            {
                if (!scopeDescriptions.TryGetValue(scopes[0], out var summary))
                {
                    var items = scopes[0].Members.Select(pair => DescribeMember(pair.Key, pair.Value, scopeDescriptions, members: true)).ToArray();
                    scopeDescriptions[scopes[0]] = summary = new(items, MemberList(items));
                }
                contents = summary.Members;
                description = type == TypeRef.Scope ? summary.Description : description + summary.Text;
            }
            else
            {
                contents = scopes.SelectMany(scope => scope.Members).GroupBy(member => member.Key, StringComparer.Ordinal)
                    .Where(group => scopes.All(scope => scope.Members.ContainsKey(group.Key)))
                    .Select(group => DescribeMember(group.Key, group.Select(pair => pair.Value), scopeDescriptions, members: true)).ToArray();
                description += MemberList(contents);
            }
        }
        var endpoint = choices[0].KnownConnection;
        if (endpoint is not null && choices.Any(info => info.KnownConnection != endpoint)) endpoint = null;
        var path = choices[0].KnownPath;
        if (path is not null && choices.Any(info => info.KnownPath != path)) path = null;
        var text = choices[0].KnownText;
        if (text is not null && choices.Any(info => info.KnownText != text)) text = null;
        if (unknown) description = "The type depends on an incomplete expression or an uninstantiated generic parameter.";
        string role = unknown ? "variable" : type.WithoutNull == TypeRef.Function ? "function"
            : type == TypeRef.Type || type.WithoutNull == TypeRef.Scope && choices.All(info => info.Scope?.Named == true) ? "type" : "variable";
        return new(span, unknown ? "unknown" : type.ToString(), signature, description, contents, endpoint, role,
            unknown ? null : path, unknown ? null : text);
    }

    private static string Description(TypeRef type, bool unknown)
    {
        if (unknown) return "The type depends on an incomplete expression or an uninstantiated generic parameter.";
        TypeRef core = type.WithoutNull;
        string description = core == TypeRef.Path ? "A filesystem path. Bare unbound names denote Paths. Calling a Path starts a Process."
            : core == TypeRef.Scope ? ScopeExplanation
            : core == TypeRef.Connection ? "A physical endpoint. Select it with `path @ connection`; `::` selects lexical Scope members."
            : core == TypeRef.Function ? "A callable value. Unannotated parameters are specialized from each call's argument types."
            : type == TypeRef.Type ? "A type value. `decltype(expression)` infers without executing the expression."
            : core == TypeRef.Bool ? "A boolean value: `true` or `false`. Conditions require Bool; Null is not a boolean."
            : core == TypeRef.String ? "A text value. Transferring a String writes its bytes; a Path reads a file."
            : core == TypeRef.Process ? "A process value. Inspect `stdout`, `stderr`, `exit_code` or `success` with `::`."
            : type == TypeRef.Null ? "The absence of a value. Test with `is null` or `is not null`; postfix `?` means `is null`."
            : type == TypeRef.Number ? "An IEEE 754 binary64 number. Arithmetic includes `+`, `-`, `*`, `/`, `%` and `**`; only explicit `as` converts other scalar types."
            : $"Inferred type: `{type}`. Inspection does not execute this expression.";
        return type.Nullable && type != TypeRef.Null
            ? description + " Handle Null before using the value; postfix `?` tests whether it is Null." : description;
    }

    private static SemanticMember DescribeMember(string name, Info choice, Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = false)
    {
        var item = Describe(default, name, choice, scopeDescriptions, members);
        return new(name, item.Type, item.Signature, item.Description, item.Members, item.Endpoint, item.SemanticRole, item.KnownPath, item.KnownText);
    }

    private static SemanticMember DescribeMember(string name, IEnumerable<Info> choices, Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = false)
    {
        var item = Describe(default, name, choices, scopeDescriptions, members);
        return new(name, item.Type, item.Signature, item.Description, item.Members, item.Endpoint, item.SemanticRole, item.KnownPath, item.KnownText);
    }

    private static string MemberList(IEnumerable<SemanticMember> items) =>
        "\n\nMembers:\n" + string.Join("\n", items.Select(member => $"- `{member.Name}: {member.Type}`"));

    private static string NativeDocumentation(string name)
    {
        string description = $"\n\nNative standard-library function `{name}`.";
        if (!nativeDeclarations.Value.TryGetValue(name, out var declaration)) return description;
        string documentation = EditorContextCollector.DocumentationAt(declaration.Source, declaration.Declaration.Span.Start);
        return documentation.Length == 0 ? description : description + "\n\n" + documentation;
    }

    private static string Signature(string? name, Info info)
    {
        if (info.EditorUnknown) return name is null ? "unknown" : $"{name}: unknown";
        if (info.Alternatives is { } alternatives)
            return string.Join("\n", alternatives.Select(alternative => Signature(name, alternative)).Distinct(StringComparer.Ordinal));
        if (info.Function is { } template)
        {
            var instances = template.Specializations.Values.Where(item => item.Result.Type.Kind != TypeKind.Never).ToArray();
            string result = template.Syntax.ReturnType is { } written ? Slice(template.Source, written.Span)
                : instances.Length == 0 ? "inferred at call" : string.Join(" | ", instances.Select(item => item.Result.Type.ToString()).Distinct());
            return FunctionSignature(name ?? template.Syntax.Name, template.Syntax.Parameters, result, template.Source,
                template.Specializations.Values.Select(instance => instance.ParameterTypes).ToArray());
        }
        if (info.NativeName is { } native)
        {
            var registered = NativeSignatures.Get(native);
            if (nativeDeclarations.Value.TryGetValue(native, out var declaration))
                return FunctionSignature(name ?? declaration.Declaration.Name, declaration.Declaration.Parameters,
                    Slice(declaration.Source, declaration.Declaration.ReturnType.Span), declaration.Source,
                    variadicMinimum: registered.Variadic ? registered.Parameters.Count : null);
            return $"fn {name ?? native}({string.Join(", ", registered.Parameters)}) -> {registered.Result}";
        }
        string type = info.TypeValue is { } inferred ? $"Type ({inferred})" : info.Type.ToString();
        return name is null ? type : $"{name}: {type}";
    }

    private static string FunctionSignature(string? name, IReadOnlyList<Parameter> parameters, string result, string source,
        IReadOnlyList<IReadOnlyList<TypeRef>>? specializations = null, int? variadicMinimum = null) =>
        $"fn {name ?? ""}({string.Join(", ", parameters.Select((parameter, index) =>
            $"{(index >= variadicMinimum ? "..." : "")}{parameter.Name}{(index >= variadicMinimum ? "" : parameter.Nullable ? "?" : "")}: {(parameter.Type is not null ? Slice(source, parameter.Type.Span)
                : specializations is null || specializations.Count == 0 ? "inferred at call"
                    : string.Join(" | ", specializations.Select(instance => instance[index].ToString()).Distinct()))}"))}) -> {result}";

    private static string Slice(string source, SourceSpan span) => source[span.Start..span.End];

    private static void Seed(IEnumerable<Node> nodes, string source, Dictionary<SourceSpan, SemanticOccurrence> occurrences, List<SourceSpan> typeSpans)
    {
        foreach (Node node in nodes)
        {
            if (node is FunctionExpr functionType)
            {
                foreach (Parameter parameter in functionType.Parameters) AddType(parameter.Type);
                AddType(functionType.ReturnType);
            }
            if (node is LetStatement letType) AddType(letType.Type);
            if (node is NativeDeclaration nativeType)
            {
                foreach (Parameter parameter in nativeType.Parameters) AddType(parameter.Type);
                AddType(nativeType.ReturnType);
            }
            if (node is TypeExpr valueType) AddType(valueType.Type);
            if (node is CastExpr cast) AddType(cast.Type);
            if (node is ConceptDeclaration conceptType)
                foreach (TypeSyntax inherited in conceptType.Bases) AddType(inherited);
            if (node is ConceptMember requirement)
            {
                AddType(requirement.Type);
                AddType(requirement.ReturnType);
                if (requirement.Parameters is { } parameters)
                    foreach (Parameter parameter in parameters) AddType(parameter.Type);
            }
            switch (node)
            {
                case FunctionDeclaration function:
                    Add(function.Function.NameSpan, "Function", FunctionSignature(function.Name, function.Function.Parameters,
                        function.Function.ReturnType is { } result ? Slice(source, result.Span) : "inferred at call", source));
                    break;
                case NativeDeclaration native:
                    Add(native.NameSpan, "Function", FunctionSignature(native.Name, native.Parameters, Slice(source, native.ReturnType.Span), source));
                    break;
                case ScopeDeclaration scope: Add(scope.NameSpan, "Scope", $"{scope.Name}: Scope", "type"); break;
                case ScopeExpr { Name: { } name } scope: Add(scope.NameSpan, "Scope", $"scope {name}", "type"); break;
                case ConceptDeclaration concept:
                    Add(concept.NameSpan, "Concept", $"concept {concept.Name}", "type");
                    if (concept.NameSpan is { } conceptName)
                        occurrences[conceptName] = occurrences[conceptName] with
                        {
                            Description = "A named structural constraint. Required fields and callable signatures are checked at specialization."
                                + (concept.Members.Count == 0 ? "" : "\n\nRequired members:\n"
                                    + string.Join("\n", concept.Members.Select(member => $"- `{Slice(source, member.Span)}`")))
                        };
                    Seed(concept.Types, source, occurrences, typeSpans);
                    Seed(concept.Members, source, occurrences, typeSpans);
                    break;
                case ConceptAssociatedType associated:
                    Add(associated.NameSpan, "Type", $"typename {associated.Name}", "type");
                    break;
                case ConceptMember { Parameters: { } parameters } member:
                    Add(member.NameSpan, "Function", FunctionSignature(member.Name, parameters,
                        member.ReturnType is { } expected ? Slice(source, expected.Span) : "inferred at call", source));
                    break;
                case ConceptMember member:
                    Add(member.NameSpan, member.Type is { } required ? Slice(source, required.Span) : "unknown",
                        Slice(source, member.Span));
                    break;
                case FunctionExpr { Name: { } name } function:
                    Add(function.NameSpan, "Function", FunctionSignature(name, function.Parameters,
                        function.ReturnType is { } namedResult ? Slice(source, namedResult.Span) : "inferred at call", source));
                    break;
                case LetStatement { Pattern: NamePattern name, Value: FunctionExpr function }:
                    Add(name.Span, "Function", FunctionSignature(name.Name, function.Parameters,
                        function.ReturnType is { } lambdaResult ? Slice(source, lambdaResult.Span) : "inferred at call", source));
                    break;
                case LetStatement { Pattern: NamePattern name, Value: ScopeExpr }: Add(name.Span, "Scope", $"{name.Name}: Scope"); break;
                case ScopeMember { Value: FunctionExpr function } member:
                    Add(member.NameSpan, "Function", FunctionSignature(member.Name, function.Parameters,
                        function.ReturnType is { } memberResult ? Slice(source, memberResult.Span) : "inferred at call", source));
                    break;
                case ScopeMember { Value: ScopeExpr } member: Add(member.NameSpan, "Scope", $"{member.Name}: Scope"); break;
            }
            Seed(Compilation.Children(node), source, occurrences, typeSpans);
        }
        void AddType(TypeSyntax? type)
        {
            if (type is null) return;
            int start = type.Span.Start;
            while (start > 0 && char.IsWhiteSpace(source[start - 1])) start--;
            typeSpans.Add(new(start, type.Span.End - start));
            string prefix = "";
            foreach (var (part, name) in EditorTypeNames.Parts(source, type))
            {
                prefix += prefix.Length == 0 ? part : "::" + part;
                occurrences[name] = new(name, "Type", type.DeclaresAssociatedType ? $"typename {type.Name}" : prefix,
                    type.DeclaresAssociatedType ? "An associated type declared inline and inferred from the constrained value."
                        : prefix == type.Name ? "A type or structural concept constraint. It is checked without executing the expression."
                        : "A compile-time standard-library namespace. Select a concept with `::`.", SemanticRole: "type");
            }
            foreach (TypeSyntax argument in type.Arguments) AddType(argument);
            foreach (TypeSyntax alternative in type.Alternatives) AddType(alternative);
        }
        void Add(SourceSpan? span, string type, string signature, string? role = null)
        {
            if (span is { } location) occurrences[location] = new(location, type, signature,
                type == "Concept" ? "A named structural constraint. Required fields and function signatures are checked at specialization."
                    : type == "Type" ? "An associated type selected from the constrained Scope."
                    : type == "Function" ? "A callable value. Its generic body is checked when specialized by a call."
                    : "A lexical Scope. Select its declared members with `::`.",
                SemanticRole: role ?? (type == "Function" ? "function" : "variable"));
        }
    }
}

internal sealed partial class Compilation
{
    internal static IReadOnlyDictionary<string, (NativeDeclaration Declaration, string Source)> StandardNativeDeclarations() =>
        libraries.Value.SelectMany(library => library.Value.Document.Statements.OfType<NativeDeclaration>()
            .Select(declaration => new KeyValuePair<string, (NativeDeclaration, string)>(library.Key + "." + declaration.Name,
                (declaration, library.Value.Source)))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private readonly Dictionary<Info, string>? editorConceptRequirements = observe is null ? null : new(ReferenceEqualityComparer.Instance);

    private void DescribeConceptRequirement(Info info, ConceptDefinition definition, ConceptMember requirement)
    {
        if (editorConceptRequirements is not null)
            editorConceptRequirements[info] = definition.Source[requirement.Span.Start..requirement.Span.End];
    }

    internal string? ConceptRequirementSignature(Info info) => editorConceptRequirements?.GetValueOrDefault(info);

    private void Observe(Frame frame, SourceSpan span, Info info, string? name = null)
    {
        if (observe is null || !IsCurrentSource(frame)) return;
        observe(span, info, name, frame.EditorReportErrors);
    }
}
