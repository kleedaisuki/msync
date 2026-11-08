using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation(string source, string sourceName, int optimization)
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

    public Module Submit(string text)
    {
        var document = Parser.Parse(text, sourceName);
        Frame frame = new(text, sourceName, sessionEnvironment) { Locals = sessionLocals };
        if (functions.Count == 0) functions.Add(new("<main>", 0, 0, [], TypeRef.Null));
        var previousFunctions = functions.ToArray();
        int previousConstants = constants.Count;
        int previousTemplates = templates.Count;
        var previousKeys = templates.Select(template => template.Specializations.Keys.ToHashSet()).ToArray();
        try
        {
            var result = Statements(frame, document.Statements);
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

    private Info Statements(Frame frame, IReadOnlyList<Statement> statements)
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
                            throw Error(frame, "A possibly null value requires an explicit '?' binding or a null-refining branch.", let.Value.Span);
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
                    Store(frame, declaration.Name, result);
                    frame.Emit(OpCode.Null); result = new(TypeRef.Null); hasValue = true;
                    break;
                case NativeDeclaration native:
                    result = Native(frame, native, null);
                    Store(frame, native.Name, result);
                    frame.Emit(OpCode.Null); result = new(TypeRef.Null); hasValue = true;
                    break;
                case ScopeDeclaration declaration:
                    result = Scope(frame, declaration.Scope);
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
        }
        if (!hasValue) frame.Emit(OpCode.Null);
        return terminated ? new(new(TypeKind.Never)) : result;
    }

    private Info Expression(Frame frame, Expr expression) => expression switch
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
