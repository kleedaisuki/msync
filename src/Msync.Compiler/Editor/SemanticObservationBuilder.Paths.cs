using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class SemanticObservationBuilder
{
    private readonly HashSet<HirNodeId> concreteQualifications = [];
    private readonly Dictionary<HirNodeId, SemanticConnectionView?> draftEndpoints = [];
    private readonly Dictionary<HirNodeId, PathValue?> draftPaths = [];

    private SemanticPathView? KnownPath(SemanticValue value, SemanticContext context)
    {
        if (value.Observation?.Path is { } imported)
            return new(imported.Raw, imported.WorkingDirectory,
                imported.Connection is { } importedEndpoint ? new(importedEndpoint.Kind, importedEndpoint.Address) : null,
                imported.HomeDirectory, imported.LocalSpelling);
        if (value.Literal is { Kind: HirLiteralKind.Path } literal) return new(literal.Text ?? "");
        if (value.Location is { } location && context.Values.TryGetValue(location, out var stored)
            && typedValues.TryGetValue(stored.Node, out var origin))
            return values[origin.Value].Path;
        if (value.Node.IsNone) return null;
        HirNode node = program.Hir.Nodes[value.Node.Value];
        if (node.Kind == HirKind.Block && node.Children.Count != 0)
            return NodeValue(program.Hir.Edges[node.Children.Start + node.Children.Count - 1], context)?.Path;
        if (node.Kind is not (HirKind.PathJoin or HirKind.QualifyOn) || node.Children.Count != 2) return null;
        HirNodeId first = program.Hir.Edges[node.Children.Start], second = program.Hir.Edges[node.Children.Start + 1];
        SemanticValueView? left = NodeValue(first, context), right = NodeValue(second, context);
        bool qualify = node.Kind == HirKind.QualifyOn;
        SemanticValueView? operand = qualify ? right : left;
        SemanticPathView? path = operand?.Path ?? (operand?.Text is { } text ? new(text) : null);
        if (path is null) return null;
        PathValue current = new(path.Raw, path.WorkingDirectory,
            path.Connection is { } owner ? new(owner.Kind, owner.Address) : null, path.HomeDirectory, path.LocalSpelling);
        PathValue? result = qualify
            ? left?.Connection is { } endpoint && PreservesRawPath(endpoint)
                ? PathSemantics.Qualify(current, new(endpoint.Kind, endpoint.Address)) : null
            : right?.Path is { } fragment ? PathSemantics.JoinValue(current, new PathValue(fragment.Raw))
                : right?.Text is { } component ? PathSemantics.JoinValue(current, new StringValue(component)) : null;
        return result is null ? null : new(result.Raw, result.WorkingDirectory,
            result.Connection is { } connection ? new(connection.Kind, connection.Address) : null,
            result.HomeDirectory, result.LocalSpelling);
    }

    public SemanticRuntimeObservation? ReturnedPath(SemanticValue body, SemanticContext context)
    {
        SemanticPathView? path = null;
        if (program.Hir.Types[body.Type.Value].RuntimeType.Kind != TypeKind.Never)
        {
            path = KnownPath(body, context);
            if (path is null) return null;
        }
        foreach (var returned in context.Returns)
        {
            var current = KnownPath(returned, context);
            if (current is null || path is not null && path != current) return null;
            path = current;
        }
        if (path is null) return null;
        var endpoint = path.Connection is { } connection
            ? new SemanticRuntimeConnection(connection.Kind, connection.Address) : null;
        return new(Path: new(path.Raw, path.WorkingDirectory, path.HomeDirectory, path.LocalSpelling, endpoint));
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

    private void RecordDraftPaths(SemanticContext context, HashSet<SourceSpan> receiverSpans)
    {
        foreach (var use in program.PathUses)
        {
            if (use.ReceiverSpan.Source != source || !receiverSpans.Add(Span(use.ReceiverSpan))) continue;
            HirNodeId operand = program.Children(use.Node)[0];
            var receiver = BoundExpression(operand);
            if (DraftPath(operand) is { } path)
            {
                var endpoint = path.Connection is { } connection
                    ? new SemanticRuntimeConnection(connection.Kind, connection.Address) : null;
                receiver = receiver with
                {
                    Observation = new(Path: new(path.Raw, path.WorkingDirectory, path.HomeDirectory, path.LocalSpelling, endpoint))
                };
            }
            receivers.Add(new(Span(use.ReceiverSpan), Value(receiver, context), EditorReceiverKind.PathJoin, Concrete: false));
        }
        foreach (var (qualification, path) in program.QualificationPaths)
            if (path.Source == source && !concreteQualifications.Contains(qualification))
                RecordPhysicalOwner(qualification, context, concrete: false);
    }

    private PathValue? DraftPath(HirNodeId id)
    {
        if (!draftPaths.TryAdd(id, null)) return draftPaths[id];
        var node = program.Node(id);
        var children = program.Children(id);
        PathValue? path = node.Kind switch
        {
            HirKind.Literal when program.Hir.Literals[node.Data] is { Kind: HirLiteralKind.Path or HirLiteralKind.String } literal =>
                new(literal.Text ?? ""),
            HirKind.ReadLocation when DraftInitializer(new(node.Data)) is { } initializer => DraftPath(initializer),
            HirKind.MemberRead when DraftMember(id) is { } field && DraftInitializer(field) is { } initializer => DraftPath(initializer),
            HirKind.Binary when node.Data == (int)HirBinaryOperator.Divide
                && DraftPath(children[0]) is { } parent && DraftPath(children[1]) is { } child =>
                PathSemantics.JoinValue(parent, child),
            HirKind.QualifyOn when DraftEndpoint(children[0]) is { } endpoint && PreservesRawPath(endpoint)
                && DraftPath(children[1]) is { } unqualified => PathSemantics.Qualify(unqualified, new(endpoint.Kind, endpoint.Address)),
            _ => null
        };
        return draftPaths[id] = path;
    }

    public void RecordQualification(HirNodeId id, SemanticContext context)
    {
        if (context.Publish && program.Node(id).Source.Source == source)
            RecordPhysicalOwner(id, context, concrete: true);
    }

    private void RecordPhysicalOwner(HirNodeId id, SemanticContext context, bool concrete)
    {
        if (concrete) concreteQualifications.Add(id);
        var children = program.Children(id);
        if (children.Length != 2) return;
        SemanticConnectionView? endpoint = Endpoint(children[0]);
        var path = program.Node(children[1]).Source;
        var qualification = program.Node(id).Source;
        var input = program.QualificationPaths[id];
        Add(new(input.Start, path.Start - input.Start), endpoint);
        Collect(children[1], endpoint);

        SemanticConnectionView? Endpoint(HirNodeId id)
        {
            if (concrete)
                return nodeValues.TryGetValue((id, context.Function), out var selected) ? values[selected.Value].Connection : null;
            return DraftEndpoint(id);
        }

        void Add(SourceSpan span, SemanticConnectionView? owner) =>
            pathOwners.Add(new(span, owner, concrete, qualification.Length));

        void Collect(HirNodeId current, SemanticConnectionView? owner)
        {
            HirNode node = program.Node(current);
            var nested = program.Children(current);
            if (node.Kind == HirKind.QualifyOn)
            {
                Collect(nested[1], Endpoint(nested[0]) is { } inner && PreservesRawPath(inner) ? owner : null);
                return;
            }
            if (node.Kind is HirKind.PathJoin or HirKind.SelectPath
                || node.Kind == HirKind.Binary && node.Data == (int)HirBinaryOperator.Divide)
            {
                if (nested.Length != 0) Collect(nested[0], owner);
                if (nested.Length > 1) Collect(nested[1], owner);
                return;
            }
            if (node.Kind == HirKind.Block && nested.Length != 0) { Collect(nested[^1], owner); return; }
            if (node.Kind == HirKind.If)
            {
                if (nested.Length > 1) Collect(nested[1], owner);
                if (nested.Length > 2) Collect(nested[2], owner);
                return;
            }
            if (node.Kind == HirKind.Match)
            {
                foreach (var arm in program.Hir.Matches[node.Data].Arms) Collect(arm.Value, owner);
                return;
            }
            if (node.Kind is HirKind.Literal or HirKind.Error or HirKind.ReadLocation && node.Source.Source == source)
                Add(Span(node.Source), owner);
        }
    }

    private SemanticConnectionView? DraftEndpoint(HirNodeId id)
    {
        if (!draftEndpoints.TryAdd(id, null)) return draftEndpoints[id];
        var node = program.Node(id);
        SemanticConnectionView? endpoint = node.Kind switch
        {
            HirKind.Literal when program.Hir.Literals[node.Data] is { Kind: HirLiteralKind.Connection } literal =>
                new(literal.ConnectionKind, literal.Text ?? ""),
            HirKind.Builtin => node.Text switch
            {
                "wsl" => new(ConnectionKind.Wsl, ""),
                "env" => new(ConnectionKind.Environment, ""),
                "path" => new(ConnectionKind.SearchPath, ""),
                "pwd" or "file" => new(ConnectionKind.Local, node.Text),
                "local" => new(ConnectionKind.Local, ""),
                _ => null
            },
            HirKind.ReadLocation when DraftInitializer(new(node.Data)) is { } initializer => DraftEndpoint(initializer),
            HirKind.MemberRead when DraftMember(id) is { } field && DraftInitializer(field) is { } initializer => DraftEndpoint(initializer),
            _ => null
        };
        return draftEndpoints[id] = endpoint;
    }

    private HirNodeId? DraftInitializer(HirLocationId location)
    {
        location = program.Original(location);
        // An uncalled body may refer to an unchanged lexical declaration,
        // but a parameter or mutable initializer is not a selected value.
        var declaration = program.Declaration(location);
        if (program.WrittenLocations.Contains(location)
            || declaration.Kind == DeclarationKind.Field && program.WrittenMembers.Contains(declaration.Name)
            || program.IsSession && program.Exports.Values.Contains(location)) return null;
        return initializers.TryGetValue(location, out var initializer) ? initializer : null;
    }

    private HirLocationId? DraftMember(HirNodeId id)
    {
        var node = program.Node(id);
        if (node.Text is not { } name || program.WrittenMembers.Contains(name)) return null;
        HirNodeId receiver = program.Children(id)[0];
        var receiverNode = program.Node(receiver);
        HirLocationId? receiverLocation = receiverNode.Kind switch
        {
            HirKind.ReadLocation => new HirLocationId(receiverNode.Data),
            HirKind.MemberRead => DraftMember(receiver),
            _ => null
        };
        HirNodeId? initializer = receiverNode.Kind == HirKind.Scope ? receiver
            : receiverLocation is { } location ? DraftInitializer(location) : null;
        if (initializer is not { } selected || program.Node(selected).Kind != HirKind.Scope) return null;
        var scope = program.Hir.Scopes[program.Node(selected).Data];
        foreach (var field in scope.Fields)
            if (program.Hir.Locations[field.Value].Name == name) return field;
        return null;
    }

    private static bool PreservesRawPath(SemanticConnectionView endpoint) =>
        endpoint.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl || endpoint is { Kind: ConnectionKind.Local, Address.Length: 0 };
}
