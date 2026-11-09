using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private static bool IsMetaQuery(string? identity) => identity is
        "meta.is_invocable" or "meta.is_invocable_r" or "meta.invoke_result" or
        "meta.parameter_count" or "meta.parameter_types" or "meta.is_variadic" or
        "meta.has_member" or "meta.member_type" or "meta.member_names" or "meta.member_count";

    private bool TryMetaQuery(Frame frame, Info callee, CallExpr call, out Info result)
    {
        result = null!;
        if (!IsMetaQuery(callee.NativeName)) return false;
        string operation = callee.NativeName![5..];
        var signature = NativeSignatures.Get(callee.NativeName);
        if (call.Arguments.Count < signature.Parameters.Count ||
            !signature.Variadic && call.Arguments.Count != signature.Parameters.Count)
            throw Error(frame, $"{operation} expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments.",
                call.Span, help: "Metadata queries inspect expressions without executing them.", code: "E0222");

        frame.Emit(OpCode.Pop);
        var (probe, scratch) = ForkMetadata(frame);
        var inputs = call.Arguments.Select(expression => probe.Expression(scratch, expression)).ToArray();
        Value? answer = operation switch
        {
            "is_invocable" or "is_invocable_r" or "invoke_result" =>
                probe.MetaInvocation(scratch, operation, inputs, call.Span),
            "has_member" or "member_type" or "member_count" =>
                MetaMember(frame, operation, inputs, call.Span),
            "is_variadic" or "parameter_count" => MetaArity(operation, inputs[0]),
            _ => null
        };
        if (operation == "member_names")
            result = EmitMetaRange(frame, MetaMembers(inputs[0]).Keys.Order(StringComparer.Ordinal)
                .Select(name => (Value)new StringValue(name)), TypeRef.String, call.Span);
        else if (operation == "parameter_types")
        {
            var parameters = probe.MetaParameters(scratch, inputs[0]);
            result = parameters is null ? Emit(frame, OpCode.Null, TypeRef.Null, call.Span)
                : EmitMetaRange(frame, parameters.Select(type => (Value)new TypeValue(type)), TypeRef.Type, call.Span);
        }
        else result = EmitMetaValue(frame, answer!, call.Span);
        return true;
    }

    private (Compilation Compilation, Frame Frame) ForkMetadata(Frame frame)
    {
        EditorSnapshot snapshot = new(copySpecializations: false);
        var environment = frame.Environment.ToImmutableDictionary(pair => pair.Key,
            pair => snapshot.Binding(pair.Value with { Info = Current(frame, pair.Value) }, null), StringComparer.Ordinal);
        Compilation probe = new(frame.Source, frame.SourceName, 0);
        probe.constants.AddRange(constants);
        foreach (var pair in constantIndices) probe.constantIndices.Add(pair.Key, pair.Value);
        probe.functions.AddRange(functions);
        Frame scratch = new(frame.Source, frame.SourceName, environment)
        {
            Concepts = frame.Concepts,
            Locals = frame.Locals,
            Facts = frame.Facts.ToImmutableDictionary(pair => pair.Key, pair => snapshot.CopyInfo(pair.Value)),
            DeferredCaptures = frame.DeferredCaptures
        };
        return (probe, scratch);
    }

    private Value MetaInvocation(Frame frame, string operation, IReadOnlyList<Info> inputs, SourceSpan span)
    {
        int offset = operation == "is_invocable_r" ? 1 : 0;
        TypeRef? expected = offset == 0 ? null : inputs[0].TypeValue ?? throw Error(frame,
            "is_invocable_r requires a statically inferred result Type.", span,
            help: "Pass a built-in Type or a Type returned by a metadata query.", code: "E0222");
        Info callee = inputs[offset];
        if (callee.TypeValue is { } denoted) callee = new(denoted);
        var arguments = inputs.Skip(offset + 1).Select(MetaWitness).ToArray();
        try
        {
            Info invoked = Invoke(frame, callee with { Storage = null }, arguments, span);
            if (operation == "invoke_result") return new TypeValue(invoked.Type);
            return MetaBool(expected is null || MetaTypes.IsConvertible(invoked.Type, expected));
        }
        catch (CompileException error)
        {
            if (operation != "invoke_result") return Value.False;
            throw Error(frame, "Cannot infer the result of this invocation.", span,
                help: error.Diagnostic.Message, code: "E0222");
        }
    }

    private static Info MetaWitness(Info info) => info.TypeValue is { } type
        ? new(type, Element: type.Element is { } element ? new(element) : null) : info;

    private static IReadOnlyDictionary<string, Info> MetaMembers(Info receiver)
    {
        if (receiver.Type.Nullable) return ImmutableDictionary<string, Info>.Empty;
        if (receiver.Type == TypeRef.Scope && receiver.Scope is { } scope) return scope.Members;
        if (receiver.Type == TypeRef.Process) return ProcessCapabilities.Members.ToDictionary(pair => pair.Key, pair => new Info(pair.Value));
        return ImmutableDictionary<string, Info>.Empty;
    }

    private static Value MetaMember(Frame frame, string operation, IReadOnlyList<Info> inputs, SourceSpan span)
    {
        var members = MetaMembers(inputs[0]);
        if (operation == "member_count") return new NumberValue(members.Count);
        string name = inputs[1].KnownText ?? throw Error(frame,
            "A member query requires a statically inferred String name.", span,
            help: "Pass a literal member name, such as \"compare\".", code: "E0222");
        if (operation == "has_member") return MetaBool(members.ContainsKey(name));
        return members.TryGetValue(name, out var member) ? new TypeValue(member.Type) : Value.Null;
    }

    private static (int Count, bool Variadic)? MetaArityInfo(Info callee)
    {
        if (callee.TypeValue is { } type) callee = new(type);
        if (callee.Type == TypeRef.Path || callee.Type == TypeRef.String) return (0, true);
        if (callee.Type != TypeRef.Function) return null;
        (int Count, bool Variadic)? result = null;
        foreach (var choice in CallableChoices(callee))
        {
            (int Count, bool Variadic)? next = choice.Function is { } function
                ? (function.Syntax.Parameters.Count, false)
                : choice.NativeName is { } native ? (NativeSignatures.Get(native).Parameters.Count, NativeSignatures.Get(native).Variadic) : null;
            if (next is null || result is not null && result != next) return null;
            result = next;
        }
        return result;
    }

    private static Value MetaArity(string operation, Info callee)
    {
        var arity = MetaArityInfo(callee);
        return operation == "is_variadic" ? MetaBool(arity?.Variadic == true)
            : arity is { } known ? new NumberValue(known.Count) : Value.Null;
    }

    private IReadOnlyList<TypeRef>? MetaParameters(Frame frame, Info callee)
    {
        if (callee.Type != TypeRef.Function) return null;
        TypeRef[]? result = null;
        foreach (var choice in CallableChoices(callee))
        {
            TypeRef[] next;
            if (choice.Function is { } function)
            {
                Frame lexical = Frame.Fork(frame);
                lexical.Concepts = function.Concepts;
                lexical.Environment = function.Captures.ToImmutableDictionary(StringComparer.Ordinal);
                next = function.Syntax.Parameters.Select(parameter => parameter.Type is null ? TypeRef.Any
                    : parameter.Nullable ? ResolveType(lexical, parameter.Type).WithNull : ResolveType(lexical, parameter.Type)).ToArray();
            }
            else if (choice.NativeName is { } native) next = NativeSignatures.Get(native).Parameters.ToArray();
            else return null;
            if (result is not null && result.Length != next.Length) return null;
            result = result is null ? next : result.Zip(next, TypeRef.Union).ToArray();
        }
        return result;
    }

    private bool TryFoldMetaTypes(Frame frame, string identity, IReadOnlyList<Info> arguments, SourceSpan span, out Info result)
    {
        result = null!;
        if (!identity.StartsWith("meta.", StringComparison.Ordinal)) return false;
        if (IsMetaQuery(identity)) throw Error(frame,
            "Dependent metadata queries require a direct, statically known invocation.", span,
            help: "Wrap the query in an fn with explicit arguments instead of passing it as a native callback.", code: "E0222");
        if (arguments.Any(argument => argument.TypeValue is null)) return false;
        Value value = MetaTypes.Evaluate(identity[5..], arguments.Select(argument => argument.TypeValue!).ToArray());
        if (value is RangeValue) return false;
        for (int index = 0; index <= arguments.Count; index++) frame.Emit(OpCode.Pop);
        result = EmitMetaValue(frame, value, span);
        return true;
    }

    private Info EmitMetaValue(Frame frame, Value value, SourceSpan span) => value switch
    {
        TypeValue type => TypeConstant(frame, type.Data, span),
        BoolValue boolean => Emit(frame, boolean.Data ? OpCode.True : OpCode.False, TypeRef.Bool, span),
        NullValue => Emit(frame, OpCode.Null, TypeRef.Null, span),
        _ => Constant(frame, value, span)
    };

    private Info EmitMetaRange(Frame frame, IEnumerable<Value> values, TypeRef element, SourceSpan span)
    {
        int count = 0;
        foreach (Value value in values) { EmitMetaValue(frame, value, span); count++; }
        frame.Emit(OpCode.MakeRange, count, span: span);
        return new(new(TypeKind.Range, element), Element: new(element), EagerRange: true);
    }

    private static Value MetaBool(bool value) => value ? Value.True : Value.False;
}
