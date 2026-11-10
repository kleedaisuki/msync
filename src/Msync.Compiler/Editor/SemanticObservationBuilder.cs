using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class SemanticObservationBuilder
{
    private readonly BoundProgram program;
    private readonly HirSourceId source;
    private readonly List<SemanticValueView> values = [];
    private readonly List<SemanticObservation> observations = [];
    private readonly List<PendingVisibility> contexts = [];
    private readonly List<SemanticReceiver> receivers = [];
    private readonly List<SemanticPathOwner> pathOwners = [];
    private readonly List<PendingImport> imports = [];
    private readonly List<SemanticReference> references = [];
    private readonly Dictionary<(SemanticValue, SemanticScope?), SemanticValueId> valueIds = new(new ValueIdentityComparer());
    private readonly Dictionary<(HirNodeId, HirFunctionId), SemanticValueId> nodeValues = [];
    private readonly Dictionary<HirNodeId, SemanticValueId> typedValues = [];
    private readonly Dictionary<HirLocationId, HirNodeId> initializers = [];
    private readonly Dictionary<HirLocationId, SemanticValue> boundValues = [];
    private readonly Dictionary<HirNodeId, BoundVisibility[]> before;
    private readonly Dictionary<HirNodeId, BoundVisibility[]> after;
    private readonly Dictionary<HirSourceRange, BoundReference[]> uses;
    private readonly Dictionary<HirNodeId, BoundMemberUse[]> memberUses;
    private readonly Dictionary<HirNodeId, BoundPathUse[]> pathUses;
    private readonly Dictionary<HirNodeId, BoundStandardUse[]> standardUses;
    private readonly Dictionary<HirSourceRange, BoundGlobalImport[]> globalImports;
    private readonly HashSet<BoundVisibility> recorded = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SemanticContext, ObservationState> states = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ImmutableDictionary<string, int>, ImmutableDictionary<string, SemanticConceptView>> conceptViews
        = new(ReferenceEqualityComparer.Instance);

    public SemanticObservationBuilder(BoundProgram program)
    {
        this.program = program;
        source = program.Node(program.Body).Source.Source;
        before = program.Visibility.Where(item => !item.After && item.Owner.Source == source)
            .GroupBy(item => item.Anchor).ToDictionary(group => group.Key, group => group.ToArray());
        after = program.Visibility.Where(item => item.After && item.Owner.Source == source)
            .GroupBy(item => item.Anchor).ToDictionary(group => group.Key, group => group.ToArray());
        uses = program.References.Where(item => item.Source.Source == source)
            .GroupBy(item => item.Source).ToDictionary(group => group.Key, group => group.ToArray());
        memberUses = program.MemberUses.Where(item => item.MemberSpan.Source == source)
            .GroupBy(item => item.Node).ToDictionary(group => group.Key, group => group.ToArray());
        pathUses = program.PathUses.Where(item => item.ReceiverSpan.Source == source)
            .GroupBy(item => item.Node).ToDictionary(group => group.Key, group => group.ToArray());
        standardUses = program.StandardUses.Where(item => item.NameSpan.Source == source)
            .GroupBy(item => item.Node).ToDictionary(group => group.Key, group => group.ToArray());
        globalImports = program.GlobalImports.Where(item => item.Source.Source == source)
            .GroupBy(item => item.Source).ToDictionary(group => group.Key, group => group.ToArray());
        for (int index = 0; index < program.Hir.Nodes.Count; index++)
        {
            HirNode node = program.Hir.Nodes[index];
            if (node.Kind == HirKind.Declare && node.Children.Count != 0)
                initializers[new(node.Data)] = program.Hir.Edges[node.Children.Start];
        }
    }

    public void BeforeNode(HirNodeId original, SemanticContext context)
    {
        if (!context.Publish || !before.TryGetValue(original, out var visible)) return;
        foreach (var item in visible) RecordContext(item, context, concrete: true);
    }

    public void RecordNode(HirNodeId original, SemanticValue value, SemanticContext context)
    {
        if (!context.Publish) return;
        HirNode node = program.Node(original);
        if (node.Source.Source != source) return;
        SemanticValueId result = Value(value, context);
        nodeValues[(original, context.Function)] = result;
        if (!value.Node.IsNone) typedValues[value.Node] = result;
        if (node.Kind == HirKind.Declare)
        {
            HirLocationId location = context.Location(new(node.Data));
            if (context.Values.TryGetValue(location, out var declared))
                Observe(program.Hir.Locations[node.Data].Source,
                    declared, context, program.Hir.Locations[location.Value].Name,
                    program.Hir.Locations[location.Value].Kind == HirLocationKind.Parameter);
        }
        if (uses.TryGetValue(node.Source, out var boundUses))
            foreach (var use in boundUses)
            {
                if (use.Declaration) continue;
                HirLocationId location = context.Location(use.Location);
                observations.Add(new(use.Source, result, program.Hir.Locations[location.Value].Name,
                    IsParameter: Parameter(use.Location)));
                references.Add(new(Span(use.Source), [Definition(use.Location)]));
            }
        if (memberUses.TryGetValue(original, out var selected))
            foreach (var use in selected) RecordMember(use, value, context);
        if (pathUses.TryGetValue(original, out var joined))
            foreach (var use in joined)
                if (Receiver(original, context) is { } receiver)
                    receivers.Add(new(Span(use.ReceiverSpan), receiver, EditorReceiverKind.PathJoin));
        if (standardUses.TryGetValue(original, out var standards))
            foreach (var use in standards)
                observations.Add(new(use.NameSpan, result, use.Module));
        if (node.Kind == HirKind.Qualify)
            RecordPhysicalOwner(original, context);
        if (globalImports.TryGetValue(node.Source, out var selectedImports))
            foreach (var import in selectedImports)
                imports.Add(new(Span(import.Source), import.Bindings, Snapshot(context), true));
        if (after.TryGetValue(original, out var completed))
            foreach (var item in completed) RecordContext(item, context, concrete: true);
    }

    public SemanticObservationView Freeze(HirModule module)
    {
        // Uninstantiated generic bodies already have bound names and type variables.
        // Their lexical information does not require checking an artificial call.
        SemanticContext generic = new(program.Entry);
        foreach (var item in program.Visibility)
            if (item.Owner.Source == source && !recorded.Contains(item))
                RecordContext(item, generic, concrete: false);
        var definitions = ImmutableDictionary.CreateBuilder<HirLocationId, SemanticBindingView>();
        foreach (var declaration in program.Declarations)
        {
            SemanticValue value = BoundValue(declaration.Location);
            definitions.Add(declaration.Location, new(declaration.Name, declaration.Location,
                Value(value, generic), Parameter(declaration.Location)));
            if (declaration.Source.Source != source) continue;
            Observe(declaration.Source, value, generic, declaration.Name,
                declaration.Kind == DeclarationKind.Parameter, concrete: false);
            references.Add(new(Span(declaration.Source), [Definition(declaration.Location)], false));
        }
        foreach (var selection in program.MemberUses)
        {
            if (selection.ReceiverSpan.Source != source || receivers.Any(item => item.Span == Span(selection.ReceiverSpan))) continue;
            var children = program.Children(selection.Node);
            if (children.Length == 0) continue;
            SemanticValue receiver = BoundExpression(children[0]);
            receivers.Add(new(Span(selection.ReceiverSpan), Value(receiver, generic), Concrete: false));
            if (selection.Name is null || receiver.Scope is not { } shape
                || !shape.Fields.TryGetValue(selection.Name, out var member)) continue;
            Observe(selection.MemberSpan, member, generic, selection.Name, concrete: false);
            if (shape.Locations.TryGetValue(selection.Name, out var location))
                references.Add(new(Span(selection.MemberSpan), [Definition(location)], false));
        }
        foreach (var use in program.TypeUses)
        {
            if (use.Source.Source != source) continue;
            int concept = program.Hir.Types[use.Type.Value].Concept;
            if (concept >= 0) references.Add(new(Span(use.Source), [program.Concepts[concept].Source]));
        }
        var declarations = definitions.ToImmutable();
        var visible = contexts.Select(item => new SemanticVisibility(Span(item.Visibility.Owner),
            item.Visibility.IncludesEnd, item.Visibility.Position,
            item.Facts.Bindings(item.Visibility.Bindings, declarations),
            Concepts(item.Visibility.Concepts), item.Concrete)).ToImmutableArray();
        var imported = imports.Select(item => new SemanticImport(item.Span,
            item.Facts.Bindings(item.Bindings, declarations), item.Concrete)).ToImmutableArray();
        foreach (var state in states.Values) state.Detach();
        return new(module, source, values.ToImmutableArray(), observations.ToImmutableArray(),
            visible, receivers.ToImmutableArray(), pathOwners.ToImmutableArray(),
            imported, references.ToImmutableArray());
    }

    private void Observe(HirSourceRange range, SemanticValue value, SemanticContext context,
        string? name, bool parameter = false, bool concrete = true)
    {
        if (range.Source == source)
            observations.Add(new(range, Value(value, context), name, concrete, parameter));
    }

    private void RecordContext(BoundVisibility item, SemanticContext context, bool concrete)
    {
        contexts.Add(new(item, Snapshot(context), concrete));
        recorded.Add(item);
    }

    private ImmutableDictionary<string, SemanticConceptView> Concepts(ImmutableDictionary<string, int> concepts)
    {
        if (conceptViews.TryGetValue(concepts, out var saved)) return saved;
        var result = concepts.ToImmutableDictionary(pair => pair.Key, pair => Concept(pair.Value), StringComparer.Ordinal);
        conceptViews.Add(concepts, result);
        return result;
    }

    private void RecordMember(BoundMemberUse use, SemanticValue value, SemanticContext context)
    {
        if (Receiver(use.Node, context) is not { } receiver) return;
        receivers.Add(new(Span(use.ReceiverSpan), receiver));
        if (use.Name is null) return;
        Observe(use.MemberSpan, value, context, use.Name);
        SemanticValueView description = values[receiver.Value];
        if (!description.Members.IsDefault)
            foreach (var field in description.Members.Where(item => item.Name == use.Name && !item.Definitions.IsDefaultOrEmpty))
                references.Add(new(Span(use.MemberSpan), field.Definitions));
    }

    private SemanticValueId? Receiver(HirNodeId node, SemanticContext context)
    {
        var children = program.Children(node);
        return children.Length != 0 && nodeValues.TryGetValue((children[0], context.Function), out var receiver)
            ? receiver : null;
    }

    private SemanticValueId Value(SemanticValue value, SemanticContext context)
    {
        SemanticScope? scope = value.Scope is { } shape && context.Objects.TryGetValue(shape.Region, out var current)
            ? current : value.Scope;
        var key = (value, scope);
        if (valueIds.TryGetValue(key, out var saved)) return saved;
        SemanticValueId id = new(values.Count);
        valueIds.Add(key, id);
        values.Add(new(value.Type));
        ImmutableArray<SemanticFieldView> fields = scope is null ? default : scope.Fields.Select(pair =>
            new SemanticFieldView(pair.Key, Value(pair.Value, context),
                scope.Locations.TryGetValue(pair.Key, out var location) ? [Definition(location)] : []))
            .ToImmutableArray();
        int concept = program.Hir.Types[value.Type.Value].Concept;
        if (scope is null && concept >= 0)
            fields = Requirements(concept).Select(requirement => new SemanticFieldView(requirement.Name,
                Value(new(requirement.Type, HirNodeId.None), context), [requirement.Source], requirement.Nullable))
                .ToImmutableArray();
        var callable = value.Callables.IsDefaultOrEmpty ? null : value.Callables[0];
        string? native = callable?.NativeName;
        HirTemplateId? template = callable?.Template;
        bool unknown = program.Hir.Types[value.Type.Value].TypeParameter >= 0
            && scope is null && callable is null;
        SemanticConnectionView? connection = value.Observation?.Connection is { } imported
            ? new(imported.Kind, imported.Address)
            : value.Literal is { Kind: HirLiteralKind.Connection } endpoint
                ? new(endpoint.ConnectionKind, endpoint.Text ?? "") : null;
        SemanticPathView? path = KnownPath(value, context);
        BoundNativeSignature? nativeSignature = native is not null
            ? program.NativeSignatures.GetValueOrDefault(native) : null;
        SemanticValueId? element = Element(value, context);
        TypeRef? currentType = IndexedResourceCount(value) == 0 ? null
            : program.Hir.Types[value.Type.Value].RuntimeType with { Element = RuntimeType(element!.Value) };
        values[id.Value] = new(value.Type, Template: template, TypeValue: value.DenotedType,
            NativeName: native, IsNamedScope: scope?.Named == true, IsUnknown: unknown,
            Members: fields,
            Element: element, CurrentType: currentType,
            Connection: connection, Path: path,
            Text: value.Observation?.Text
                ?? (value.Literal is { Kind: HirLiteralKind.String } text ? text.Text : null),
            Signature: nativeSignature is null ? null : NativeSignature(nativeSignature),
            Documentation: nativeSignature is null ? null : Documentation(nativeSignature.Source));
        if (!value.Callables.IsDefault && value.Callables.Length > 1)
            values[id.Value] = values[id.Value] with
            {
                Alternatives = value.Callables.Select(candidate => Callable(values[id.Value], candidate)).ToImmutableArray()
            };
        return id;
    }

    private SemanticValueId Callable(SemanticValueView value, SemanticCallable callable)
    {
        BoundNativeSignature? declaration = callable.NativeName is { } native
            ? program.NativeSignatures.GetValueOrDefault(native) : null;
        SemanticValueId id = new(values.Count);
        values.Add(value with
        {
            Template = callable.Template, NativeName = callable.NativeName, Alternatives = default,
            Signature = declaration is null ? null : NativeSignature(declaration),
            Documentation = declaration is null ? null : Documentation(declaration.Source)
        });
        return id;
    }

    private static int IndexedResourceCount(SemanticValue value) => value.IndexedRegion is not null ? 1
        : value.IndexedAliases.IsDefault ? 0 : value.IndexedAliases.Length;

    private static int IndexedResourceAt(SemanticValue value, int index) => value.IndexedRegion ?? value.IndexedAliases[index];

    private SemanticValueId? Element(SemanticValue value, SemanticContext context)
    {
        int count = IndexedResourceCount(value);
        if (count == 0) return value.Element is null ? null : Value(value.Element, context);
        SemanticValueId first = Value(context.Indexed[IndexedResourceAt(value, 0)].Element, context);
        if (count == 1) return first;
        var alternatives = ImmutableArray.CreateBuilder<SemanticValueId>(count);
        alternatives.Add(first);
        TypeRef type = RuntimeType(first);
        for (int index = 1; index < count; index++)
        {
            var element = Value(context.Indexed[IndexedResourceAt(value, index)].Element, context);
            alternatives.Add(element);
            type = TypeRef.Union(type, RuntimeType(element));
        }
        SemanticValueId joined = new(values.Count);
        values.Add(new(values[first.Value].Type, Alternatives: alternatives.MoveToImmutable(), CurrentType: type));
        return joined;
    }

    private TypeRef RuntimeType(SemanticValueId value) => values[value.Value].CurrentType
        ?? program.Hir.Types[values[value.Value].Type.Value].RuntimeType;

    private SemanticPathView? KnownPath(SemanticValue value, SemanticContext context)
    {
        if (value.Observation?.Path is { } imported)
            return new(imported.Raw, imported.WorkingDirectory,
                imported.Connection is { } importedEndpoint ? new(importedEndpoint.Kind, importedEndpoint.Address) : null,
                imported.HomeDirectory, imported.LocalSpelling);
        if (value.Literal is { Kind: HirLiteralKind.Path } literal) return new(literal.Text ?? "");
        if (value.Node.IsNone) return null;
        HirNode node = program.Hir.Nodes[value.Node.Value];
        if (node.Kind is not (HirKind.PathJoin or HirKind.Qualify) || node.Children.Count != 2) return null;
        HirNodeId first = program.Hir.Edges[node.Children.Start], second = program.Hir.Edges[node.Children.Start + 1];
        SemanticValueView? left = NodeValue(first, context), right = NodeValue(second, context);
        SemanticPathView? path = left?.Path ?? (left?.Text is { } text ? new(text) : null);
        if (path is null) return null;
        PathValue current = new(path.Raw, path.WorkingDirectory,
            path.Connection is { } owner ? new(owner.Kind, owner.Address) : null, path.HomeDirectory, path.LocalSpelling);
        PathValue? result = node.Kind == HirKind.Qualify
            ? right?.Connection is { } endpoint
                ? PathSemantics.Qualify(current, new(endpoint.Kind, endpoint.Address)) : null
            : right?.Path is { } fragment ? PathSemantics.JoinValue(current, new PathValue(fragment.Raw))
                : right?.Text is { } component ? PathSemantics.JoinValue(current, new StringValue(component)) : null;
        return result is null ? null : new(result.Raw, result.WorkingDirectory,
            result.Connection is { } connection ? new(connection.Kind, connection.Address) : null,
            result.HomeDirectory, result.LocalSpelling);
    }

    private SemanticValueView? NodeValue(HirNodeId typedNode, SemanticContext context)
    {
        if (typedValues.TryGetValue(typedNode, out var selected)) return values[selected.Value];
        var node = program.Hir.Nodes[typedNode.Value];
        if (node.Kind == HirKind.Literal)
        {
            HirLiteral literal = program.Hir.Literals[node.Data];
            return new(node.Type,
                Connection: literal.Kind == HirLiteralKind.Connection ? new(literal.ConnectionKind, literal.Text ?? "") : null,
                Path: literal.Kind == HirLiteralKind.Path ? new(literal.Text ?? "") : null,
                Text: literal.Kind == HirLiteralKind.String ? literal.Text : null);
        }
        if (node.Kind == HirKind.ReadLocation && context.Values.TryGetValue(new(node.Data), out var value))
            return values[Value(value, context).Value];
        return null;
    }

    private void RecordPhysicalOwner(HirNodeId id, SemanticContext context)
    {
        var children = program.Children(id);
        if (children.Length != 2) return;
        SemanticConnectionView? endpoint = nodeValues.TryGetValue((children[1], context.Function), out var selected)
            ? values[selected.Value].Connection : null;
        Collect(children[0]);

        void Collect(HirNodeId current)
        {
            HirNode node = program.Node(current);
            var nested = program.Children(current);
            if (node.Kind is HirKind.PathJoin or HirKind.Qualify)
            {
                if (nested.Length != 0) Collect(nested[0]);
                if (node.Kind == HirKind.PathJoin && nested.Length > 1) Collect(nested[1]);
                return;
            }
            if (node.Kind == HirKind.Block && nested.Length != 0) { Collect(nested[^1]); return; }
            if (node.Kind == HirKind.If)
            {
                if (nested.Length > 1) Collect(nested[1]);
                if (nested.Length > 2) Collect(nested[2]);
                return;
            }
            if (node.Kind == HirKind.Match)
            {
                foreach (var arm in program.Hir.Matches[node.Data].Arms) Collect(arm.Value);
                return;
            }
            if (node.Source.Source == source) pathOwners.Add(new(Span(node.Source), endpoint));
        }
    }

    private SemanticValue BoundValue(HirLocationId location)
    {
        if (boundValues.TryGetValue(location, out var saved)) return saved;
        var definition = program.Hir.Locations[location.Value];
        SemanticValue value = new(definition.StorageType, HirNodeId.None, Location: location);
        boundValues.Add(location, value);
        if (program.FunctionLocations.TryGetValue(location, out var template))
            value = value with { Callables = [new(template)] };
        else if (program.NativeLocations.TryGetValue(location, out string? native))
            value = value with { Callables = [new(NativeName: native)] };
        else if (program.TryDeclaration(location, out var declaration) && declaration.CapturedFrom is { } parent)
            value = BoundValue(parent);
        else if (initializers.TryGetValue(location, out var initializer))
            value = BoundExpression(initializer);
        boundValues[location] = value;
        return value;
    }

    private SemanticValue BoundExpression(HirNodeId id)
    {
        HirNode node = program.Node(id);
        if (node.Kind == HirKind.ReadLocation) return BoundValue(new(node.Data));
        if (node.Kind == HirKind.Literal) return new(node.Type, id, program.Hir.Literals[node.Data]);
        if (node.Kind == HirKind.NativeFunction) return new(node.Type, id, Callables: [new(NativeName: node.Text)]);
        if (node.Kind == HirKind.TemplateClosure)
            return new(node.Type, id, Callables: [new(program.Hir.TemplateClosures[node.Data].Template)]);
        if (node.Kind == HirKind.Scope)
        {
            HirScopeConstruction construction = program.Hir.Scopes[node.Data];
            var fields = construction.Fields.ToImmutableDictionary(location => program.Hir.Locations[location.Value].Name,
                BoundValue, StringComparer.Ordinal);
            var locations = construction.Fields.ToImmutableDictionary(location => program.Hir.Locations[location.Value].Name,
                location => location, StringComparer.Ordinal);
            return new(node.Type, id, Scope: new(-1, construction.Layout, fields, locations, construction.Singleton >= 0));
        }
        return new(node.Type, id);
    }

    private SemanticConceptView Concept(int id)
    {
        BoundConcept concept = program.Concepts[id];
        var members = concept.Requirements.Select(requirement => new SemanticMember(requirement.Name,
            program.Hir.Types[requirement.Type.Value].RuntimeType.ToString(),
            requirement.DeclarationSource is { } written ? Text(written)
                : requirement.Parameters.IsDefault ? $"{requirement.Name}: {TypeName(requirement.Type)}"
                    : $"{requirement.Name}: fn({string.Join(", ", requirement.Parameters.Select(TypeName))}) -> {TypeName(requirement.Result!.Value)}",
            "A required member of this structural concept.",
            SemanticRole: requirement.Parameters.IsDefault ? "variable" : "function")).ToImmutableArray();
        return new(concept.Name, concept.Source, $"concept {concept.Name}",
            Documentation(concept.Source)
                + "\n\nA named structural constraint. Required fields and callable signatures are checked at specialization."
                + (members.Length == 0 ? "" : "\n\nRequired members:\n" + string.Join('\n', members.Select(member => $"- `{member.Signature}`"))), members);
    }

    private string TypeName(HirTypeId type) => program.Hir.Types[type.Value].TypeParameter >= 0
        ? program.TypeParameterNames.GetValueOrDefault(program.Hir.Types[type.Value].TypeParameter, "inferred at call")
        : program.Hir.Types[type.Value].RuntimeType.ToString();

    private IEnumerable<BoundRequirement> Requirements(int concept)
    {
        BoundConcept definition = program.Concepts[concept];
        return definition.Bases.SelectMany(Requirements).Concat(definition.Requirements)
            .DistinctBy(requirement => requirement.Name);
    }

    private string NativeSignature(BoundNativeSignature declaration)
    {
        NativeSignatures.TryGet(declaration.Name, out var native);
        string parameters = string.Join(", ", declaration.Parameters.Select((type, index) =>
        {
            TypeRef runtime = program.Hir.Types[type.Value].RuntimeType;
            bool nullable = !declaration.NullableParameters.IsDefault && declaration.NullableParameters[index]
                || runtime.Nullable && runtime != TypeRef.Null;
            string prefix = native?.Variadic == true && index >= native.Parameters.Count ? "..." : "";
            return $"{prefix}{declaration.ParameterNames[index]}{(nullable ? "?" : "")}: "
                + (nullable ? runtime.WithoutNull.ToString() : TypeName(type));
        }));
        return $"fn {declaration.Name}({parameters}) -> {TypeName(declaration.Result)}";
    }

    private string Text(HirSourceRange source) => program.Hir.Sources[source.Source.Value].Text is { } text
        ? text.Substring(source.Start, source.Length) : "";

    private string Documentation(HirSourceRange source) => program.Hir.Sources[source.Source.Value].Text is { } text
        ? SemanticStandardCatalog.Documentation(text, source.Start) : "";

    private HirSourceRange Definition(HirLocationId location)
    {
        while (program.TryDeclaration(location, out var declaration) && declaration.CapturedFrom is { } parent) location = parent;
        return program.Hir.Locations[location.Value].Source;
    }

    private bool Parameter(HirLocationId location)
    {
        while (program.TryDeclaration(location, out var declaration) && declaration.CapturedFrom is { } parent) location = parent;
        return program.Hir.Locations[location.Value].Kind == HirLocationKind.Parameter;
    }

    private static SourceSpan Span(HirSourceRange source) => new(source.Start, source.Length);

    private sealed class ValueIdentityComparer : IEqualityComparer<(SemanticValue, SemanticScope?)>
    {
        public bool Equals((SemanticValue, SemanticScope?) left, (SemanticValue, SemanticScope?) right) =>
            ReferenceEquals(left.Item1, right.Item1) && ReferenceEquals(left.Item2, right.Item2);
        public int GetHashCode((SemanticValue, SemanticScope?) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Item1),
            key.Item2 is null ? 0 : RuntimeHelpers.GetHashCode(key.Item2));
    }
}
