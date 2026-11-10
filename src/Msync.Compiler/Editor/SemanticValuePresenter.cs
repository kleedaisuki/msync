using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler;

internal readonly record struct NativePresentation(string Signature, string Description);

internal sealed class SemanticValuePresenter(SemanticObservationView view,
    Func<string, NativePresentation?>? nativeMetadata = null)
{
    private enum ValueKind : byte { Value, Type, RuntimeType }
    private readonly record struct ValueKey(ValueKind Kind, int Id, bool Nullable = false, TypeRef? RuntimeType = null);
    private readonly record struct Field(string Name, ValueKey Value, ImmutableArray<ValueKey> Alternatives = default)
    {
        public IEnumerable<ValueKey> Choices => Alternatives.IsDefault ? [Value] : Alternatives;
    }
    private sealed record Fields(ImmutableArray<Field> Ordered, IReadOnlyDictionary<string, Field> Named);

    private readonly Dictionary<(ValueKey Value, string Name), SemanticMember> descriptions = [];
    private readonly Dictionary<ValueKey, Fields?> fields = [];
    private readonly HashSet<ValueKey> expanding = [];
    private readonly HashSet<IReadOnlyList<ValueKey>> expandingGroups = new(new ValueSequenceComparer());

    private sealed class ValueSequenceComparer : IEqualityComparer<IReadOnlyList<ValueKey>>
    {
        public bool Equals(IReadOnlyList<ValueKey>? left, IReadOnlyList<ValueKey>? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right);
        public int GetHashCode(IReadOnlyList<ValueKey> values)
        {
            HashCode hash = new();
            foreach (var value in values) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    public SemanticMember Describe(string name, IEnumerable<SemanticValueId> ids)
    {
        using var iterator = ids.GetEnumerator();
        if (!iterator.MoveNext()) return Unknown(name);
        SemanticValueId first = iterator.Current;
        bool more = iterator.MoveNext();
        if (!more && view.Value(first).Alternatives.IsDefaultOrEmpty)
            return Present(name, new ValueKey(ValueKind.Value, first.Value));
        return DescribeMany(name, first, iterator, more);
    }

    private SemanticMember DescribeMany(string name, SemanticValueId first,
        IEnumerator<SemanticValueId> remaining, bool more)
    {
        List<ValueKey> values = [];
        HashSet<SemanticValueId> visited = [];
        Add(first);
        if (more)
        {
            do { Add(remaining.Current); } while (remaining.MoveNext());
        }
        return values.Count == 0 ? Unknown(name) : Present(name, values);

        void Add(SemanticValueId id)
        {
            if (!visited.Add(id)) return;
            var value = view.Value(id);
            if (!value.Alternatives.IsDefaultOrEmpty)
            {
                foreach (var alternative in value.Alternatives) Add(alternative);
                return;
            }
            values.Add(new(ValueKind.Value, id.Value));
        }
    }

    private SemanticMember Present(string name, IReadOnlyList<ValueKey> values)
    {
        if (values.Count == 1) return Present(name, values[0]);
        SemanticMember[] variants = values.Select(value => Present(name, value)).ToArray();
        bool unknown = variants.Any(item => item.Type == "unknown");
        TypeRef type = values.Select(RuntimeType).Aggregate(TypeRef.Union);
        var first = variants[0];
        SemanticMember result = first with
        {
            Type = unknown ? "unknown" : type.ToString(),
            Signature = string.Join('\n', variants.Select(item => item.Signature).Distinct(StringComparer.Ordinal)),
            Description = unknown ? UnknownDescription : Description(type),
            Members = null,
            Endpoint = variants.All(item => item.Endpoint == first.Endpoint) ? first.Endpoint : null,
            KnownPath = variants.All(item => item.KnownPath == first.KnownPath) ? first.KnownPath : null,
            KnownText = variants.All(item => item.KnownText == first.KnownText) ? first.KnownText : null,
            SemanticRole = variants.All(item => item.SemanticRole == first.SemanticRole) ? first.SemanticRole : "variable"
        };
        if (!expandingGroups.Add(values)) return result;
        try { return result with { Members = CommonFields(values) }; }
        finally { expandingGroups.Remove(values); }
    }

    private SemanticMember Present(string name, ValueKey key)
    {
        if (descriptions.TryGetValue((key, name), out var cached)) return cached;
        TypeRef type = RuntimeType(key);
        SemanticValueView? value = key.Kind == ValueKind.Value ? view.Value(new(key.Id)) : null;
        bool unknown = value?.IsUnknown == true || IsTypeParameter(key);
        NativePresentation? native = value?.NativeName is { } nativeName
            && (value.Signature is null || value.Documentation is null) ? nativeMetadata?.Invoke(nativeName) : null;
        var result = new SemanticMember(name, unknown ? "unknown" : type.ToString(),
            Signature(name, key, value, native), unknown ? UnknownDescription : value?.Documentation ?? native?.Description ?? Description(type),
            Endpoint: Connection(value?.Connection), SemanticRole: Role(value, type, unknown),
            KnownPath: Path(value?.Path), KnownText: value?.Text);
        if (!HasFields(key, type, value))
        {
            descriptions[(key, name)] = result;
            return result;
        }
        ValueKey expansion = key.Kind == ValueKind.Type ? key with { Nullable = false }
            : value is not null && value.Members.IsDefault && view.Module.Types[value.Type.Value].Shape >= 0
                ? new(ValueKind.Type, value.Type.Value) : key;
        if (!expanding.Add(expansion)) return result;
        try { result = result with { Members = CommonFields([key]) }; }
        finally { expanding.Remove(expansion); }
        descriptions[(key, name)] = result;
        return result;
    }

    private bool HasFields(ValueKey key, TypeRef type, SemanticValueView? value) =>
        value is not null && !value.Members.IsDefault || type.WithoutNull == TypeRef.Process
        || key.Kind != ValueKind.RuntimeType && (view.Module.Types[TypeId(key).Value].Shape >= 0
            || !view.Module.Types[TypeId(key).Value].Alternatives.IsDefaultOrEmpty);

    private SemanticMember[]? CommonFields(IReadOnlyList<ValueKey> values)
    {
        ValueKey[] present = values.Where(value => RuntimeType(value) != TypeRef.Null).Distinct().ToArray();
        if (present.Length == 0) return null;
        Fields?[] variants = present.Select(ValueFields).ToArray();
        if (variants.Any(item => item is null)) return null;
        List<SemanticMember> result = [];
        foreach (var field in variants[0]!.Ordered)
        {
            if (variants.Any(item => !item!.Named.ContainsKey(field.Name))) continue;
            ValueKey[] choices = variants.SelectMany(item => item!.Named[field.Name].Choices).Distinct().ToArray();
            result.Add(Present(field.Name, choices));
        }
        return result.ToArray();
    }

    private Fields? ValueFields(ValueKey key)
    {
        if (fields.TryGetValue(key, out var cached)) return cached;
        SemanticValueView? value = key.Kind == ValueKind.Value ? view.Value(new(key.Id)) : null;
        ImmutableArray<Field> members;
        if (value is not null && !value.Members.IsDefault)
            members = value.Members.Select(field => new Field(field.Name, new(ValueKind.Value, field.Value.Value, field.AdmitsNull))).ToImmutableArray();
        else if (RuntimeType(key).WithoutNull == TypeRef.Process)
            members = [
                new("stdout", new(ValueKind.RuntimeType, 0, RuntimeType: TypeRef.String)),
                new("stderr", new(ValueKind.RuntimeType, 0, RuntimeType: TypeRef.String)),
                new("exit_code", new(ValueKind.RuntimeType, 0, RuntimeType: TypeRef.Number)),
                new("success", new(ValueKind.RuntimeType, 0, RuntimeType: TypeRef.Bool))
            ];
        else
        {
            HirType? type = key.Kind == ValueKind.RuntimeType ? null : view.Module.Types[TypeId(key).Value];
            if (type is null) return fields[key] = null;
            if (!type.Alternatives.IsDefaultOrEmpty) return UnionFields(key, type.Alternatives);
            if (type.Shape < 0) return fields[key] = null;
            members = view.Module.ScopeLayouts[type.Shape].Fields.Select(field => new Field(field.Name,
                new(ValueKind.Type, field.StorageType.Value, field.AdmitsNull))).ToImmutableArray();
        }
        return fields[key] = new(members, members.ToDictionary(field => field.Name, StringComparer.Ordinal));
    }

    private Fields? UnionFields(ValueKey key, ImmutableArray<HirTypeId> alternatives)
    {
        fields[key] = null;
        var variants = alternatives.Where(type => view.Module.RuntimeType(type) != TypeRef.Null)
            .Select(type => ValueFields(new(ValueKind.Type, type.Value))).ToArray();
        if (variants.Length == 0 || variants.Any(variant => variant is null)) return null;
        var members = variants[0]!.Ordered.Where(field => variants.All(variant => variant!.Named.ContainsKey(field.Name)))
            .Select(field => new Field(field.Name, default,
                variants.SelectMany(variant => variant!.Named[field.Name].Choices).Distinct().ToImmutableArray()))
            .ToImmutableArray();
        return fields[key] = new(members, members.ToDictionary(field => field.Name, StringComparer.Ordinal));
    }

    private string Signature(string name, ValueKey key, SemanticValueView? value, NativePresentation? native)
    {
        if ((value?.Signature ?? native?.Signature) is { } written) return Rename(name, written);
        if (value?.Function is { } function)
        {
            SemanticFunctionSignature callable = view.Module.Functions[function.Value];
            return FunctionSignature(name.Length == 0 ? callable.Name : name, callable.Parameters, TypeName(callable.Result));
        }
        if (value?.Template is { } template)
        {
            SemanticTemplateSignature callable = view.Module.Templates[template.Value];
            string result = callable.Result is { } inferred ? TypeName(inferred) : "inferred at call";
            return FunctionSignature(name.Length == 0 ? callable.Name : name, callable.Parameters, result);
        }
        if (value?.NativeName is { } nativeName)
        {
            if (!NativeSignatures.TryGet(nativeName, out var signature)) return $"fn {Name(name, nativeName)}(...) -> unknown";
            IEnumerable<string> parameters = signature.Parameters.Select((type, index) => $"arg{index + 1}: {type}");
            if (signature.Variadic) parameters = parameters.Append("...arguments");
            return $"fn {Name(name, nativeName)}({string.Join(", ", parameters)}) -> {signature.Result}";
        }
        if (key.Kind != ValueKind.RuntimeType && RuntimeType(key).WithoutNull == TypeRef.Function
            && view.Module.Types[TypeId(key).Value].Signature is >= 0 and var callableSignature)
        {
            HirSignature signature = view.Module.Signatures[callableSignature];
            IEnumerable<string> parameters = signature.Parameters.Select((type, index) => $"arg{index + 1}: {TypeName(type)}");
            if (signature.Variadic) parameters = parameters.Append("...arguments");
            return $"fn {name}({string.Join(", ", parameters)}) -> {TypeName(signature.Result)}";
        }
        string typeName = value?.IsUnknown == true || IsTypeParameter(key) ? "unknown" : value?.TypeValue is { } target
            ? $"Type ({TypeName(target)})" : key.Kind == ValueKind.RuntimeType || value?.CurrentType is not null
                ? RuntimeType(key).ToString() : TypeName(TypeId(key), key.Nullable);
        return name.Length == 0 ? typeName : $"{name}: {typeName}";
    }

    private string FunctionSignature(string name, ImmutableArray<HirLocationId> parameters, string result)
    {
        var declared = parameters.IsDefault ? ImmutableArray<HirLocationId>.Empty : parameters;
        return $"fn {name}({string.Join(", ", declared.Select(id =>
        {
            HirLocation parameter = view.Module.Locations[id.Value];
            TypeRef type = view.Module.RuntimeType(parameter.StorageType);
            string display = view.Module.TypeName(parameter.StorageType,
                omitNull: parameter.AdmitsNull && type != TypeRef.Null);
            return $"{parameter.Name}{(parameter.AdmitsNull ? "?" : "")}: {display}";
        }))}) -> {result}";
    }

    private TypeRef RuntimeType(ValueKey key)
    {
        TypeRef type = key.Kind == ValueKind.RuntimeType ? key.RuntimeType!
            : key.Kind == ValueKind.Value && view.Value(new(key.Id)).CurrentType is { } current ? current
                : view.Module.RuntimeType(TypeId(key));
        return key.Nullable ? type.WithNull : type;
    }

    private HirTypeId TypeId(ValueKey key) => key.Kind == ValueKind.Type ? new(key.Id) : view.Value(new(key.Id)).Type;
    private bool IsTypeParameter(ValueKey key) => key.Kind != ValueKind.RuntimeType && IsTypeParameter(TypeId(key));
    private bool IsTypeParameter(HirTypeId type) => view.Module.Types[type.Value].TypeParameter >= 0;
    private string TypeName(HirTypeId type, bool nullable = false) => view.Module.TypeName(type, nullable);
    private static string Name(string name, string fallback) => name.Length == 0 ? fallback : name;

    private static string Rename(string name, string signature)
    {
        int start = signature.IndexOf('(');
        if (name.Length == 0 || start < 0) return signature;
        string prefix = signature.StartsWith("native fn ", StringComparison.Ordinal) ? "native fn "
            : signature.StartsWith("fn ", StringComparison.Ordinal) ? "fn " : "";
        return prefix.Length == 0 ? signature : $"{prefix}{name}{signature[start..]}";
    }

    private static string Role(SemanticValueView? value, TypeRef type, bool unknown) => unknown ? "variable"
        : type.WithoutNull == TypeRef.Function ? "function"
        : type == TypeRef.Type || value?.IsNamedScope == true ? "type" : "variable";

    private static SemanticMember Unknown(string name) => new(name, "unknown", name.Length == 0 ? "unknown" : $"{name}: unknown", UnknownDescription);

    internal static ConnectionValue? Connection(SemanticConnectionView? connection) => connection is null ? null : new(connection.Kind, connection.Address);
    private static PathValue? Path(SemanticPathView? path) => path is null ? null
        : new(path.Raw, path.WorkingDirectory, Connection(path.Connection), path.HomeDirectory, path.LocalSpelling);

    private const string UnknownDescription = "The type depends on an incomplete expression or an uninstantiated generic parameter.";

    private static string Description(TypeRef type)
    {
        TypeRef core = type.WithoutNull;
        string description = core == TypeRef.Scope ? "A lexical Scope. Select a declared member with `::`; a Scope is not a physical endpoint."
            : core == TypeRef.Path ? "A filesystem path. Bare unbound names denote Paths. Calling a Path starts a Process."
            : core == TypeRef.Connection ? "A physical endpoint. Select it with `path @ connection`; `::` selects lexical Scope members."
            : core == TypeRef.Function ? "A callable value. Unannotated parameters are specialized from each call's argument types."
            : core == TypeRef.Type ? "A type value. `decltype(expression)` infers without executing the expression."
            : core == TypeRef.Null ? "The absence of a value. Test with `is null` or `is not null`; postfix `?` means `is null`."
            : core == TypeRef.Bool ? "A boolean value: `true` or `false`. Conditions require Bool; Null is not a boolean."
            : core == TypeRef.String ? "A text value. Transferring a String writes its bytes; a Path reads a file."
            : core == TypeRef.Number ? "An IEEE 754 binary64 number. Only explicit `as` converts other scalar types."
            : core == TypeRef.Process ? "A process value. Inspect `stdout`, `stderr`, `exit_code` or `success` with `::`."
            : $"Inferred type: `{type}`. Inspection does not execute this expression.";
        return type.Nullable && type != TypeRef.Null ? description + " Handle Null before using the value." : description;
    }
}
