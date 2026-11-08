using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private void Store(Frame frame, string name, Info value)
    {
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot); frame.Bind(name, new(slot, value));
    }
    private void BindPattern(Frame frame, Pattern pattern, Info value)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern { Name: "_" }: frame.Emit(OpCode.Pop); break;
            case NamePattern name: Observe(frame, name.Span, value, name.Name); Store(frame, name.Name, value); break;
            case TuplePattern tuple:
                Require(frame, value.Type, new(TypeKind.Range), pattern.Span);
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
        TypeRef type = syntax.Name switch
        {
            "Bool" => TypeRef.Bool,
            "String" => TypeRef.String,
            "Path" => TypeRef.Path,
            "Function" => TypeRef.Function,
            "Scope" => TypeRef.Scope,
            "Connection" => TypeRef.Connection,
            "Process" => TypeRef.Process,
            "Null" => TypeRef.Null,
            "Number" => TypeRef.Number,
            "Type" => TypeRef.Type,
            "Range" => new(TypeKind.Range, syntax.Arguments.Count == 0 ? null : ResolveType(frame, syntax.Arguments[0])),
            "Any" => TypeRef.Any,
            _ => throw Error(frame, $"Unknown type '{syntax.Name}'.", syntax.Span,
                label: "not a built-in type",
                help: "Use Bool, String, Path, Function, Scope, Connection, Process, Null, Number, Type or Range<T>.", code: "E0202")
        };
        foreach (var alternative in syntax.Alternatives) type = TypeRef.Union(type, ResolveType(frame, alternative));
        return syntax.Nullable ? type.WithNull : type;
    }
    private static Info Join(Info left, Info right)
    {
        if (left.Type.Kind == TypeKind.Never) return right;
        if (right.Type.Kind == TypeKind.Never) return left;
        ScopeInfo? scope = null;
        if (left.Scope is not null && right.Scope is not null)
        {
            var members = left.Scope.Members.Where(member => right.Scope.Members.ContainsKey(member.Key))
                .ToDictionary(member => member.Key, member => Join(member.Value, right.Scope.Members[member.Key]));
            scope = new(members);
        }
        IReadOnlyList<Info>? alternatives = null;
        if (left.Type.WithoutNull == TypeRef.Function && right.Type.WithoutNull == TypeRef.Function &&
            (left.Function != right.Function || left.NativeName != right.NativeName || left.Alternatives is not null || right.Alternatives is not null))
            alternatives = CallableChoices(left).Concat(CallableChoices(right)).DistinctBy(InfoKey).ToArray();
        return new(TypeRef.Union(left.Type, right.Type), left.Function == right.Function ? left.Function : null,
            scope, left.TypeValue == right.TypeValue ? left.TypeValue : null,
            left.NativeName == right.NativeName ? left.NativeName : null, alternatives,
            left.Element is null || right.Element is null ? left.Element ?? right.Element : Join(left.Element, right.Element),
            left.KnownBool == right.KnownBool ? left.KnownBool : null,
            left.KnownConnection == right.KnownConnection ? left.KnownConnection : null);
    }
    private static void Convert(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (expected.Accepts(actual)) return;
        var target = expected.Kind & ~TypeKind.Null;
        if (target is TypeKind.Path or TypeKind.String) frame.Emit(OpCode.Convert, text: target.ToString(), span: span);
    }
    private void CheckConversion(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (expected.Accepts(actual)) return;
        var converted = actual.Kind & ~(TypeKind.Path | TypeKind.String);
        if ((expected.Kind & (TypeKind.Path | TypeKind.String)) != 0 && (expected.Kind & converted) == converted) return;
        throw TypeError(frame, actual, expected, span);
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
                        : PathCompatible.Accepts(expected)
                            ? "Use a Path or String value. A Connection selects an endpoint; qualify a path with 'connection::path'."
                            : $"Supply a {expected} value here. Type annotations constrain values; they do not add new capabilities.";
        return Error(frame, $"Expected {expected}, found {actual}.", span,
            label: $"this expression has type {actual}", help: help, code: "E0203");
    }
    private static CompileException Error(Frame frame, string message, SourceSpan span,
        string? label = null, string? help = null, string? note = null, DiagnosticFix? fix = null, string code = "E0200") =>
        new(message, frame.Source, frame.SourceName, span, label, help, note, fix, code);
    private static bool HasStandardReference(Node node) => node is UnaryExpr { Operator: "$", Operand: NameExpr { Name: "std" } } || Children(node).Any(HasStandardReference);
    private static IEnumerable<string> Names(Node node)
    {
        if (node is NameExpr name) yield return name.Name;
        foreach (var child in Children(node)) foreach (var nested in Names(child)) yield return nested;
    }
    internal static IEnumerable<Node> Children(Node node) => node switch
    {
        BlockExpr block => block.Statements,
        LetStatement let => [let.Value],
        ExpressionStatement expression => [expression.Expression],
        FunctionDeclaration declaration => [declaration.Function],
        ScopeDeclaration declaration => [declaration.Scope],
        FunctionExpr function => [function.Body],
        ScopeExpr scope => scope.Members.Cast<Node>().Concat(scope.Value is null ? [] : [scope.Value]),
        ScopeMember member => [member.Value],
        UnaryExpr unary => [unary.Operand],
        BinaryExpr binary => [binary.Left, binary.Right],
        CallExpr call => new Node[] { call.Callee }.Concat(call.Arguments),
        IfExpr conditional => new Node[] { conditional.Condition, conditional.Then }.Concat(conditional.Else is null ? [] : [conditional.Else]),
        MatchExpr match => new Node[] { match.Value }.Concat(match.Arms.Select(arm => (Node)arm.Value)),
        ForExpr loop => [loop.Source, loop.Body],
        LoopExpr loop => [loop.Body],
        RangeExpr range => range.Items,
        ReturnExpr returned when returned.Value is not null => [returned.Value],
        BreakExpr broken when broken.Value is not null => [broken.Value],
        _ => []
    };
}
