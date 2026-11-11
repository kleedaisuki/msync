using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler.Semantics;

internal sealed partial class SourceBinder
{
    private sealed class Environment(Environment? parent, HirSourceRange owner,
        HirTemplateId? template, ImmutableDictionary<string, HirLocationId>? globals = null,
        bool includesEnd = false)
    {
        public Environment? Parent { get; } = parent;
        public HirSourceRange Owner { get; } = owner;
        public HirTemplateId? Template { get; } = template;
        public bool IncludesEnd { get; } = includesEnd;
        public ImmutableDictionary<string, HirLocationId> Values { get; set; }
            = parent?.Values ?? ImmutableDictionary<string, HirLocationId>.Empty;
        public ImmutableDictionary<string, int> Concepts { get; set; }
            = parent?.Concepts ?? ImmutableDictionary<string, int>.Empty;
        public ImmutableDictionary<string, HirTypeId> Associated { get; set; }
            = parent?.Associated ?? ImmutableDictionary<string, HirTypeId>.Empty;
        public ImmutableDictionary<string, HirLocationId>? Globals { get; } = globals ?? parent?.Globals;
        public Dictionary<HirLocationId, HirLocationId> Captures { get; }
            = parent is not null && parent.Template == template ? parent.Captures : [];
    }

    private readonly HirBuilder hir;
    private readonly BoundProgram program;
    private readonly bool observations;
    private readonly bool editorMode;
    private readonly bool incremental;
    private readonly CancellationToken cancellationToken;
    private HirSourceId source;
    private string sourceName = "<source>";
    private string sourceText = "";
    private string? nativeModule;
    private Environment scope = null!;
    private Environment? annotationScope;
    private StandardBindingDemand? standardDemand;
    private readonly Stack<HirLoopId> loops = [];
    private readonly Queue<string> pendingModules = new();

    public SourceBinder(HirBuilder? builder = null, bool observations = false, bool editorMode = false,
        bool isSession = false, CancellationToken cancellationToken = default)
    {
        hir = builder ?? new();
        program = new(hir);
        program.IsSession = isSession;
        this.observations = observations;
        this.editorMode = editorMode;
        this.cancellationToken = cancellationToken;
    }

    public SourceBinder(BoundProgram previous, bool observations = false, bool editorMode = false,
        bool appendDocument = false, CancellationToken cancellationToken = default)
        : this(previous.Hir, observations, editorMode, isSession: !appendDocument, cancellationToken)
    {
        program.Inherit(previous);
        if (appendDocument) program.DocumentPrefix = previous.Body;
        incremental = true;
    }

    public BoundProgram Bind(string text, string name = "<source>", Document? document = null)
    {
        document ??= Parser.Parse(text, name);
        if (document.EditorError is { } syntax)
        {
            if (!editorMode) throw syntax;
            program.Diagnostics.Add(syntax.Diagnostic);
        }
        sourceText = text;
        sourceName = name;
        if (!program.IsSession && !editorMode && text.Contains('$'))
            standardDemand = new(document, StandardLibraries.Value);
        source = new(hir.Sources.Count);
        hir.Sources.Add(new(name, text));
        if (!incremental)
        {
            program.Entry = new(hir.Functions.Count);
            hir.Functions.Add(new("<main>", [], [], hir.Type(TypeRef.Any), new(-1), IsOpen: true));
        }
        scope = new(null, Range(document.Span), null, includesEnd: true);
        if (incremental)
        {
            scope.Values = program.Exports;
            scope.Concepts = program.RootConcepts;
        }
        program.Body = Statements(document.Statements, document.Span);
        hir.SetFunction(program.Entry, hir.Functions[program.Entry.Value] with { Body = program.Body });
        program.Exports = scope.Values;
        program.RootConcepts = scope.Concepts;
        hir.SetExports(scope.Values.Select(pair => new HirExport(pair.Key, pair.Value)));
        SealStandardBindings();
        return program;
    }

    public BoundModule BindStandardModule(string name)
    {
        if (program.StandardModules.TryGetValue(name, out var existing)) return existing;
        if (!StandardLibraries.Value.TryGetValue(name, out var library))
            throw Error($"Unknown standard module '{name}'.", new(0, 0));
        var previous = (source, sourceName, sourceText, nativeModule, scope, annotationScope);
        source = new(hir.Sources.Count);
        sourceText = library.Source;
        sourceName = $"msync-stdlib:///{Artifact.LanguageVersion}/{name}.msync";
        nativeModule = name;
        hir.Sources.Add(new(sourceName, sourceText));
        scope = new(null, Range(library.Document.Span), null);
        annotationScope = null;
        try
        {
            var statements = name == "concepts" ? library.Document.Statements
                : standardDemand?.Declarations(name, library.Document) ?? library.Document.Statements;
            var initializers = statements.Select(Statement).ToArray();
            var fields = scope.Values.Values.ToImmutableArray();
            int layout = hir.ScopeLayouts.Count;
            hir.ScopeLayouts.Add(new(fields.Select(location => new HirField(
                hir.Locations[location.Value].Name, hir.Locations[location.Value].StorageType,
                hir.Locations[location.Value].AdmitsNull)).ToImmutableArray()));
            int construction = hir.Scopes.Count;
            hir.AddScope(new(layout, fields));
            var body = Node(HirKind.Scope, hir.Type(new HirType(TypeRef.Scope, Shape: layout)),
                library.Document.Span, initializers, construction);
            BoundModule module = new(name, source, body, scope.Values, scope.Concepts);
            program.StandardModules.Add(name, module);
            return module;
        }
        finally
        {
            (source, sourceName, sourceText, nativeModule, scope, annotationScope) = previous;
        }
    }

    private static readonly Lazy<IReadOnlyDictionary<string, StandardSource>> StandardLibraries
        = new(StandardSourceCatalog.Read);

    private HirNodeId StandardReference(string module, SourceSpan span)
    {
        if (module.Length != 0 && !StandardLibraries.Value.ContainsKey(module))
            throw Error($"Unknown standard module '{module}'.", span, "E0205");
        var sourceLocation = ReserveStandardLocation(module, Range(span));
        var location = Capture(sourceLocation, module.Length == 0 ? "$std" : "$std::" + module);
        return Node(HirKind.ReadLocation, hir.Type(TypeRef.Scope), span, data: location.Value,
            effects: HirEffects.ReadLocation);
    }

    private HirLocationId ReserveStandardLocation(string module, HirSourceRange sourceRange)
    {
        if (program.StandardLocations.TryGetValue(module, out var existing)) return existing;
        HirLocationId location = new(hir.Locations.Count);
        string name = module.Length == 0 ? "$std" : "$std::" + module;
        hir.Locations.Add(new(name, hir.Type(TypeRef.Scope), sourceRange, program.Entry, HirLocationKind.Import));
        program.Declare(new(location, module, module.Length == 0
            ? DeclarationKind.StandardRoot : DeclarationKind.StandardModule, sourceRange));
        program.StandardLocations.Add(module, location);
        pendingModules.Enqueue(module);
        return location;
    }

    private void SealStandardBindings()
    {
        while (pendingModules.TryDequeue(out string? module))
        {
            if (module.Length != 0)
            {
                BindStandardModule(module);
                continue;
            }
            var rootSource = hir.Locations[program.StandardLocations[""].Value].Source;
            foreach (string name in StandardLibraries.Value.Keys.Where(name => !name.StartsWith('_')).Order(StringComparer.Ordinal))
                ReserveStandardLocation(name, rootSource);
        }
    }

    private HirSourceRange Range(SourceSpan span) => new(source, span.Start, span.Length);
    private HirTypeId OpenType(string name)
    {
        int parameter = program.NextTypeParameter++;
        program.TypeParameterNames.Add(parameter, name);
        return hir.Type(new HirType(TypeRef.Any, TypeParameter: parameter));
    }
    private HirNodeId Node(HirKind kind, HirTypeId type, SourceSpan span,
        ReadOnlySpan<HirNodeId> children = default, int data = 0, string? text = null,
        HirEffects effects = HirEffects.None) => hir.Add(kind, type, Range(span), children, data, text, effects);
    private HirNodeId Null(SourceSpan span) => hir.Literal(new(HirLiteralKind.Null), hir.Type(TypeRef.Null), Range(span));
    private void Observe(SourceSpan span, HirLocationId location, bool declaration = false, bool parameter = false)
    {
        if (observations) program.References.Add(new(Range(span), location, declaration, parameter));
    }
    private int Context(int position, HirNodeId? anchor = null, bool after = false)
    {
        if (!observations) return -1;
        int index = program.Visibility.Count;
        program.Visibility.Add(new(scope.Owner, position, scope.Values,
            scope.Concepts, scope.IncludesEnd, anchor ?? HirNodeId.None, after));
        return index;
    }

    private HirLocationId Declare(string name, SourceSpan span, DeclarationKind kind,
        HirTypeId type, bool nullable = false, HirTypeId? annotation = null)
    {
        HirLocationId id = new(hir.Locations.Count);
        var locationKind = kind switch
        {
            DeclarationKind.Parameter => HirLocationKind.Parameter,
            DeclarationKind.Field => HirLocationKind.Field,
            DeclarationKind.Capture => HirLocationKind.Import,
            _ => HirLocationKind.Local
        };
        hir.Locations.Add(new(name, type, Range(span), scope.Template is null ? program.Entry : new(-1),
            locationKind, AdmitsNull: nullable, TemplateOwner: scope.Template));
        program.Declare(new(id, name, kind, Range(span), annotation, Template: scope.Template));
        if (nativeModule is not null && scope.Template is null) program.WrittenLocations.Add(id);
        scope.Values = scope.Values.SetItem(name, id);
        Observe(span, id, declaration: true, parameter: kind == DeclarationKind.Parameter);
        return id;
    }

    private HirLocationId Capture(HirLocationId sourceLocation, string name)
    {
        if (scope.Template is null) return sourceLocation;
        return CaptureInto(scope, sourceLocation, name);
    }

    private HirLocationId CaptureInto(Environment environment, HirLocationId sourceLocation, string name)
    {
        if (environment.Template is null || hir.Locations[sourceLocation.Value].TemplateOwner == environment.Template)
            return sourceLocation;
        var parent = environment.Parent;
        while (parent is not null && parent.Template == environment.Template) parent = parent.Parent;
        if (parent is not null) sourceLocation = CaptureInto(parent, sourceLocation, name);
        if (environment.Captures.TryGetValue(sourceLocation, out var saved)) return saved;
        var original = hir.Locations[sourceLocation.Value];
        HirLocationId target = new(hir.Locations.Count);
        hir.Locations.Add(original with
        {
            Owner = new(-1),
            Kind = HirLocationKind.Import,
            TemplateOwner = environment.Template
        });
        program.Declare(new(target, name, DeclarationKind.Capture, original.Source,
            CapturedFrom: sourceLocation, Template: environment.Template));
        environment.Captures.Add(sourceLocation, target);
        if (!name.StartsWith('$')) environment.Values = environment.Values.SetItem(name, target);
        return target;
    }

    private HirNodeId Statements(IReadOnlyList<Statement> statements, SourceSpan span)
    {
        List<HirNodeId> nodes = [];
        int startContext = Context(span.Start);
        foreach (var statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int before = Context(statement.Span.Start);
            var beforeValues = scope.Values;
            var beforeConcepts = scope.Concepts;
            var beforeAssociated = scope.Associated;
            HirNodeId value;
            try { value = Statement(statement); }
            catch (CompileException error) when (editorMode)
            {
                program.Diagnostics.Add(error.Diagnostic);
                scope.Values = beforeValues;
                scope.Concepts = beforeConcepts;
                scope.Associated = beforeAssociated;
                RecoverDeclaration(statement);
                value = Node(HirKind.Error, hir.Type(TypeRef.Any), statement.Span);
            }
            nodes.Add(value);
            if (before >= 0) program.Visibility[before] = program.Visibility[before] with { Anchor = value };
            Context(statement.Span.End, value, after: true);
        }
        if (nodes.Count == 0) nodes.Add(Null(span));
        var result = program.Node(nodes[^1]).Type;
        var block = Node(HirKind.Block, result, span, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nodes));
        if (startContext >= 0) program.Visibility[startContext] = program.Visibility[startContext] with { Anchor = block };
        return block;
    }

    private HirNodeId Statement(Statement statement) => statement switch
    {
        LetStatement let => Let(let),
        ExpressionStatement expression => expression.Discarded
            ? Node(HirKind.Discard, hir.Type(TypeRef.Null), expression.Span, [Expression(expression.Expression)])
            : Expression(expression.Expression),
        FunctionDeclaration function => FunctionDeclaration(function),
        NativeDeclaration native => Native(native),
        ScopeDeclaration named => ScopeDeclaration(named),
        GlobalStatement global => Globals(global),
        ConceptDeclaration concept => Concept(concept),
        _ => throw Error("Unsupported declaration.", statement.Span)
    };

    private HirNodeId Let(LetStatement let)
    {
        var initializer = Expression(let.Value);
        HirTypeId? annotation = let.Type is null ? null : Type(let.Type);
        var targetType = annotation ?? program.Node(initializer).Type;
        if (annotation is null && let.Nullable && hir.Types[targetType.Value].RuntimeType.WithoutNull.Kind == TypeKind.Never)
            targetType = OpenType("nullable storage");
        return BindPattern(let.Pattern, initializer, let.Nullable, targetType, annotation, DeclarationKind.Local);
    }

    private HirNodeId BindPattern(Pattern pattern, HirNodeId value, bool nullable,
        HirTypeId type, HirTypeId? annotation, DeclarationKind kind)
    {
        if (pattern is WildcardPattern or NamePattern { Name: "_" })
            return Node(HirKind.Discard, hir.Type(TypeRef.Null), pattern.Span, [value]);
        if (pattern is NamePattern name)
        {
            var location = Declare(name.Name, name.Span, kind, type, nullable, annotation);
            return Node(HirKind.Declare, hir.Type(TypeRef.Null), pattern.Span, [value], location.Value);
        }
        var tuple = (TuplePattern)pattern;
        List<HirNodeId> declarations = [];
        var count = hir.Literal(new(HirLiteralKind.Number, Number: tuple.Items.Count),
            hir.Type(TypeRef.Number), Range(pattern.Span));
        var prefix = CallNative("ranges.take", program.Node(value).Type, pattern.Span, [value, count]);
        value = CallNative("ranges.materialize", program.Node(value).Type, pattern.Span, [prefix]);
        var temporary = Declare("<destructure>", pattern.Span, DeclarationKind.Local,
            program.Node(value).Type);
        declarations.Add(Node(HirKind.Declare, hir.Type(TypeRef.Null), pattern.Span, [value], temporary.Value));
        for (int index = 0; index < tuple.Items.Count; index++)
        {
            var receiver = Node(HirKind.ReadLocation, hir.Locations[temporary.Value].StorageType, pattern.Span,
                data: temporary.Value, effects: HirEffects.ReadLocation);
            var ordinal = hir.Literal(new(HirLiteralKind.Number, Number: index), hir.Type(TypeRef.Number), Range(pattern.Span));
            var at = CallNative("ranges.at", OpenType("item"), pattern.Span, [receiver, ordinal]);
            declarations.Add(BindPattern(tuple.Items[index], at, nullable, program.Node(at).Type, null, kind));
        }
        return Node(HirKind.Block, hir.Type(TypeRef.Null), pattern.Span,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(declarations));
    }

    private HirNodeId Block(BlockExpr block)
    {
        var outer = scope;
        scope = new(outer, Range(block.Span), outer.Template, includesEnd: block.EditorIncomplete);
        try { return Statements(block.Statements, block.Span); }
        finally { scope = outer; }
    }

    private CompileException Error(string message, SourceSpan span, string code = "E0200") =>
        SemanticDiagnosticFactory.Create(new(sourceName, sourceText), span, message, code);

    private void RecoverDeclaration(Statement statement)
    {
        switch (statement)
        {
            case LetStatement let:
                RecoverPattern(let.Pattern);
                break;
            case FunctionDeclaration function:
                Declare(function.Name, function.Function.NameSpan ?? function.Span,
                    DeclarationKind.Function, hir.Type(TypeRef.Function));
                break;
            case ScopeDeclaration named:
                Declare(named.Name, named.NameSpan ?? named.Span, DeclarationKind.Scope, hir.Type(TypeRef.Scope));
                break;
            case NativeDeclaration native:
                Declare(native.Name, native.NameSpan ?? native.Span, DeclarationKind.Native, hir.Type(TypeRef.Function));
                break;
        }
    }

    private void RecoverPattern(Pattern pattern)
    {
        if (pattern is NamePattern { Name: not "_" } name)
            Declare(name.Name, name.Span, DeclarationKind.Local, hir.Type(TypeRef.Any), nullable: true);
        if (pattern is TuplePattern tuple)
            foreach (var child in tuple.Items) RecoverPattern(child);
    }
}
