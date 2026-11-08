using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info Emit(Frame frame, OpCode op, TypeRef type, SourceSpan span)
    {
        frame.Emit(op, span: span); return new(type, KnownBool: op == OpCode.True ? true : op == OpCode.False ? false : null);
    }
    private Info Constant(Frame frame, Value value, SourceSpan span)
    {
        int index = constants.IndexOf(value);
        if (index < 0) { index = constants.Count; constants.Add(value); }
        frame.Emit(OpCode.Constant, index, span: span);
        return new(value.Type);
    }
    private Info TypeConstant(Frame frame, TypeRef type, SourceSpan span)
    {
        Constant(frame, new TypeValue(type), span);
        return new(TypeRef.Type, TypeValue: type);
    }
    private Info Path(Frame frame, string path, SourceSpan span)
    {
        if (path.StartsWith("ssh://", StringComparison.Ordinal)) return Constant(frame, new ConnectionValue(ConnectionKind.Ssh, path[6..]), span);
        if (path.StartsWith("wsl://", StringComparison.Ordinal)) return Constant(frame, new ConnectionValue(ConnectionKind.Wsl, path[6..]), span);
        return Constant(frame, new PathValue(path), span);
    }
    private Info Name(Frame frame, string name, SourceSpan span, bool reference = false)
    {
        if (frame.Environment.TryGetValue(name, out var binding))
        {
            if (binding.Self is not null) EmitClosure(frame, binding.Self);
            else frame.Emit(OpCode.LoadLocal, binding.Slot, span: span);
            return binding.Info;
        }
        if (reference && name == "std") return Standard(frame, span);
        if (reference && name is "view" or "views" or "ranges") return StandardModule(frame, "ranges", span);
        if (reference && name is "local" or "wsl" or "pwd" or "file" or "env" or "path")
        {
            frame.Emit(OpCode.LoadBuiltin, text: name, span: span);
            return new(TypeRef.Connection);
        }
        return Path(frame, name, span);
    }
    private Info Unary(Frame frame, UnaryExpr unary)
    {
        if (unary.Operator == "$")
        {
            if (unary.Operand is NameExpr name) return Name(frame, name.Name, unary.Span, true);
            return Expression(frame, unary.Operand);
        }
        var operand = Expression(frame, unary.Operand);
        switch (unary.Operator)
        {
            case "?": frame.Emit(OpCode.IsNull, span: unary.Span); return new(TypeRef.Bool, KnownBool: operand.Type == TypeRef.Null ? true : !operand.Type.Nullable ? false : null);
            case "not": Require(frame, operand.Type, TypeRef.Bool, unary.Operand.Span); frame.Emit(OpCode.Not, span: unary.Span); return new(TypeRef.Bool, KnownBool: operand.KnownBool is null ? null : !operand.KnownBool.Value);
            case "exists": Require(frame, operand.Type, PathCompatible, unary.Operand.Span); frame.Emit(OpCode.NativeCall, 1, text: "fs.exists", span: unary.Span); return new(TypeRef.Bool);
            case "-": Require(frame, operand.Type, TypeRef.Number, unary.Operand.Span); frame.Emit(OpCode.Negate, span: unary.Span); return operand;
            default: throw Error(frame, $"Unknown unary operator '{unary.Operator}'.", unary.Span);
        }
    }

    private static readonly TypeRef PathCompatible = TypeRef.Union(TypeRef.Path, TypeRef.String);
    private Info Binary(Frame frame, BinaryExpr binary)
    {
        if (binary.Operator == "::") return Select(frame, binary);
        if (binary.Operator == "|") return Pipe(frame, binary);
        if (binary.Operator is "and" or "or") return Logical(frame, binary);
        var left = Expression(frame, binary.Left);
        if (binary.Operator == "/" && binary.Right is PathExpr { Path: var pattern } && pattern.StartsWith('<') && pattern.EndsWith('>'))
        {
            Require(frame, left.Type, PathCompatible, binary.Left.Span);
            ValidateRegex(frame, pattern[1..^1], binary.Right.Span);
            frame.Emit(OpCode.SelectPath, text: pattern[1..^1], span: binary.Span);
            return new(new(TypeKind.Range, TypeRef.Path));
        }
        var right = Expression(frame, binary.Right);
        switch (binary.Operator)
        {
            case "/":
                Require(frame, left.Type, PathCompatible, binary.Left.Span); Require(frame, right.Type, PathCompatible, binary.Right.Span);
                frame.Emit(OpCode.PathJoin, span: binary.Span); return new(TypeRef.Path);
            case "->":
                Require(frame, left.Type, new(TypeKind.Path | TypeKind.String | TypeKind.Range), binary.Left.Span);
                if (left.Type.Element is not null) Require(frame, left.Type.Element, PathCompatible, binary.Left.Span);
                Require(frame, right.Type, PathCompatible, binary.Right.Span);
                frame.Emit(OpCode.Transfer, span: binary.Span); return new(TypeRef.Bool);
            case "is":
            case "is not":
                if (right.Type == TypeRef.Null) { frame.Emit(OpCode.Pop); frame.Emit(OpCode.IsNull, span: binary.Span); }
                else if (right.Type == TypeRef.Type) frame.Emit(OpCode.IsType, span: binary.Span);
                else throw Error(frame, "'is' compares against null or a type.", binary.Right.Span);
                if (binary.Operator == "is not") frame.Emit(OpCode.Not);
                bool? known = right.Type == TypeRef.Null ? left.Type == TypeRef.Null ? true : !left.Type.Nullable ? false : null
                    : right.TypeValue is null ? null : left.TypeValue is not null ? right.TypeValue.Accepts(left.TypeValue)
                    : right.TypeValue.Accepts(left.Type) ? true : (right.TypeValue.Kind & left.Type.Kind) == 0 ? false : null;
                if (binary.Operator == "is not" && known is not null) known = !known.Value;
                return new(TypeRef.Bool, KnownBool: known);
            case "==":
            case "!=":
            case "<":
            case "<=":
            case ">":
            case ">=":
                if (left.Type.Nullable || right.Type.Nullable) throw Error(frame, "Null has no boolean comparison; use 'is null' or 'is not null'.", binary.Span);
                var comparable = new TypeRef(TypeKind.Bool | TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Type | TypeKind.Connection);
                Require(frame, left.Type, comparable, binary.Left.Span); Require(frame, right.Type, comparable, binary.Right.Span);
                if (left.Type != right.Type && !(PathCompatible.Accepts(left.Type) && PathCompatible.Accepts(right.Type)))
                    throw Error(frame, $"Cannot compare {left.Type} and {right.Type}.", binary.Span);
                frame.Emit(binary.Operator switch { "==" => OpCode.Equal, "!=" => OpCode.NotEqual, "<" => OpCode.Less, "<=" => OpCode.LessEqual, ">" => OpCode.Greater, _ => OpCode.GreaterEqual }, span: binary.Span);
                return new(TypeRef.Bool);
            case "+":
            case "-":
            case "*":
                Require(frame, left.Type, TypeRef.Number, binary.Left.Span); Require(frame, right.Type, TypeRef.Number, binary.Right.Span);
                frame.Emit(binary.Operator switch { "+" => OpCode.Add, "-" => OpCode.Subtract, _ => OpCode.Multiply }, span: binary.Span);
                return new(TypeRef.Number);
            default: throw Error(frame, $"Unknown operator '{binary.Operator}'.", binary.Span);
        }
    }

    private Info Logical(Frame frame, BinaryExpr binary)
    {
        var left = Expression(frame, binary.Left); Require(frame, left.Type, TypeRef.Bool, binary.Left.Span);
        frame.Emit(OpCode.Dup);
        int skip = frame.Emit(binary.Operator == "and" ? OpCode.JumpIfFalse : OpCode.JumpIfTrue);
        frame.Emit(OpCode.Pop);
        var old = frame.Environment;
        frame.Environment = Refined(frame, binary.Left, binary.Operator == "and");
        var right = Expression(frame, binary.Right); Require(frame, right.Type, TypeRef.Bool, binary.Right.Span);
        frame.Environment = old;
        frame.Patch(skip, frame.Code.Count);
        return new(TypeRef.Bool);
    }

    private Info Select(Frame frame, BinaryExpr binary)
    {
        var left = binary.Left is NameExpr alias && alias.Name is "view" or "views" or "ranges" && !frame.Environment.ContainsKey(alias.Name)
            ? StandardModule(frame, "ranges", binary.Left.Span) : Expression(frame, binary.Left);
        var member = binary.Right switch { NameExpr name => name.Name, PathExpr path => path.Path, StringExpr text => text.Value, _ => null };
        if (left.Type == TypeRef.Scope && member is not null)
        {
            if (left.Scope is null || !left.Scope.Members.TryGetValue(member, out var info)) throw Error(frame, $"Scope has no member '{member}'.", binary.Right.Span);
            frame.Emit(OpCode.GetMember, text: member, span: binary.Span); return info;
        }
        if (left.Type == TypeRef.Process && member is not null)
        {
            frame.Emit(OpCode.GetMember, text: member, span: binary.Span);
            return member switch { "stdout" or "stderr" => new(TypeRef.String), "exit_code" or "pid" => new(TypeRef.Number), _ => throw Error(frame, $"Process has no member '{member}'.", binary.Right.Span) };
        }
        Require(frame, left.Type, TypeRef.Connection, binary.Left.Span);
        var right = Expression(frame, binary.Right); Require(frame, right.Type, PathCompatible, binary.Right.Span);
        frame.Emit(OpCode.Qualify, span: binary.Span); return new(TypeRef.Path);
    }

    private Info Scope(Frame frame, ScopeExpr scope)
    {
        var old = frame.Environment; frame.Environment = new(old);
        Dictionary<string, Info> members = [];
        List<string> keys = [];
        foreach (var member in scope.Members)
        {
            var info = Expression(frame, member.Value);
            frame.Emit(OpCode.Dup); Store(frame, member.Name, info);
            keys.Add(member.Name); members[member.Name] = info;
        }
        if (scope.Value is not null) { Expression(frame, scope.Value); frame.Emit(OpCode.Pop); }
        frame.Emit(OpCode.MakeScope, keys.Count, text: string.Join('\0', keys), span: scope.Span);
        frame.Environment = old;
        return new(TypeRef.Scope, Scope: new(members));
    }


}
