using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private sealed record MetaQueryResult(SemanticValue Value, ImmutableArray<MetaQueryResult> Items = default)
    {
        public static implicit operator MetaQueryResult(SemanticValue value) => new(value);
    }
    internal static bool IsDependentMeta(string? name) => name is
        "meta.is_invocable" or "meta.is_invocable_r" or "meta.invoke_result" or "meta.try_invoke_result" or "meta.common_type_of" or
        "meta.parameter_count" or "meta.parameter_types" or "meta.is_variadic" or "meta.signature_of" or
        "meta.has_member" or "meta.member_type" or "meta.member_names" or "meta.member_count" or "meta.members_of"
        or "storage._leaf_callable";

    private bool TryMetaCall(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context, out SemanticValue result)
    {
        result = null!;
        string? name = hir.Calls[node.Data].NativeName;
        if (name is null)
        {
            HirNode callee = program.Node(children[0]);
            if (callee.Kind == HirKind.NativeFunction) name = callee.Text;
            else if (BoundValue(children[0], context) is { } known) name = UniqueMetaName(known);
            if (name is null && BoundLocation(children[0]) is { } location)
                program.NativeLocations.TryGetValue(location, out name);
        }
        if (!IsDependentMeta(name)) return false;
        result = EvaluateMetaCall(name!, node, children, context);
        return true;
    }

    private bool TryMetaCall(SemanticValue callee, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context, out SemanticValue result)
    {
        result = null!;
        string? name = UniqueMetaName(callee);
        if (name is null) return false;
        result = EvaluateMetaCall(name, node, children, context);
        if (context.Publish)
        {
            var discard = context.Output!.Add(HirKind.Discard, hir.Type(TypeRef.Null), node.Source, [callee.Node]);
            result = result with { Node = context.Output.Add(HirKind.Block, result.Type, node.Source, [discard, result.Node]) };
        }
        return true;
    }

    private static string? UniqueMetaName(SemanticValue callee)
    {
        if (callee.Callables.IsDefaultOrEmpty) return null;
        string? name = null;
        foreach (var choice in callee.Callables)
        {
            if (!IsDependentMeta(choice.NativeName) || name is not null && name != choice.NativeName) return null;
            name = choice.NativeName;
        }
        return name;
    }

    private SemanticValue EvaluateMetaCall(string name, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        NativeSignature signature = NativeSignatures.Get(name!);
        int count = children.Length - 1;
        string operation = name[(name.IndexOf('.') + 1)..];
        if (count < signature.Parameters.Count || !signature.Variadic && count != signature.Parameters.Count)
            throw Error(node.Source, $"{operation} expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments.", "E0222");
        SemanticContext query = context.QueryFork();
        SemanticValue[] arguments = new SemanticValue[count];
        for (int index = 0; index < count; index++) arguments[index] = Analyze(children[index + 1], query);
        var answer = DependentMetaResult(operation, arguments, node.Source, query);
        return EmitMetaResult(answer, node.Source, context);
    }

    private MetaQueryResult DependentMetaResult(string operation, SemanticValue[] arguments,
        HirSourceRange source, SemanticContext context)
    {
        if (operation == "_leaf_callable") return MetaBoolean(IsLeafCallable(arguments[0]));
        if (operation == "common_type_of") return CommonTypeOf(arguments, source);
        if (operation is "is_invocable" or "is_invocable_r" or "invoke_result" or "try_invoke_result")
            return MetaInvocation(operation, arguments, source, context);
        if (operation is "parameter_count" or "parameter_types" or "is_variadic")
            return MetaSignature(operation, arguments[0]);
        if (operation == "signature_of") return DescribeSignature(arguments[0]);
        if (operation == "members_of") return DescribeMembers(arguments[0], context);
        var members = MetaMembers(arguments[0], context);
        if (operation == "member_names")
            return MetaRange(members.Keys.Order(StringComparer.Ordinal).Select(name =>
                MetaLiteral(TypeRef.String, new(HirLiteralKind.String, Text: name))), hir.Type(TypeRef.String));
        if (operation == "member_count") return MetaLiteral(TypeRef.Number, new(HirLiteralKind.Number, Number: members.Count));
        if (arguments[1].Literal is not { Kind: HirLiteralKind.String, Text: { } member })
            throw Error(source, "A member query requires a statically inferred String name.", "E0222");
        bool found = members.TryGetValue(member, out var value);
        return operation == "has_member" ? MetaBoolean(found) : found ? MetaType(value!.Type) : MetaNull();
    }

    private Dictionary<HirTemplateId, bool>? leafCallables;

    // Only scalar parameter operations; no captured or reachable mutable storage.
    private bool IsLeafCallable(SemanticValue value)
    {
        if (value.Callables.IsDefaultOrEmpty) return false;
        var known = leafCallables ??= [];
        if (!AllLeaves(value.Callables)) return false;
        return value.FutureCallableDomain is not { } future
            || AllLeaves(future.Choices) && AllLeaves(future.Current);

        bool AllLeaves(ImmutableArray<SemanticCallable> choices)
        {
            if (choices.IsDefaultOrEmpty) return true;
            foreach (var choice in choices)
            {
                if (choice.Template is not { } id || !choice.Captures.IsDefaultOrEmpty) return false;
                if (!known.TryGetValue(id, out bool leaf))
                {
                    var template = hir.Templates[id.Value];
                    leaf = IsLeafBody(template.Body, template.Parameters);
                    known.Add(id, leaf);
                }
                if (!leaf) return false;
            }
            return true;
        }
    }

    private bool IsLeafBody(HirNodeId id, ImmutableArray<HirLocationId> parameters)
    {
        if (id.IsNone) return false;
        var node = program.Node(id);
        if (node.Kind is HirKind.TypeOf or HirKind.Literal or HirKind.TypeValue) return true;
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage) return parameters.Contains(new(node.Data));
        if (node.Kind is not (HirKind.Block or HirKind.Return or HirKind.If or HirKind.Discard
            or HirKind.IsNull or HirKind.IsType or HirKind.Unary or HirKind.Binary)) return false;
        foreach (var child in program.Children(id))
            if (!IsLeafBody(child, parameters)) return false;
        return true;
    }

    private SemanticValue MetaInvocation(string operation, SemanticValue[] inputs,
        HirSourceRange source, SemanticContext context)
    {
        int offset = operation == "is_invocable_r" ? 1 : 0;
        HirTypeId? expected = offset == 0 ? null : inputs[0].DenotedType
            ?? throw Error(source, "is_invocable_r requires a statically inferred result Type.", "E0222");
        SemanticValue callee = MetaWitness(inputs[offset]);
        SemanticValue[] arguments = new SemanticValue[inputs.Length - offset - 1];
        for (int index = 0; index < arguments.Length; index++) arguments[index] = MetaWitness(inputs[index + offset + 1]);
        try
        {
            SemanticValue value = InvokeType(callee, arguments, source, context);
            if (operation is "invoke_result" or "try_invoke_result") return MetaType(value.Type);
            return MetaBoolean(expected is null || MetaTypes.IsConvertible(Runtime(value), hir.Types[expected.Value.Value].RuntimeType));
        }
        catch (CompileException error)
        {
            if (operation == "try_invoke_result") return MetaNull();
            if (operation != "invoke_result") return MetaBoolean(false);
            throw Error(source, $"Cannot infer the result of this invocation: {error.Diagnostic.Message}", "E0222");
        }
    }

    private SemanticValue MetaWitness(SemanticValue value) => value.DenotedType is { } type ? ValueForType(type) : value;

    private readonly record struct DeclaredSignature(ImmutableArray<HirTypeId> Parameters, HirTypeId? Result,
        bool Variadic);

    private DeclaredSignature? CallableDeclaration(SemanticCallable choice)
    {
        if (choice.Template is { } template)
        {
            var definition = hir.Templates[template.Value];
            return new(definition.Parameters.Select(location =>
            {
                var declaration = hir.Locations[location.Value];
                return declaration.AdmitsNull ? Nullable(declaration.StorageType) : declaration.StorageType;
            }).ToImmutableArray(), definition.ResultAnnotation, false);
        }
        if (choice.NativeName is not { } native) return null;
        var signature = NativeSignatures.Get(native);
        return new(signature.Parameters.Select(hir.Type).ToImmutableArray(), hir.Type(signature.Result), signature.Variadic);
    }

    private MetaQueryResult MetaSignature(string operation, SemanticValue value)
    {
        value = MetaWitness(value);
        if (Runtime(value) is { Nullable: false, Kind: TypeKind.Path or TypeKind.String })
            return operation == "is_variadic" ? MetaBoolean(true) : operation == "parameter_count"
                ? MetaLiteral(TypeRef.Number, new(HirLiteralKind.Number, Number: 0)) : MetaNull();
        ImmutableArray<HirTypeId>? parameters = null;
        bool? variadic = null;
        foreach (var choice in Choices(value))
        {
            if (CallableDeclaration(choice) is not { } declared)
                return operation == "is_variadic" ? MetaBoolean(false) : MetaNull();
            var next = declared.Parameters;
            bool variable = declared.Variadic;
            if (parameters is { } previous)
            {
                if (previous.Length != next.Length || variadic != variable)
                    return operation == "is_variadic" ? MetaBoolean(false) : MetaNull();
                next = previous.Zip(next, (first, second) => Join(ValueForType(first), ValueForType(second)).Type).ToImmutableArray();
            }
            parameters = next;
            variadic = variable;
        }
        if (operation == "is_variadic") return MetaBoolean(variadic == true);
        if (parameters is not { } known) return MetaNull();
        return operation == "parameter_count" ? MetaLiteral(TypeRef.Number, new(HirLiteralKind.Number, Number: known.Length))
            : MetaRange(known.Select(MetaType), hir.Type(TypeRef.Type));
    }

    private IReadOnlyDictionary<string, SemanticValue> MetaMembers(SemanticValue value, SemanticContext context)
    {
        if (Runtime(value).Nullable) return ImmutableDictionary<string, SemanticValue>.Empty;
        if (Runtime(value) == TypeRef.Scope && value.Scope is { } scope)
            return context.Objects.GetValueOrDefault(scope.Region, scope).Fields;
        if (Runtime(value) == TypeRef.Process)
            return ProcessCapabilities.Members.ToDictionary(pair => pair.Key, pair => ValueForType(hir.Type(pair.Value)), StringComparer.Ordinal);
        return ImmutableDictionary<string, SemanticValue>.Empty;
    }

    private SemanticValue MetaLiteral(TypeRef type, HirLiteral literal) => new(hir.Type(type), HirNodeId.None, literal);
    private SemanticValue MetaBoolean(bool value) => MetaLiteral(TypeRef.Bool, new(HirLiteralKind.Bool, Boolean: value));
    private SemanticValue MetaNull() => MetaLiteral(TypeRef.Null, new(HirLiteralKind.Null));
    private SemanticValue MetaType(HirTypeId type) => new(hir.Type(TypeRef.Type), HirNodeId.None,
        new(HirLiteralKind.Type, Type: hir.Types[type.Value].RuntimeType), DenotedType: type);
    private MetaQueryResult MetaRange(IEnumerable<SemanticValue> values, HirTypeId element)
        => MetaRange(values.Select(value => new MetaQueryResult(value)), element);

    private MetaQueryResult MetaRange(IEnumerable<MetaQueryResult> values, HirTypeId element)
    {
        var items = values.ToImmutableArray();
        return new(new(RangeType(element), HirNodeId.None, Element: ValueForType(element), EagerRange: true), items);
    }

    private SemanticValue EmitMetaResult(MetaQueryResult answer, HirSourceRange source, SemanticContext context)
    {
        SemanticValue value = answer.Value;
        if (value.Scope is { } scope) return EmitMetaRecord(answer, scope, source, context);
        if (!context.Publish) return value;
        if (value.Literal is { } literal) return value with { Node = context.Output!.Literal(literal, value.Type, source) };
        HirNodeId[] children = new HirNodeId[answer.Items.Length];
        for (int index = 0; index < children.Length; index++) children[index] = EmitMetaResult(answer.Items[index], source, context).Node;
        int range = hir.Ranges.Count;
        context.Output!.AddRange(new(ImmutableArray.Create(new bool[children.Length])));
        return value with { Node = context.Output!.Add(HirKind.Range, value.Type, source, children, range) };
    }
}
