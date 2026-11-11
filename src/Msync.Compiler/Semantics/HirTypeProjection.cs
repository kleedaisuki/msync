using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// A type-only projection of an already bound body. It has no executable-output
// capability, and Source calls consult a return scheme instead of analyzing a body.
internal sealed class HirTypeProjection(BoundProgram program, SemanticArena hir,
    SemanticContext context, HirFlowTypeAlgebra types,
    Func<SemanticCallable, IReadOnlyList<FlowFact>, FlowFact?>? sourceReturn = null,
    Func<HirNodeId, HirLocationId?>? places = null,
    Func<HirNodeId, IEnumerable<SemanticCallable>>? resolveCallable = null)
{
    private readonly Dictionary<int, HirTypeId> scopeResults = [];
    private readonly HashSet<(int Site, HirLocationId Field)> recursiveFields = [];

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
                    if (facts.Contains(location)) return facts.Read(location);
                    if (context.Values.TryGetValue(location, out var current)) return Fact(current.Type, current.KnownBool)
                        with
                    { Eager = current.EagerRange };
                    return Fact(Resolve(hir.Locations[location.Value].StorageType));
                }
            case HirKind.Block:
                return children.Length == 0 ? Fact(hir.Type(TypeRef.Null)) : Project(children[^1], facts);
            case HirKind.Discard:
                {
                    HirNode operand = program.Node(children[0]);
                    TypeRef type = operand.Kind is HirKind.Return or HirKind.Break
                        ? new(TypeKind.Never) : Project(children[0], facts).Type;
                    return Fact(hir.Type(SemanticControl.Discard(type)));
                }
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
                return TypeTest(children, facts);
            case HirKind.RegexMatch:
            case HirKind.Transfer:
                return Fact(hir.Type(TypeRef.Bool));
            case HirKind.TypeOf:
                {
                    FlowFact operand = Project(children[0], facts);
                    return Fact(hir.Type(TypeRef.Type)) with { Denotations = new([operand.SemanticType ?? hir.Type(operand.Type)]) };
                }
            case HirKind.TypeValue:
                return Fact(hir.Type(TypeRef.Type)) with { Denotations = new([Resolve(new(node.Data))]) };
            case HirKind.Convert:
            case HirKind.Coerce:
                {
                    FlowFact converted = Fact(Resolve(new(node.Data)));
                    return converted with { Eager = converted.Type.Kind == TypeKind.Range && Project(children[0], facts).Eager };
                }
            case HirKind.PathJoin:
            case HirKind.Qualify:
                return Fact(hir.Type(TypeRef.Path));
            case HirKind.Range:
            case HirKind.LazyRange:
                {
                    HirTypeId element = hir.Type(new TypeRef(TypeKind.Never));
                    FlowDenotationDomain? denotations = null;
                    bool first = true;
                    foreach (var child in children)
                    {
                        FlowFact item = Project(child, facts);
                        element = types.Union(element, item.SemanticType ?? hir.Type(item.Type));
                        denotations = first ? item.Denotations : FlowDenotationDomain.Join(denotations, item.Denotations);
                        first = false;
                    }
                    return Fact(hir.Type(new HirType(new(TypeKind.Range, hir.Types[element.Value].RuntimeType), Element: element.Value)))
                        with
                    { Denotations = denotations };
                }
            case HirKind.MemberRead:
                {
                    if (places?.Invoke(id) is { } field && facts.Contains(field))
                        return facts.Read(field);
                    FlowFact receiver = Project(children[0], facts);
                    return Fact(types.Member(receiver.SemanticType ?? hir.Type(receiver.Type), node.Text!))
                        with
                    { Eager = Value(id)?.EagerRange == true };
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

    private FlowFact TypeTest(ReadOnlySpan<HirNodeId> children, FlowFacts facts)
    {
        FlowFact subject = Project(children[0], facts);
        if (children.Length != 2 || Project(children[1], facts).Denotations is not { Types.Length: 1 } targets)
            return Fact(hir.Type(TypeRef.Bool));
        TypeRef tested = hir.Types[targets.Types[0].Value].RuntimeType;
        bool? result = null;
        if (subject.Type == TypeRef.Type && subject.Denotations is { } represented)
        {
            foreach (HirTypeId type in represented.Types)
            {
                bool match = tested.Accepts(hir.Types[type.Value].RuntimeType);
                if (result is { } before && before != match) return Fact(hir.Type(TypeRef.Bool));
                result = match;
            }
        }
        else if ((subject.Type.Kind & TypeKind.Type) == 0)
            result = tested.Accepts(subject.Type) ? true : (tested.Kind & subject.Type.Kind) == 0 ? false : null;
        return Fact(hir.Type(TypeRef.Bool), result);
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
            var site = (node.Data, location);
            HirType projectedType = hir.Types[type.Value];
            bool structural = projectedType.Shape >= 0 || projectedType.Element >= 0 || !projectedType.Alternatives.IsDefaultOrEmpty;
            if (structural && !recursiveFields.Contains(site) && scopeResults.TryGetValue(node.Data, out var previous)
                && ContainsType(type, previous, [])) recursiveFields.Add(site);
            if (recursiveFields.Contains(site))
                type = hir.Type(hir.Types[type.Value].RuntimeType);
            fields.Add(new(descriptor.Name, type, descriptor.AdmitsNull));
        }
        ImmutableArray<HirField> row = fields.ToImmutable();
        int shape = hir.InternScopeLayout(new(row));
        HirTypeId result = hir.Type(new HirType(TypeRef.Scope, Shape: shape));
        scopeResults[node.Data] = result;
        return Fact(result);
    }

    private bool ContainsType(HirTypeId source, HirTypeId target, HashSet<HirTypeId> visited)
    {
        if (source == target) return true;
        if (!visited.Add(source)) return false;
        HirType type = hir.Types[source.Value];
        if (!type.Alternatives.IsDefaultOrEmpty)
            foreach (HirTypeId alternative in type.Alternatives)
                if (ContainsType(alternative, target, visited)) return true;
        if (type.Element >= 0 && ContainsType(new(type.Element), target, visited)) return true;
        if (type.Shape >= 0)
            foreach (HirField field in hir.ScopeLayouts[type.Shape].Fields)
                if (ContainsType(field.StorageType, target, visited)) return true;
        return false;
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
            if (choices.Length == 0 && resolveCallable is not null)
            {
                choices = resolveCallable(children[0]).ToArray();
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
            NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
            bool eager = (behavior & NativeBehavior.EagerRange) != 0;
            if (arguments.Length != 0 && HirNativeResultTypes.IsStructuralRange(signature.ResultRule))
            {
                var first = arguments[0];
                HirTypeId? denoted = first.Denotations is { Types.Length: 1 } known ? known.Types[0] : null;
                var structural = HirNativeResultTypes.Project(signature.ResultRule,
                    first.SemanticType ?? hir.Type(first.Type),
                    arguments.Length > 1 ? arguments[1].SemanticType ?? hir.Type(arguments[1].Type) : null,
                    denoted, hir, types);
                if (structural is { } type) return Fact(type) with { Eager = eager };
            }
            if (signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length != 0)
                return arguments[0] with
                {
                    Eager = eager
                    || (behavior & NativeBehavior.PreservesIdentity) != 0 && arguments[0].Eager
                };
            if (signature.ResultRule == NativeResultRule.RangeItem && arguments.Length != 0)
                return Fact(types.Element(arguments[0].SemanticType ?? hir.Type(arguments[0].Type)))
                    with
                { Eager = Value(children[1])?.Element?.EagerRange == true };
            if (signature.ResultRule == NativeResultRule.CastTarget && children.Length > 2)
            {
                HirNode target = program.Node(children[2]);
                if (target.Kind == HirKind.TypeValue)
                {
                    FlowFact cast = Fact(Resolve(new(target.Data)));
                    return cast with { Eager = cast.Type.Kind == TypeKind.Range && arguments[0].Eager };
                }
            }
            return Fact(hir.Type(signature.Result)) with { Eager = eager };
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
