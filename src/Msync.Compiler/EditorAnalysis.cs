using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public sealed record SemanticMember(string Name, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null);
public sealed record SemanticOccurrence(SourceSpan Span, string Type, string Signature, string Description,
    IReadOnlyList<SemanticMember>? Members = null, ConnectionValue? Endpoint = null, string SemanticRole = "variable",
    PathValue? KnownPath = null, string? KnownText = null);
public sealed record EditorAnalysis(IReadOnlyList<SemanticOccurrence> Occurrences, SourceDiagnostic? Diagnostic,
    IReadOnlyList<SemanticMember>? RecoveryBindings = null, int? RecoveryStart = null)
{
    internal EditorContextCollector? Contexts { get; init; }
    public IReadOnlyList<SourceSpan> RegexSpans { get; init; } = [];
    public EditorContext At(int offset) => Contexts?.At(offset) ?? new(RecoveryBindings ?? [], null);
}

public static class EditorCompiler
{
    private static readonly Lazy<IReadOnlyList<SemanticMember>> builtins = new(CreateBuiltinBindings);
    public static IReadOnlyList<SemanticMember> BuiltinBindings => builtins.Value;

    private static IReadOnlyList<SemanticMember> CreateBuiltinBindings()
    {
        Compilation compilation = new("", "<builtins>", 0);
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        return compilation.BuiltinInfos().Select(pair => DescribeMember(pair.Key, [pair.Value], scopes, members: true)).ToArray();
    }

    internal static EditorContext DescribeContext(IReadOnlyList<System.Collections.Immutable.ImmutableDictionary<string, Binding>> environments,
        IReadOnlyList<Info> receivers)
    {
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        SemanticMember[] bindings = environments.Count == 0 ? [] : environments[0].Keys.Where(name => !name.StartsWith('$') && environments.All(environment => environment.ContainsKey(name)))
            .Select(name => DescribeMember(name, environments.Select(environment => environment[name].Info), scopes, members: true)).ToArray();
        SemanticMember? receiver = receivers.Count == 0 ? null : DescribeMember("", receivers, scopes, members: true);
        return new(bindings, receiver);
    }

    internal static IReadOnlyList<SemanticMember> DescribeBindings(IReadOnlyDictionary<string, Binding> bindings)
    {
        Dictionary<ScopeInfo, ScopeDescription> scopes = [];
        return bindings.Where(pair => !pair.Key.StartsWith('$')).Select(pair => DescribeMember(pair.Key, [pair.Value.Info], scopes, members: true)).ToArray();
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
        try
        {
            EditorParseResult parsed = Parser.ParseForEditor(source, sourceName);
            regexSpans = parsed.RegexSpans;
            Document document = parsed.Document;
            diagnostic = parsed.Error?.Diagnostic;
            Seed(document.Statements, source, syntax);
            Action<SourceSpan, Info, string?, bool> observer = (span, info, name, concrete) =>
            {
                if (!inferred.TryGetValue(span, out var observation)) inferred[span] = observation = new(name);
                if (concrete && !observation.Concrete) observation.Values.Clear();
                if (!concrete && observation.Concrete) return;
                observation.Concrete |= concrete;
                observation.Values[(info.Type, info.Function?.Id, info.NativeName, info.Scope, info.TypeValue, info.KnownConnection, info.KnownPath, info.KnownText)] = info;
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
            syntax[span] = Describe(span, observation.Name, observation.Values.Values, scopeDescriptions);
        return new(syntax.Values.OrderBy(item => item.Span.Start).ThenBy(item => item.Span.Length).ToArray(), diagnostic, recovery, recoveryStart)
        { Contexts = compilation?.EditorContexts, RegexSpans = regexSpans };
    }

    private sealed class Observation(string? name)
    {
        public string? Name { get; } = name;
        public bool Concrete { get; set; }
        public Dictionary<(TypeRef, int?, string?, ScopeInfo?, TypeRef?, ConnectionValue?, PathValue?, string?), Info> Values { get; } = [];
    }

    private sealed record ScopeDescription(SemanticMember[] Members, string Text);

    private static SemanticOccurrence Describe(SourceSpan span, string? name, IEnumerable<Info> values,
        Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = true)
    {
        Info[] choices = values.ToArray();
        bool unknown = choices.Any(value => value.EditorUnknown);
        TypeRef type = choices.Select(value => value.Type).Aggregate(TypeRef.Union);
        string signature = string.Join("\n", choices.Select(value => Signature(name, value)).Distinct(StringComparer.Ordinal));
        string description = type.WithoutNull == TypeRef.Path ? "A filesystem path. Bare unbound names denote Paths. Calling a Path starts a Process."
            : type.WithoutNull == TypeRef.Scope ? "A lexical Scope. Select a declared member with `::`; a Scope is not a physical endpoint."
            : type.WithoutNull == TypeRef.Connection ? "A physical endpoint. Qualify a filesystem path with `connection::path`."
            : type.WithoutNull == TypeRef.Function ? "A callable value. Unannotated parameters are specialized from each call's argument types."
            : type == TypeRef.Type ? "A type value. `decltype(expression)` infers without executing the expression."
            : type.WithoutNull == TypeRef.Bool ? "A boolean value: `true` or `false`. Conditions require Bool; Null is not a boolean."
            : type.WithoutNull == TypeRef.String ? "A text value. Transferring a String writes its bytes; a Path reads a file."
            : type.WithoutNull == TypeRef.Process ? "A process value. Inspect `stdout`, `stderr`, `exit_code` or `pid` with `::`."
            : type == TypeRef.Null ? "The absence of a value. Test with `is null` or `is not null`; postfix `?` means `is null`."
            : $"Inferred type: `{type}`. Inspection does not execute this expression.";
        if (type.Nullable && type != TypeRef.Null)
            description += " Handle Null before using the value; postfix `?` tests whether it is Null.";
        var native = choices.Select(value => value.NativeName).FirstOrDefault(value => value is not null);
        if (native is not null) description += $"\n\nNative standard-library function `{native}`.";
        SemanticMember[]? contents = null;
        if (members && !unknown && choices.Any(value => value.Scope is not null))
        {
            ScopeInfo[] scopes = choices.Where(value => value.Scope is not null).Select(value => value.Scope!).ToArray();
            if (scopes.Length == 1)
            {
                if (!scopeDescriptions.TryGetValue(scopes[0], out var summary))
                {
                    var items = scopes[0].Members.Select(pair => DescribeMember(pair.Key, [pair.Value], scopeDescriptions, members: true)).ToArray();
                    scopeDescriptions[scopes[0]] = summary = new(items, MemberList(items));
                }
                contents = summary.Members;
                description += summary.Text;
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

    private static SemanticMember DescribeMember(string name, IEnumerable<Info> choices, Dictionary<ScopeInfo, ScopeDescription> scopeDescriptions, bool members = false)
    {
        var item = Describe(default, name, choices, scopeDescriptions, members);
        return new(name, item.Type, item.Signature, item.Description, item.Members, item.Endpoint, item.SemanticRole, item.KnownPath, item.KnownText);
    }

    private static string MemberList(IEnumerable<SemanticMember> items) =>
        "\n\nMembers:\n" + string.Join("\n", items.Select(member => $"- `{member.Name}: {member.Type}`"));

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
            return $"fn {name ?? native}({string.Join(", ", registered.Parameters)}) -> {registered.Result}";
        }
        string type = info.TypeValue is { } inferred ? $"Type ({inferred})" : info.Type.ToString();
        return name is null ? type : $"{name}: {type}";
    }

    private static string FunctionSignature(string? name, IReadOnlyList<Parameter> parameters, string result, string source,
        IReadOnlyList<IReadOnlyList<TypeRef>>? specializations = null) =>
        $"fn {name ?? ""}({string.Join(", ", parameters.Select((parameter, index) =>
            $"{parameter.Name}{(parameter.Nullable ? "?" : "")}: {(parameter.Type is not null ? Slice(source, parameter.Type.Span)
                : specializations is null || specializations.Count == 0 ? "inferred at call"
                    : string.Join(" | ", specializations.Select(instance => instance[index].ToString()).Distinct()))}"))}) -> {result}";

    private static string Slice(string source, SourceSpan span) => source[span.Start..span.End];

    private static void Seed(IEnumerable<Node> nodes, string source, Dictionary<SourceSpan, SemanticOccurrence> occurrences)
    {
        foreach (Node node in nodes)
        {
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
            Seed(Compilation.Children(node), source, occurrences);
        }
        void Add(SourceSpan? span, string type, string signature, string? role = null)
        {
            if (span is { } location) occurrences[location] = new(location, type, signature,
                type == "Function" ? "A callable value. Its generic body is checked when specialized by a call."
                    : "A lexical Scope. Select its declared members with `::`.",
                SemanticRole: role ?? (type == "Function" ? "function" : "variable"));
        }
    }
}

internal sealed partial class Compilation
{
    private void Observe(Frame frame, SourceSpan span, Info info, string? name = null)
    {
        if (observe is null || !IsCurrentSource(frame)) return;
        observe(span, info, name, frame.EditorReportErrors);
    }
}
