using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private void Store(Frame frame, string name, Info value, SourceSpan? nameSpan = null,
        bool nullable = false, TypeRef? constraint = null)
    {
        int slot = frame.Allocate(); frame.Emit(OpCode.InitializeLocal, slot);
        var origin = DeclarationOrigin(frame, nameSpan);
        Binding binding = new(slot, value, Origin: origin, AdmitsNull: nullable, Constraint: constraint ?? StorageType(value.Type, nullable));
        if (nameSpan is { } declaration)
        {
            var key = (declaration.Start, name);
            if (frame.Declarations.TryGetValue(key, out var previous)) binding = binding with { Identity = previous.Identity, Storage = previous.Storage };
            else frame.Declarations.Add(key, binding);
            binding.Storage.IsUnwritten = frame.UnwrittenLocations.Contains(declaration.Start);
        }
        frame.Bind(name, binding);
        if (origin is not null) CaptureDefinition(frame, origin.Span, [origin]);
    }
    private void BindPattern(Frame frame, Pattern pattern, Info value, bool nullable = false, TypeRef? constraint = null)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern { Name: "_" }: frame.Emit(OpCode.Pop); break;
            case NamePattern name: Observe(frame, name.Span, value, name.Name); Store(frame, name.Name, value, name.Span, nullable, constraint ?? StorageType(value.Type, nullable)); break;
            case TuplePattern tuple:
                Require(frame, value.Type, new(TypeKind.Range), pattern.Span);
                Constant(frame, new NumberValue(tuple.Items.Count), pattern.Span);
                frame.Emit(OpCode.NativeCall, 2, text: "ranges.take", span: pattern.Span);
                frame.Emit(OpCode.NativeCall, 1, text: "ranges.materialize", span: pattern.Span);
                int sourceSlot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, sourceSlot);
                for (int index = 0; index < tuple.Items.Count; index++)
                {
                    frame.Emit(OpCode.LoadLocal, sourceSlot); Constant(frame, new NumberValue(index), tuple.Items[index].Span);
                    frame.Emit(OpCode.NativeCall, 2, text: "ranges.at", span: tuple.Items[index].Span);
                    BindPattern(frame, tuple.Items[index], value.Element ?? new(value.Type.Element ?? TypeRef.Any));
                }
                break;
            default: throw Error(frame, "let/for requires a binding name, '_' or a destructuring pattern.", pattern.Span);
        }
    }
    private TypeRef ResolveType(Frame frame, TypeSyntax syntax)
    {
        if (TryConcept(frame, syntax.Name, out _))
        {
            CaptureConceptDefinition(frame, syntax);
            return ConceptType(frame, syntax);
        }
        if (!LanguageTypes.TryResolve(syntax.Name, out TypeRef type))
        {
            bool shadowed = syntax.Name.StartsWith(StandardCachePrefix, StringComparison.Ordinal)
                && frame.Environment.ContainsKey("std");
            var error = Error(frame, $"Unknown type '{syntax.Name}'.", syntax.Span,
                label: "unknown type or concept",
                help: shadowed ? "The lexical 'std' binding shadows the builtin standard namespace. Rename that binding to use standard concept declarations."
                    : $"Use a declared concept, a qualified standard concept, or {string.Join(", ", LanguageTypes.Names.Select(name => name == "Range" ? "Range<T>" : name))}.", code: "E0202");
            if (shadowed && frame.Environment["std"].Origin is { } origin)
                error.Diagnostic.Related.Add(new(origin.SourceName, origin.Source, origin.Span, "this binding shadows the standard namespace"));
            throw error;
        }
        if (syntax.Name == "Range" && syntax.Arguments.Count != 0) type = new(TypeKind.Range, ResolveType(frame, syntax.Arguments[0]));
        foreach (var alternative in syntax.Alternatives) type = TypeRef.Union(type, ResolveType(frame, alternative));
        return syntax.Nullable ? type.WithNull : type;
    }
    private static Info Join(Info left, Info right)
    {
        if (ReferenceEquals(left, right)) return left;
        if (left.Type.Kind == TypeKind.Never) return right;
        if (right.Type.Kind == TypeKind.Never) return left;
        var payload = left.Type.Kind == TypeKind.Null ? right : right.Type.Kind == TypeKind.Null ? left : null;
        ScopeInfo? scope = payload?.Scope;
        if (payload is null && left.Scope is not null && right.Scope is not null)
        {
            var members = left.Scope.Members.Where(member => right.Scope.Members.ContainsKey(member.Key))
                .ToDictionary(member => member.Key, member => Join(member.Value, right.Scope.Members[member.Key]));
            scope = new(members, left.Scope.Named && right.Scope.Named, JoinOrigins(left.Scope, right.Scope, members.Keys),
                left.Scope.NullableMembers, left.Scope.StorageTypes)
            {
                Identity = ReferenceEquals(left.Scope.Identity, right.Scope.Identity) ? left.Scope.Identity : new(),
                Memory = ReferenceEquals(left.Scope.Identity, right.Scope.Identity) ? left.Scope.Memory : new(),
                Sources = ReferenceEquals(left.Scope.Identity, right.Scope.Identity) ? left.Scope.Sources : [left.Scope, right.Scope],
                FieldBindings = ReferenceEquals(left.Scope.Identity, right.Scope.Identity) ? left.Scope.FieldBindings : new Dictionary<string, Binding>(),
                Storage = ReferenceEquals(left.Scope.Identity, right.Scope.Identity) ? left.Scope.Storage :
                    members.ToDictionary(pair => pair.Key, pair => new StorageConstraint(StorageType(pair.Value.Type, pair.Value.Type.Nullable)))
            };
        }
        IReadOnlyList<Info>? alternatives = payload?.Alternatives;
        if (left.Type.WithoutNull == TypeRef.Function && right.Type.WithoutNull == TypeRef.Function &&
            (left.Function != right.Function || left.NativeName != right.NativeName || left.Alternatives is not null || right.Alternatives is not null))
            alternatives = CallableChoices(left).Concat(CallableChoices(right)).DistinctBy(InfoKey).ToArray();
        return new(TypeRef.Union(left.Type, right.Type),
            payload is not null ? payload.Function : left.Function == right.Function ? left.Function : null,
            scope, left.TypeValue == right.TypeValue ? left.TypeValue : null,
            payload is not null ? payload.NativeName : left.NativeName == right.NativeName ? left.NativeName : null, alternatives,
            left.Element is null || right.Element is null ? left.Element ?? right.Element : Join(left.Element, right.Element),
            left.KnownBool == right.KnownBool ? left.KnownBool : null,
            left.KnownConnection == right.KnownConnection ? left.KnownConnection : null,
            left.EditorUnknown || right.EditorUnknown,
            left.KnownPath == right.KnownPath ? left.KnownPath : null,
            left.KnownText == right.KnownText ? left.KnownText : null,
            EagerRange: left.EagerRange && right.EagerRange,
            Indexed: ReferenceEquals(left.Indexed, right.Indexed) ? left.Indexed : null,
            MemoryVersion: ReferenceEquals(left.MemoryVersion, right.MemoryVersion) ? left.MemoryVersion
                : left.MemoryVersion is null && right.MemoryVersion is null ? null : new object());
    }
    private Info Retype(Info info, TypeRef target)
    {
        if (target.Accepts(info.Type)) return info with { Type = target };
        return (target.Kind & ~TypeKind.Null) switch
        {
            TypeKind.Path => info with { Type = target, KnownPath = observe is null ? null : LiteralPath(info), KnownText = null },
            TypeKind.String => info with { Type = target, KnownPath = null, KnownText = info.KnownText ?? info.KnownPath?.NativeText },
            _ => info with { Type = target }
        };
    }
    private static void Convert(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (expected.Accepts(actual)) return;
        var target = expected.Kind & ~TypeKind.Null;
        if (target is TypeKind.Path or TypeKind.String) frame.Emit(OpCode.Convert, text: target.ToString(), span: span);
    }
    private void CheckConversion(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (!MetaTypes.IsConvertible(actual, expected)) throw TypeError(frame, actual, expected, span);
    }
    private static void Require(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (!expected.Accepts(actual)) throw TypeError(frame, actual, expected, span);
    }
    private static CompileException TypeError(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        string help = actual.Nullable && !expected.Nullable
            ? "Handle the Null case first: use 'if value is not null { ... }' or match both null and the remaining cases."
            : expected == TypeRef.Bool
                ? "Conditions and boolean operators require Bool. Use 'exists path' for a filesystem test, or 'value is null' for a Null test."
                : expected == TypeRef.Function
                    ? "Call an fn value. A Scope groups members; select its function with 'scope::operation(...)'."
                    : expected.Kind == TypeKind.Range
                        ? "Iterate a Range, such as '[first, second]' or 'directory/<pattern>'. A single Path is not a collection."
                        : actual == TypeRef.Connection && PathCompatible.Accepts(expected)
                            ? "Use a Path or String value. A Connection selects an endpoint; qualify a path with 'path @ connection'."
                            : ExplicitConversions.Possible(actual, expected)
                                ? $"Convert explicitly with 'value as {expected}'. Only Path and String convert implicitly; declared storage types do not change on assignment."
                                : $"Supply a {expected} value here. Declared storage types do not change on assignment; use a new binding for a different type.";
        return Error(frame, $"Expected {expected}, found {actual}.", span,
            label: $"this expression has type {actual}", help: help, code: "E0203");
    }
    private static CompileException Error(Frame frame, string message, SourceSpan span,
        string? label = null, string? help = null, string? note = null, DiagnosticFix? fix = null, string code = "E0200") =>
        new(message, frame.Source, frame.SourceName, span, label, help, note, fix, code);
}
