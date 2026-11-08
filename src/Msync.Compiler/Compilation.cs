using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation(string source, string sourceName, int optimization, Action<SourceSpan, Info, string?>? observe = null)
{
    private readonly List<Value> constants = [];
    private readonly Dictionary<Value, int> constantIndices = [];
    private readonly List<FunctionCode> functions = [];
    private readonly List<Template> templates = [];
    private ImmutableDictionary<string, Binding> sessionEnvironment = ImmutableDictionary<string, Binding>.Empty;
    private int sessionLocals;
    private static readonly Lazy<IReadOnlyDictionary<string, (string Source, Document Document)>> libraries = new(ReadLibraries);
    private int nextTemplate;

    public Module Run() => Submit(source);
    public IReadOnlyCollection<string> BindingNames => sessionEnvironment.Keys.ToArray();

    public Module Submit(string text, Document? parsedDocument = null)
    {
        var document = parsedDocument ?? Parser.Parse(text, sourceName);
        PrepareSessionBindings();
        Frame frame = new(text, sourceName, sessionEnvironment) { Locals = sessionLocals };
        if (functions.Count == 0) functions.Add(new("<main>", 0, 0, [], TypeRef.Null));
        var previousFunctions = functions.ToArray();
        int previousConstants = constants.Count;
        int previousTemplates = templates.Count;
        var previousKeys = templates.Select(template => template.Specializations.Keys.ToHashSet()).ToArray();
        try
        {
            var result = Statements(frame, document.Statements, submittedBindings is not null);
            frame.Emit(OpCode.Return);
            functions[0] = new("<main>", 0, frame.Locals, frame.Code.ToArray(), result.Type);
            sessionEnvironment = frame.Environment;
            sessionLocals = frame.Locals;
            return Optimizer.Optimize(new(constants.ToArray(), functions.ToArray(), OptimizationLevel: optimization), optimization);
        }
        catch
        {
            functions.Clear(); functions.AddRange(previousFunctions);
            for (int i = previousConstants; i < constants.Count; i++) constantIndices.Remove(constants[i]);
            constants.RemoveRange(previousConstants, constants.Count - previousConstants);
            templates.RemoveRange(previousTemplates, templates.Count - previousTemplates);
            for (int i = 0; i < templates.Count; i++)
                foreach (var key in templates[i].Specializations.Keys.Where(key => !previousKeys[i].Contains(key)).ToArray()) templates[i].Specializations.Remove(key);
            throw;
        }
    }

    private static IReadOnlyDictionary<string, (string Source, Document Document)> ReadLibraries()
    {
        Dictionary<string, (string, Document)> result = [];
        var assembly = typeof(Compiler).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".msync", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            var text = reader.ReadToEnd();
            var module = name.Split('.')[^2];
            result[module] = (text, Parser.Parse(text, $"msync-stdlib:///0.8.0/{module}.msync"));
        }
        return result;
    }

    private Info Statements(Frame frame, IReadOnlyList<Statement> statements, bool trackBindings = false)
    {
        Info result = new(TypeRef.Null);
        bool hasValue = false;
        bool terminated = false;
        foreach (var statement in statements)
        {
            if (hasValue) frame.Emit(OpCode.Pop);
            switch (statement)
            {
                case LetStatement let:
                    result = Expression(frame, let.Value);
                    if (let.Pattern is not WildcardPattern && let.Pattern is not NamePattern { Name: "_" })
                    {
                        var asserted = let.Type is null ? null : ResolveType(frame, let.Type);
                        bool nullable = let.Nullable || asserted?.Nullable == true;
                        if (result.Type.Nullable && !nullable)
                            throw Error(frame, "A possibly null value requires an explicit '?' binding or a null-refining branch.", let.Value.Span,
                                label: $"this value may be Null ({result.Type})",
                                help: "Add '?' after the binding name to admit Null, or use 'if value is not null' before binding.",
                                fix: let.Pattern is NamePattern bound ? new("Allow Null in this binding", new(bound.Span.End, 0), "?") : null,
                                code: "E0201");
                        if (asserted is not null)
                        {
                            if (let.Nullable) asserted = asserted.WithNull;
                            CheckConversion(frame, result.Type, asserted, let.Value.Span);
                            Convert(frame, result.Type, asserted, let.Value.Span);
                            result = result with { Type = asserted.Kind == TypeKind.Range && asserted.Element is null ? result.Type : asserted };
                        }
                        else if (let.Nullable) result = result with { Type = result.Type.WithNull };
                    }
                    BindPattern(frame, let.Pattern, result);
                    frame.Emit(OpCode.Null);
                    result = new(TypeRef.Null);
                    hasValue = true;
                    break;
                case FunctionDeclaration declaration:
                    result = Function(frame, declaration.Function);
                    if (declaration.Function.NameSpan is { } functionName) Observe(frame, functionName, result, declaration.Name);
                    Store(frame, declaration.Name, result);
                    frame.Emit(OpCode.Null); result = new(TypeRef.Null); hasValue = true;
                    break;
                case NativeDeclaration native:
                    result = Native(frame, native, null);
                    if (native.NameSpan is { } nativeName) Observe(frame, nativeName, result, native.Name);
                    Store(frame, native.Name, result);
                    frame.Emit(OpCode.Null); result = new(TypeRef.Null); hasValue = true;
                    break;
                case ScopeDeclaration declaration:
                    result = Scope(frame, declaration.Scope);
                    if (declaration.NameSpan is { } scopeName) Observe(frame, scopeName, result, declaration.Name);
                    Store(frame, declaration.Name, result);
                    frame.Emit(OpCode.Null); result = new(TypeRef.Null); hasValue = true;
                    break;
                case ExpressionStatement expression:
                    result = Expression(frame, expression.Expression);
                    if (expression.Discarded && result.Type.Kind != TypeKind.Never) { frame.Emit(OpCode.Pop); frame.Emit(OpCode.Null); result = new(TypeRef.Null); }
                    hasValue = true;
                    break;
                default: throw new InvalidOperationException($"Unknown statement {statement.GetType().Name}.");
            }
            terminated |= result.Type.Kind == TypeKind.Never;
            if (trackBindings) RecordSessionBinding(statement, frame.Environment);
        }
        if (!hasValue) frame.Emit(OpCode.Null);
        return terminated ? new(new(TypeKind.Never)) : result;
    }

    private Info Expression(Frame frame, Expr expression)
    {
        Info result = InferExpression(frame, expression);
        Observe(frame, expression.Span, result, expression is NameExpr name ? name.Name : null);
        return result;
    }

    private Info InferExpression(Frame frame, Expr expression) => expression switch
    {
        BoolExpr value => Emit(frame, value.Value ? OpCode.True : OpCode.False, TypeRef.Bool, expression.Span),
        NullExpr => Emit(frame, OpCode.Null, TypeRef.Null, expression.Span),
        StringExpr value => Constant(frame, new StringValue(value.Value), expression.Span),
        NumberExpr value => Constant(frame, new NumberValue(value.Value), expression.Span),
        PathExpr path => Path(frame, path.Path, expression.Span),
        NameExpr name => Name(frame, name.Name, expression.Span),
        TypeExpr type => TypeConstant(frame, ResolveType(frame, type.Type), expression.Span),
        UnaryExpr unary => Unary(frame, unary),
        BinaryExpr binary => Binary(frame, binary),
        BlockExpr block => Block(frame, block),
        IfExpr conditional => Conditional(frame, conditional),
        MatchExpr match => Match(frame, match),
        FunctionExpr function => Function(frame, function),
        ScopeExpr scope => Scope(frame, scope),
        RangeExpr range => Range(frame, range),
        CallExpr call => Call(frame, call),
        ForExpr loop => For(frame, loop),
        LoopExpr loop => Loop(frame, loop),
        ReturnExpr returned => Return(frame, returned),
        BreakExpr broken => Break(frame, broken),
        _ => throw new InvalidOperationException($"Unknown expression {expression.GetType().Name}.")
    };


}
