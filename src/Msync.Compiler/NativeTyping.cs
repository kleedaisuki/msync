using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info CastResult(Frame frame, IReadOnlyList<Info> arguments, SourceSpan span, IReadOnlyList<Expr>? expressions)
    {
        Info source = arguments[0];
        TypeRef target = arguments[1].TypeValue ?? throw Error(frame,
            "The cast target must be a statically inferred Type value.", expressions?[1].Span ?? span,
            help: "Use a built-in type such as Path or String, or decltype(expression).", code: "E0220");
        if (!ExplicitConversions.Possible(source.Type, target))
            throw Error(frame, $"Cannot cast {source.Type} to {target}.", expressions?[0].Span ?? span,
                help: source.Type.Nullable && !target.Nullable
                    ? "Handle Null before casting, or use a target type that explicitly admits Null."
                    : "Choose a compatible concrete target type. Casts do not turn arbitrary objects into another object kind.", code: "E0220");
        if (frame.DeferredCaptures && arguments[1].Storage is { } storage)
            RecordCastTarget(frame, storage, source.Type, target, expressions?[1].Span ?? span);
        if (target.WithoutNull.Kind == TypeKind.Range && source.Type.WithoutNull.Kind == TypeKind.Range)
        {
            TypeRef? element = target.Element ?? source.Type.Element;
            Info? info = element is null ? source.Element : source.Element is { } existing && element.Accepts(existing.Type)
                ? Retype(existing, element) : new Info(element);
            return new(target with { Element = element }, Element: info);
        }
        if (target.Accepts(source.Type)) return Retype(source, target);
        return new(target, KnownConnection: target.WithoutNull == TypeRef.Connection && source.KnownText is { } endpoint
                ? ExplicitConversions.ConnectionFromText(endpoint) : null,
            KnownPath: target.WithoutNull == TypeRef.Path && source.KnownText is { } path ? new PathValue(path) : null);
    }

    private static Info RangeItem(Info range) => range.Element ?? new(range.Type.Element ?? TypeRef.Any);

    private static Info RangeResult(Info element) => new(new(TypeKind.Range, element.Type), Element: element);

    private static Info RecordResult(params (string Name, Info Value)[] fields)
    {
        var members = fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        return new(TypeRef.Scope, Scope: new(members, NullableMembers: fields.Where(field => field.Value.Type.Nullable)
            .Select(field => field.Name).ToHashSet(StringComparer.Ordinal))
        {
            Storage = members.ToDictionary(field => field.Key, field => new StorageConstraint(field.Value.Type), StringComparer.Ordinal)
        });
    }

    private static void ValidateNativeRange(Frame frame, NativeSignature signature, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        if (signature.Name != "ranges.sort") return;
        TypeRef element = RangeItem(arguments[0]).Type;
        var text = new TypeRef(TypeKind.Path | TypeKind.String);
        if (element == TypeRef.Bool || element == TypeRef.Number || text.Accepts(element)) return;
        throw Error(frame, $"Natural sorting requires Bool, Number, or compatible Path/String items, not {element}.", span,
            help: "Use sort_with and an explicit comparator for records, Null, or mixed scalar domains.", code: "E0221");
    }

    private Info ExtendedNativeResult(Frame frame, NativeSignature signature, IReadOnlyList<Info> arguments,
        Info? callbackResult, SourceSpan span, IReadOnlyList<Expr>? expressions) => signature.ResultRule switch
        {
            NativeResultRule.CastTarget => CastResult(frame, arguments, span, expressions),
            NativeResultRule.RangeConcat => RangeResult(Join(RangeItem(arguments[0]), RangeItem(arguments[1]))),
            NativeResultRule.RangeFlatten => FlattenResult(frame, arguments[0], expressions?[0].Span ?? span),
            NativeResultRule.RangeZip => RangeResult(RecordResult(("first", RangeItem(arguments[0])), ("second", RangeItem(arguments[1])))),
            NativeResultRule.RangeEnumerate => RangeResult(RecordResult(("index", new(TypeRef.Number)), ("value", RangeItem(arguments[0])))),
            NativeResultRule.RangeNested => RangeResult(arguments[0]),
            NativeResultRule.RangeRepeat => RangeResult(arguments[0]),
            NativeResultRule.RangeScan when callbackResult is not null => RangeResult(callbackResult),
            _ => LibraryNativeResult(signature)
        };

    private static Info LibraryNativeResult(NativeSignature signature)
    {
        if (signature.Name == "environment.variables")
            return RangeResult(RecordResult(("name", new(TypeRef.String)), ("value", new(TypeRef.String))));
        if (signature.Name is not ("regex.find" or "regex.matches")) return new(signature.Result);
        Info group = RecordResult(("name", new(TypeRef.String)), ("value", new(TypeRef.String)),
            ("index", new(TypeRef.Number)), ("length", new(TypeRef.Number)), ("success", new(TypeRef.Bool)));
        Info match = RecordResult(("value", new(TypeRef.String)), ("index", new(TypeRef.Number)),
            ("length", new(TypeRef.Number)), ("groups", RangeResult(group)));
        return signature.Name == "regex.matches" ? RangeResult(match) : match with { Type = TypeRef.Scope.WithNull };
    }

    private static Info FlattenResult(Frame frame, Info source, SourceSpan span)
    {
        var range = RangeItem(source);
        Require(frame, range.Type, new(TypeKind.Range), span);
        return RangeResult(RangeItem(range));
    }

    private Info NativeCallback(Frame frame, NativeSignature signature, IReadOnlyList<Info> arguments,
        SourceSpan span, IReadOnlyList<Expr>? expressions)
    {
        int index = arguments.Count - 1;
        Info item = RangeItem(arguments[0]);
        if (signature.ResultRule == NativeResultRule.RangeScan)
            return ScanCallback(frame, arguments[index], arguments[1], item, span);
        IReadOnlyList<Info> inputs = signature.Name == "ranges.sort_with" ? [item, item] : [item];
        var callback = ResolveDispatch(frame, arguments[index], inputs, span);
        if (callback.Mapping.Length != 0) frame.Emit(OpCode.SpecializeClosure, text: callback.Mapping, span: span);
        if (signature.Name is "ranges.filter" or "ranges.take_while" or "ranges.drop_while" or "ranges.any" or "ranges.any_of"
            or "ranges.all_of" or "ranges.none" or "ranges.none_of" or "ranges.find_if" or "ranges.sort_with")
            Require(frame, callback.Result.Type, TypeRef.Bool, expressions?[index].Span ?? span);
        return callback.Result;
    }

    private Info ScanCallback(Frame frame, Info operation, Info initial, Info item, SourceSpan span)
    {
        Info domain = initial;
        Info? output = null;
        TypeRef? constraint = initial.Type.WithoutNull.Kind == TypeKind.Never ? null : initial.Type;
        while (true)
        {
            Frame scratch = CloneFrame(frame);
            var callback = ResolveDispatch(scratch, operation, [domain, item], span);
            if (constraint is null && callback.Result.Type.WithoutNull.Kind != TypeKind.Never)
                constraint = initial.Type.Nullable ? callback.Result.Type.WithNull : callback.Result.Type;
            if (constraint is not null) Require(frame, callback.Result.Type, constraint, span);
            var next = Join(domain, callback.Result);
            output = output is null ? callback.Result : Join(output, callback.Result);
            if (SameNativeSchema(domain, next))
            {
                var final = ResolveDispatch(frame, operation, [domain, item], span);
                if (final.Mapping.Length != 0) frame.Emit(OpCode.SpecializeClosure, text: final.Mapping, span: span);
                return Join(output, final.Result);
            }
            domain = next;
        }
    }

    private static bool SameNativeSchema(Info left, Info right)
    {
        HashSet<(ScopeInfo, ScopeInfo)> scopes = [];
        bool Same(Info first, Info second)
        {
            if (first.Type != second.Type || first.TypeValue != second.TypeValue) return false;
            if (first.Type.WithoutNull == TypeRef.Function)
            {
                var a = CallableChoices(first).Select(choice => (choice.Function?.Id, choice.NativeName)).ToHashSet();
                if (!a.SetEquals(CallableChoices(second).Select(choice => (choice.Function?.Id, choice.NativeName)))) return false;
            }
            if (first.Scope is { } x && second.Scope is { } y)
            {
                if (!scopes.Add((x, y))) return true;
                if (x.Members.Count != y.Members.Count) return false;
                foreach (var (name, field) in x.Members)
                    if (!y.Members.TryGetValue(name, out var other) || !Same(field, other)) return false;
            }
            else if (first.Scope is not null || second.Scope is not null) return false;
            return first.Element is null || second.Element is null ? first.Element == second.Element : Same(first.Element, second.Element);
        }
        return Same(left, right);
    }
}
