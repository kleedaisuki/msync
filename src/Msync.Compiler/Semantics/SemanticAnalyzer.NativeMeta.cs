using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private static bool IsDependentMeta(string? name) => name is
        "meta.is_invocable" or "meta.is_invocable_r" or "meta.invoke_result" or
        "meta.parameter_count" or "meta.parameter_types" or "meta.is_variadic" or
        "meta.has_member" or "meta.member_type" or "meta.member_names" or "meta.member_count";

    private bool TryMetaCall(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context, out SemanticValue result)
    {
        result = null!;
        string? name = hir.Calls[node.Data].NativeName;
        if (name is null)
        {
            foreach (var choice in BoundCallables(children[0], context))
            {
                if (!IsDependentMeta(choice.NativeName)) return false;
                if (name is not null && name != choice.NativeName) return false;
                name = choice.NativeName;
            }
        }
        if (!IsDependentMeta(name)) return false;
        NativeSignature signature = NativeSignatures.Get(name!);
        int count = children.Length - 1;
        if (count < signature.Parameters.Count || !signature.Variadic && count != signature.Parameters.Count)
            throw Error(node.Source, $"{name![5..]} expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments.", "E0222");
        SemanticContext query = context.QueryFork();
        SemanticValue[] arguments = new SemanticValue[count];
        for (int index = 0; index < count; index++) arguments[index] = Analyze(children[index + 1], query);
        result = DependentMetaResult(name![5..], arguments, node.Source, query);
        result = EmitMetaResult(result, node.Source, context);
        return true;
    }

    private SemanticValue DependentMetaResult(string operation, SemanticValue[] arguments,
        HirSourceRange source, SemanticContext context)
    {
        if (operation is "is_invocable" or "is_invocable_r" or "invoke_result")
            return MetaInvocation(operation, arguments, source, context);
        if (operation is "parameter_count" or "parameter_types" or "is_variadic")
            return MetaSignature(operation, arguments[0]);
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
            if (operation == "invoke_result") return MetaType(value.Type);
            return MetaBoolean(expected is null || MetaTypes.IsConvertible(Runtime(value), hir.Types[expected.Value.Value].RuntimeType));
        }
        catch (CompileException error)
        {
            if (operation != "invoke_result") return MetaBoolean(false);
            throw Error(source, $"Cannot infer the result of this invocation: {error.Diagnostic.Message}", "E0222");
        }
    }

    private SemanticValue MetaWitness(SemanticValue value) => value.DenotedType is { } type ? ValueForType(type) : value;

    private SemanticValue MetaSignature(string operation, SemanticValue value)
    {
        value = MetaWitness(value);
        if (Runtime(value) is { Nullable: false, Kind: TypeKind.Path or TypeKind.String })
            return operation == "is_variadic" ? MetaBoolean(true) : operation == "parameter_count"
                ? MetaLiteral(TypeRef.Number, new(HirLiteralKind.Number, Number: 0)) : MetaNull();
        ImmutableArray<HirTypeId>? parameters = null;
        bool? variadic = null;
        foreach (var choice in Choices(value))
        {
            ImmutableArray<HirTypeId> next;
            bool variable;
            if (choice.Template is { } template)
            {
                next = hir.Templates[template.Value].Parameters.Select(location =>
                {
                    var declaration = hir.Locations[location.Value];
                    return declaration.AdmitsNull ? Nullable(declaration.StorageType) : declaration.StorageType;
                }).ToImmutableArray();
                variable = false;
            }
            else if (choice.NativeName is { } native)
            {
                var signature = NativeSignatures.Get(native);
                next = signature.Parameters.Select(hir.Type).ToImmutableArray();
                variable = signature.Variadic;
            }
            else return operation == "is_variadic" ? MetaBoolean(false) : MetaNull();
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
    private SemanticValue MetaRange(IEnumerable<SemanticValue> values, HirTypeId element)
    {
        var items = values.ToImmutableArray();
        return new(RangeType(element), HirNodeId.None, Element: ValueForType(element), EagerRange: true, Alternatives: items);
    }

    private SemanticValue EmitMetaResult(SemanticValue value, HirSourceRange source, SemanticContext context)
    {
        if (!context.Publish) return value;
        if (value.Literal is { } literal) return value with { Node = context.Output!.Literal(literal, value.Type, source) };
        HirNodeId[] children = new HirNodeId[value.Alternatives.Length];
        for (int index = 0; index < children.Length; index++) children[index] = EmitMetaResult(value.Alternatives[index], source, context).Node;
        return value with { Node = context.Output!.Add(HirKind.Range, value.Type, source, children) };
    }
}
