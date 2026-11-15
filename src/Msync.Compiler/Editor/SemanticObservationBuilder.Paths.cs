using Msync.Core.Native;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class SemanticObservationBuilder
{
    private readonly HashSet<HirNodeId> concreteQualifications = [];

    private SemanticPathView? KnownPath(SemanticValue value, SemanticContext context)
    {
        TypeKind kind = program.Hir.Types[value.Type.Value].RuntimeType.Kind;
        if (value.RuntimeDependentMetadata || kind != TypeKind.Path
            && !(kind is TypeKind.Any or TypeKind.Never
                && (value.Observation?.Path is not null || value.Literal?.Kind == HirLiteralKind.Path))) return null;
        if (value.Observation?.Path is { } imported)
            return new(imported.Raw, imported.WorkingDirectory,
                imported.Connection is { } importedEndpoint ? new(importedEndpoint.Kind, importedEndpoint.Address) : null,
                imported.HomeDirectory, imported.LocalSpelling);
        if (value.Literal is { Kind: HirLiteralKind.Path or HirLiteralKind.String } literal) return new(literal.Text ?? "");
        if (value.Location is { } location && context.Values.TryGetValue(location, out var stored)
            && typedValues.TryGetValue(stored.Node, out var origin))
            return values[origin.Value].Path;
        if (value.Node.IsNone) return null;
        HirNode node = program.Hir.Nodes[value.Node.Value];
        if (node.Kind == HirKind.Block && node.Children.Count != 0)
            return NodePath(program.Hir.Edges[node.Children.Start + node.Children.Count - 1], context);
        if (node.Kind is HirKind.Convert or HirKind.Coerce && node.Children.Count == 1)
            return NodeValue(program.Children(value.Node)[0], context)?.Text is { } converted ? new(converted) : null;
        if (node.Kind == HirKind.If && node.Children.Count == 3)
            return CommonPath(program.Children(value.Node)[1..].ToArray(), context);
        if (node.Kind == HirKind.Match)
            return CommonPath(program.Hir.Matches[node.Data].Arms.Select(arm => arm.Value), context);
        if (node.Kind is not (HirKind.PathJoin or HirKind.QualifyOn) || node.Children.Count != 2) return null;
        HirNodeId first = program.Hir.Edges[node.Children.Start], second = program.Hir.Edges[node.Children.Start + 1];
        SemanticValueView? left = NodeValue(first, context), right = NodeValue(second, context);
        bool qualify = node.Kind == HirKind.QualifyOn;
        SemanticValueView? operand = qualify ? right : left;
        SemanticPathView? path = operand?.Path ?? (operand?.Text is { } text ? new(text) : null);
        if (path is null) return null;
        NativePath current = new(path.Raw, path.WorkingDirectory,
            path.Connection is { } owner ? new(owner.Kind, owner.Address) : null, path.HomeDirectory, path.LocalSpelling);
        NativePath? result = qualify
            ? left?.Connection is { } endpoint && PreservesRawPath(endpoint)
                ? PathSemantics.Qualify(current, new(endpoint.Kind, endpoint.Address)) : null
            : right?.Path is { } fragment ? PathSemantics.JoinValue(current, new NativePath(fragment.Raw))
                : right?.Text is { } component ? PathSemantics.JoinValue(current, component) : null;
        return result is not { } selected ? null : new(selected.Raw, selected.WorkingDirectory,
            selected.Connection is { } connection ? new(connection.Kind, connection.Address) : null,
            selected.HomeDirectory, selected.LocalSpelling);
    }

    private SemanticPathView? NodePath(HirNodeId id, SemanticContext context)
    {
        if (NodeValue(id, context) is { } observed) return observed.Path;
        HirNode node = program.Node(id);
        return node.Kind == HirKind.Block && node.Children.Count != 0
            ? NodePath(program.Hir.Edges[node.Children.Start + node.Children.Count - 1], context) : null;
    }

    private SemanticPathView? CommonPath(IEnumerable<HirNodeId> alternatives, SemanticContext context)
    {
        SemanticPathView? result = null;
        foreach (HirNodeId alternative in alternatives)
        {
            SemanticPathView? candidate = NodePath(alternative, context);
            if (candidate is null || result is not null && result != candidate) return null;
            result = candidate;
        }
        return result;
    }

    private string? KnownText(SemanticValue value, SemanticContext context)
    {
        if (value.RuntimeDependentMetadata || program.Hir.Types[value.Type.Value].RuntimeType.Kind != TypeKind.String) return null;
        if (value.Observation?.Text is { } imported) return imported;
        if (value.Literal is { Kind: HirLiteralKind.String or HirLiteralKind.Path } literal) return literal.Text;
        if (value.Observation?.Path is { } path)
            return PathText(path.Raw, path.LocalSpelling, path.Connection?.Kind);
        if (value.Node.IsNone) return null;
        HirNode node = program.Node(value.Node);
        if (node.Kind == HirKind.Block && node.Children.Count != 0)
            return NodeValue(program.Hir.Edges[node.Children.Start + node.Children.Count - 1], context)?.Text;
        if (node.Kind is not (HirKind.Convert or HirKind.Coerce) || node.Children.Count != 1) return null;
        SemanticValueView? origin = NodeValue(program.Children(value.Node)[0], context);
        return origin?.Text ?? (origin?.Path is { } converted
            ? PathText(converted.Raw, converted.LocalSpelling, converted.Connection?.Kind) : null);
    }

    private static string PathText(string raw, string? local, ConnectionKind? kind) =>
        kind is null or ConnectionKind.Local ? local ?? raw : raw;

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
            var current = KnownPath(returned.Value, context);
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
            var receiver = ProjectExpression(operand);
            receivers.Add(new(Span(use.ReceiverSpan), Value(receiver, context), EditorReceiverKind.PathJoin, Concrete: false));
        }
        foreach (var (qualification, path) in program.QualificationPaths)
            if (path.Source == source && !concreteQualifications.Contains(qualification))
                RecordPhysicalOwner(qualification, context, concrete: false);
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
            SemanticValue value = ProjectExpression(id);
            return value.Literal is { Kind: HirLiteralKind.Connection } literal
                ? new(literal.ConnectionKind, literal.Text ?? "")
                : value.Observation?.Connection is { } connection ? new(connection.Kind, connection.Address) : null;
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

    private static bool PreservesRawPath(SemanticConnectionView endpoint) =>
        endpoint.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl || endpoint is { Kind: ConnectionKind.Local, Address.Length: 0 };
}
