using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// A type-only projection of an already bound body. It has no executable-output
// capability, and Source calls consult a return scheme instead of analyzing a body.
internal sealed class HirTypeProjection(BoundProgram program, SemanticArena hir,
    SemanticContext context, HirFlowTypeAlgebra types,
    Func<SemanticCallable, IReadOnlyList<FlowFact>, FlowFact?>? sourceReturn = null)
{
    public FlowFact Project(HirNodeId id, FlowFacts facts)
    {
        HirNode node = program.Node(id);
        var children = program.Children(id);
        switch (node.Kind)
        {
            case HirKind.Literal:
                return Fact(Resolve(node.Type), hir.Literals[node.Data].Kind == HirLiteralKind.Bool
                    ? hir.Literals[node.Data].Boolean : null);
            case HirKind.ReadLocation:
            case HirKind.ReadStorage:
            {
                HirLocationId location = context.Location(new(node.Data));
                if (facts.Contains(location)) return facts.Read(location,
                    context.DeferredReads ? FlowReadKind.Deferred : FlowReadKind.Immediate);
                if (context.Values.TryGetValue(location, out var current)) return Fact(current.Type, current.KnownBool);
                return Fact(Resolve(hir.Locations[location.Value].StorageType));
            }
            case HirKind.Block:
                return children.Length == 0 ? Fact(hir.Type(TypeRef.Null)) : Project(children[^1], facts);
            case HirKind.Discard:
            case HirKind.Declare:
            case HirKind.Assign:
            case HirKind.MemberAssign:
            case HirKind.For:
                return Fact(hir.Type(TypeRef.Null));
            case HirKind.Return:
            case HirKind.Break:
                return children.Length == 0 ? Fact(hir.Type(TypeRef.Null)) : Project(children[0], facts);
            case HirKind.If:
            {
                FlowFact condition = Project(children[0], facts);
                if (condition.Boolean is { } selected) return Project(children[selected ? 1 : 2], facts);
                return types.Join(Project(children[1], facts), Project(children[2], facts));
            }
            case HirKind.Match:
            {
                FlowFact result = Fact(hir.Type(new TypeRef(TypeKind.Never)));
                foreach (var arm in hir.Matches[node.Data].Arms) result = types.Join(result, Project(arm.Value, facts));
                return result;
            }
            case HirKind.Unary:
                return Fact(hir.Type((HirUnaryOperator)node.Data == HirUnaryOperator.Not ? TypeRef.Bool : TypeRef.Number),
                    (HirUnaryOperator)node.Data == HirUnaryOperator.Not && Project(children[0], facts).Boolean is { } known ? !known : null);
            case HirKind.Binary:
            {
                var operation = (HirBinaryOperator)node.Data;
                if (operation >= HirBinaryOperator.Equal) return Fact(hir.Type(TypeRef.Bool));
                TypeRef left = Project(children[0], facts).Type, right = Project(children[1], facts).Type;
                return Fact(hir.Type(operation == HirBinaryOperator.Add && left == TypeRef.String && right == TypeRef.String
                    ? TypeRef.String : operation == HirBinaryOperator.Divide && (left != TypeRef.Number || right != TypeRef.Number)
                        ? TypeRef.Path : TypeRef.Number));
            }
            case HirKind.IsNull:
            {
                TypeRef operand = Project(children[0], facts).Type;
                return Fact(hir.Type(TypeRef.Bool), operand == TypeRef.Null ? true : !operand.Nullable ? false : null);
            }
            case HirKind.IsType:
            case HirKind.RegexMatch:
            case HirKind.Transfer:
                return Fact(hir.Type(TypeRef.Bool));
            case HirKind.TypeOf:
            case HirKind.TypeValue:
                return Fact(hir.Type(TypeRef.Type));
            case HirKind.Convert:
            case HirKind.Coerce:
                return Fact(Resolve(new(node.Data)));
            case HirKind.PathJoin:
            case HirKind.Qualify:
                return Fact(hir.Type(TypeRef.Path));
            case HirKind.Range:
            case HirKind.LazyRange:
            {
                HirTypeId element = hir.Type(new TypeRef(TypeKind.Never));
                foreach (var child in children)
                {
                    FlowFact item = Project(child, facts);
                    element = types.Union(element, item.SemanticType ?? hir.Type(item.Type));
                }
                return Fact(hir.Type(new HirType(new(TypeKind.Range, hir.Types[element.Value].RuntimeType), Element: element.Value)));
            }
            case HirKind.MemberRead:
            {
                FlowFact receiver = Project(children[0], facts);
                return Fact(types.Member(receiver.SemanticType ?? hir.Type(receiver.Type), node.Text!));
            }
            case HirKind.Scope:
                return Scope(node, children, facts);
            case HirKind.TemplateClosure:
                return Fact(hir.Type(TypeRef.Function)) with { Callables = new([new(hir.TemplateClosures[node.Data].Template)]) };
            case HirKind.NativeFunction:
                return Fact(hir.Type(TypeRef.Function)) with { Callables = new([new(NativeName: node.Text)]) };
            case HirKind.Closure:
                return Fact(hir.Type(TypeRef.Function));
            case HirKind.Call:
                return Call(node, children, facts);
            case HirKind.Barrier:
                return children.Length == 0 ? Fact(hir.Type(TypeRef.Null)) : Project(children[0], facts);
            default:
                return Fact(Resolve(node.Type));
        }
    }

    private FlowFact Scope(HirNode node, ReadOnlySpan<HirNodeId> children, FlowFacts facts)
    {
        HirScopeConstruction construction = hir.Scopes[node.Data];
        var fields = ImmutableArray.CreateBuilder<HirField>();
        foreach (HirLocationId location in construction.Fields)
        {
            HirLocation descriptor = hir.Locations[location.Value];
            HirTypeId type = Resolve(descriptor.StorageType);
            foreach (HirNodeId child in children)
                if (Initializer(child, location) is { } value)
                {
                    FlowFact projected = Project(value, facts);
                    type = projected.SemanticType ?? hir.Type(projected.Type);
                    break;
                }
            if (descriptor.AdmitsNull)
            {
                HirType stored = hir.Types[type.Value];
                type = hir.Type(stored with { RuntimeType = stored.RuntimeType.WithNull, Nullable = true });
            }
            fields.Add(new(descriptor.Name, type, descriptor.AdmitsNull));
        }
        ImmutableArray<HirField> row = fields.ToImmutable();
        int shape = -1;
        for (int index = 0; index < hir.ScopeLayouts.Count; index++)
            if (hir.ScopeLayouts[index].Fields.AsSpan().SequenceEqual(row.AsSpan())) { shape = index; break; }
        if (shape < 0) shape = hir.InternScopeLayout(new(row));
        return Fact(hir.Type(new HirType(TypeRef.Scope, Shape: shape)));
    }

    private HirNodeId? Initializer(HirNodeId id, HirLocationId location)
    {
        HirNode node = program.Node(id);
        var children = program.Children(id);
        if (node.Kind == HirKind.Declare && node.Data == location.Value) return children[0];
        if (node.Kind != HirKind.Block) return null;
        foreach (HirNodeId child in children)
            if (Initializer(child, location) is { } found) return found;
        return null;
    }

    private FlowFact Call(HirNode node, ReadOnlySpan<HirNodeId> children, FlowFacts facts)
    {
        HirCall call = hir.Calls[node.Data];
        string? native = call.NativeName;
        SemanticCallable[] choices = [];
        if (children.Length != 0)
        {
            HirNode callee = program.Node(children[0]);
            if (callee.Kind == HirKind.NativeFunction) native = callee.Text;
            else if (callee.Kind == HirKind.TemplateClosure)
                choices = [new(hir.TemplateClosures[callee.Data].Template)];
            else if (Value(children[0]) is { } value && !value.Callables.IsDefaultOrEmpty)
            {
                choices = value.Callables.ToArray();
                if (choices.Length == 1) native = choices[0].NativeName;
            }
            if (Project(children[0], facts).Callables is { } domain)
            {
                choices = domain.Choices.Select(choice => choices.FirstOrDefault(item =>
                    item.Template == choice.Template && item.NativeName == choice.NativeName)
                    ?? new SemanticCallable(choice.Template, choice.NativeName)).ToArray();
                native = choices.Length == 1 ? choices[0].NativeName : null;
            }
        }
        FlowFact[] arguments = new FlowFact[Math.Max(0, children.Length - 1)];
        for (int index = 0; index < arguments.Length; index++) arguments[index] = Project(children[index + 1], facts);
        if (native is not null && NativeSignatures.TryGet(native, out var signature))
        {
            if (signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length != 0) return arguments[0];
            if (signature.ResultRule == NativeResultRule.RangeItem && arguments.Length != 0)
                return Fact(types.Element(arguments[0].SemanticType ?? hir.Type(arguments[0].Type)));
            if (signature.ResultRule == NativeResultRule.CastTarget && children.Length > 2)
            {
                HirNode target = program.Node(children[2]);
                if (target.Kind == HirKind.TypeValue) return Fact(Resolve(new(target.Data)));
            }
            return Fact(hir.Type(signature.Result));
        }
        if (sourceReturn is not null && choices.Length != 0)
        {
            FlowFact? result = null;
            foreach (SemanticCallable choice in choices)
                if (sourceReturn(choice, arguments) is { } returned) result = result is { } before ? types.Join(before, returned) : returned;
            if (result is { } value) return value;
        }
        if (call.Kind == HirCallKind.Path) return Fact(hir.Type(TypeRef.Process));
        return Fact(Resolve(node.Type));
    }

    private SemanticValue? Value(HirNodeId id)
    {
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            return context.Values.GetValueOrDefault(context.Location(new(node.Data)));
        if (node.Kind != HirKind.MemberRead || Value(program.Children(id)[0])?.Scope is not { } scope) return null;
        if (context.Objects.TryGetValue(scope.Region, out var updated)) scope = updated;
        return scope.Fields.GetValueOrDefault(node.Text!);
    }

    private HirTypeId Resolve(HirTypeId type)
    {
        while (context.Substitutions.TryGetValue(type, out var target) && target != type) type = target;
        return type;
    }

    private FlowFact Fact(HirTypeId type, bool? boolean = null) =>
        new(hir.Types[type.Value].RuntimeType, Boolean: boolean, SemanticType: type);
}
