using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

public static class Compiler
{
    public static Module Compile(string source, string? sourceName = null, int optimization = 1) =>
        new Compilation(source, sourceName ?? "<source>", optimization).Run();
}

internal sealed record Info(TypeRef Type, Template? Function = null, ScopeInfo? Scope = null, TypeRef? TypeValue = null, string? NativeName = null);
internal sealed record Binding(int Slot, Info Info, Template? Self = null);
internal sealed record ScopeInfo(IReadOnlyDictionary<string, Info> Members);
internal sealed class Template(int id, FunctionExpr syntax, IReadOnlyList<KeyValuePair<string, Binding>> captures, string source, string sourceName)
{
    public int Id { get; } = id;
    public FunctionExpr Syntax { get; } = syntax;
    public IReadOnlyList<KeyValuePair<string, Binding>> Captures { get; } = captures;
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public Dictionary<string, Specialization> Specializations { get; } = [];
}
internal sealed class Specialization(int id, Info result)
{
    public int Id { get; } = id;
    public Info Result { get; set; } = result;
}
internal sealed class Frame(string source, string sourceName, Dictionary<string, Binding>? environment = null)
{
    public string Source { get; } = source;
    public string SourceName { get; } = sourceName;
    public Dictionary<string, Binding> Environment { get; set; } = environment ?? [];
    public List<Instruction> Code { get; } = [];
    public int Locals { get; set; }
    public List<Info> Returns { get; } = [];
    public TypeRef? ExpectedReturn { get; set; }
    public Dictionary<int, int[]> SelfCaptureSlots { get; } = [];
    public List<List<int>> Breaks { get; } = [];
    public List<List<Info>> BreakValues { get; } = [];
    public int Allocate() => Locals++;
    public int Emit(OpCode op, int a = 0, int b = 0, string? text = null, SourceSpan? span = null)
    {
        Code.Add(new(op, a, b, text, span is null ? null : new Msync.Core.SourceSpan(SourceName, span.Value.Start, span.Value.Length)));
        return Code.Count - 1;
    }
    public void Patch(int at, int target) => Code[at] = Code[at] with { A = target };
}

internal sealed class Compilation(string source, string sourceName, int optimization)
{
    private readonly List<Value> constants = [];
    private readonly List<FunctionCode> functions = [];
    private readonly List<Template> templates = [];
    private Dictionary<string, Binding> sessionEnvironment = [];
    private int sessionLocals;
    private static readonly Lazy<IReadOnlyDictionary<string, (string Source, Document Document)>> libraries = new(ReadLibraries);
    private int nextTemplate;

    public Module Run() => Submit(source);

    public Module Submit(string text)
    {
        var document = Parser.Parse(text, sourceName);
        Frame frame = new(text, sourceName, new(sessionEnvironment)) { Locals = sessionLocals };
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

    private Info Emit(Frame frame, OpCode op, TypeRef type, SourceSpan span)
    {
        frame.Emit(op, span: span); return new(type);
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
    private Info Name(Frame frame, string name, SourceSpan span)
    {
        if (frame.Environment.TryGetValue(name, out var binding))
        {
            if (binding.Self is not null) EmitClosure(frame, binding.Self);
            else frame.Emit(OpCode.LoadLocal, binding.Slot, span: span);
            return binding.Info;
        }
        if (name == "std") return Standard(frame, span);
        if (name is "view" or "views" or "ranges") return StandardModule(frame, "ranges", span);
        if (name is "local" or "wsl" or "pwd" or "file" or "env" or "path")
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
            if (unary.Operand is NameExpr name) return Name(frame, name.Name, unary.Span);
            return Expression(frame, unary.Operand);
        }
        var operand = Expression(frame, unary.Operand);
        switch (unary.Operator)
        {
            case "?": frame.Emit(OpCode.IsNull, span: unary.Span); return new(TypeRef.Bool);
            case "not": Require(frame, operand.Type, TypeRef.Bool, unary.Operand.Span); frame.Emit(OpCode.Not, span: unary.Span); return new(TypeRef.Bool);
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
                return new(TypeRef.Bool);
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

    private Info Block(Frame frame, BlockExpr block)
    {
        var old = frame.Environment; frame.Environment = new(old);
        var result = Statements(frame, block.Statements);
        frame.Environment = old;
        return result;
    }
    private Info Conditional(Frame frame, IfExpr conditional)
    {
        var test = Expression(frame, conditional.Condition); Require(frame, test.Type, TypeRef.Bool, conditional.Condition.Span);
        int otherwise = frame.Emit(OpCode.JumpIfFalse);
        var old = frame.Environment;
        frame.Environment = Refined(frame, conditional.Condition, true);
        var yes = Block(frame, conditional.Then);
        int end = frame.Emit(OpCode.Jump);
        frame.Patch(otherwise, frame.Code.Count);
        frame.Environment = old;
        frame.Environment = Refined(frame, conditional.Condition, false);
        var no = conditional.Else is null ? Emit(frame, OpCode.Null, TypeRef.Null, conditional.Span) : Expression(frame, conditional.Else);
        frame.Environment = old;
        if (yes.Type.Kind == TypeKind.Never && no.Type.Kind != TypeKind.Never) frame.Environment = Refined(frame, conditional.Condition, false);
        if (no.Type.Kind == TypeKind.Never && yes.Type.Kind != TypeKind.Never) frame.Environment = Refined(frame, conditional.Condition, true);
        frame.Patch(end, frame.Code.Count);
        return Join(yes, no);
    }
    private Dictionary<string, Binding> Refined(Frame frame, Expr expression, bool positive)
    {
        Dictionary<string, Binding> result = new(frame.Environment);
        if (expression is UnaryExpr { Operator: "not" } not) return Refined(frame, not.Operand, !positive);
        if (expression is BinaryExpr logical && ((logical.Operator == "and" && positive) || (logical.Operator == "or" && !positive)))
        {
            var old = frame.Environment; frame.Environment = Refined(frame, logical.Left, positive);
            var both = Refined(frame, logical.Right, positive); frame.Environment = old; return both;
        }
        string? name = null; TypeRef? type = null;
        if (expression is UnaryExpr { Operator: "?", Operand: NameExpr nullable }) { name = nullable.Name; type = TypeRef.Null; }
        if (expression is BinaryExpr { Operator: "is" or "is not", Left: NameExpr tested } binary)
        {
            name = tested.Name;
            if (binary.Right is NullExpr) type = TypeRef.Null;
            if (binary.Right is TypeExpr syntax) type = ResolveType(frame, syntax.Type);
            if (binary.Operator == "is not") positive = !positive;
        }
        if (name is null || type is null || !result.TryGetValue(name, out var binding)) return result;
        var narrowed = positive ? new TypeRef(binding.Info.Type.Kind & type.Kind, binding.Info.Type.Element) : new TypeRef(binding.Info.Type.Kind & ~type.Kind, binding.Info.Type.Element);
        result[name] = binding with { Info = binding.Info with { Type = narrowed } };
        return result;
    }

    private Info Select(Frame frame, BinaryExpr binary)
    {
        var left = Expression(frame, binary.Left);
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

    private Info Range(Frame frame, RangeExpr range)
    {
        TypeRef item = new(TypeKind.Never);
        bool allPure = range.Items.All(PureValue);
        List<char> mask = [];
        foreach (var expression in range.Items)
        {
            if (PureValue(expression))
            {
                item = TypeRef.Union(item, Expression(frame, expression).Type); mask.Add('0'); continue;
            }
            Frame scratch = CloneFrame(frame);
            var info = Expression(scratch, expression);
            item = TypeRef.Union(item, info.Type);
            EmitThunk(frame, scratch, info, "<range-item>"); mask.Add('1');
        }
        frame.Emit(allPure ? OpCode.MakeRange : OpCode.MakeLazyRange, range.Items.Count, text: allPure ? null : new string(mask.ToArray()), span: range.Span);
        return new(new(TypeKind.Range, item));
    }
    private static bool PureValue(Expr expression) => expression switch
    {
        BoolExpr or NullExpr or StringExpr or NumberExpr or PathExpr or NameExpr or TypeExpr => true,
        BinaryExpr { Operator: "/" or "::" } binary => PureValue(binary.Left) && PureValue(binary.Right),
        UnaryExpr { Operator: "$" } unary => PureValue(unary.Operand),
        _ => false
    };
    private static Frame CloneFrame(Frame frame)
    {
        Frame clone = new(frame.Source, frame.SourceName, new(frame.Environment)) { Locals = frame.Locals, ExpectedReturn = frame.ExpectedReturn };
        foreach (var pair in frame.SelfCaptureSlots) clone.SelfCaptureSlots[pair.Key] = pair.Value;
        return clone;
    }

    private void EmitThunk(Frame frame, Frame scratch, Info result, string name)
    {
        int parentLocals = frame.Locals;
        var captured = scratch.Code.Where(instruction => instruction.Op == OpCode.LoadLocal && instruction.A < parentLocals)
            .Select(instruction => instruction.A).Distinct().Order().ToArray();
        var slots = captured.Select((slot, index) => (slot, index)).ToDictionary(pair => pair.slot, pair => pair.index);
        int Remap(int slot) => slot < parentLocals ? slots[slot] : captured.Length + slot - parentLocals;
        var code = scratch.Code.Select(instruction => instruction.Op is OpCode.LoadLocal or OpCode.StoreLocal or OpCode.IteratorNext
            ? instruction with { A = Remap(instruction.A) } : instruction).ToList();
        code.Add(new(OpCode.Return));
        int id = functions.Count;
        functions.Add(new(name, 0, captured.Length + scratch.Locals - parentLocals, code.ToArray(), result.Type, captured.Length));
        foreach (int slot in captured) frame.Emit(OpCode.LoadLocal, slot);
        frame.Emit(OpCode.MakeClosure, id, captured.Length);
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

    private Info Function(Frame frame, FunctionExpr function)
    {
        var usedNames = Names(function.Body).ToHashSet(StringComparer.Ordinal);
        if (usedNames.Contains("std") && !frame.Environment.ContainsKey("std"))
        {
            var standard = Standard(frame, function.Span);
            Store(frame, "std", standard);
        }
        var captures = frame.Environment.Where(pair => usedNames.Contains(pair.Key) && pair.Key != function.Name).ToArray();
        int id = functions.Count;
        functions.Add(new(function.Name ?? $"<lambda{nextTemplate++}>", function.Parameters.Count, captures.Length + function.Parameters.Count,
            [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null, captures.Length));
        Template template = new(id, function, captures, frame.Source, frame.SourceName);
        templates.Add(template);
        EmitClosure(frame, template);
        return new(TypeRef.Function, template);
    }
    private void EmitClosure(Frame frame, Template template)
    {
        if (frame.SelfCaptureSlots.TryGetValue(template.Id, out var ownSlots))
        {
            foreach (int slot in ownSlots) frame.Emit(OpCode.LoadLocal, slot);
        }
        else
        {
            foreach (var capture in template.Captures)
            {
                if (capture.Value.Self is not null) EmitClosure(frame, capture.Value.Self);
                else frame.Emit(OpCode.LoadLocal, capture.Value.Slot);
            }
        }
        frame.Emit(OpCode.MakeClosure, template.Id, template.Captures.Count, span: template.Syntax.Span);
    }
    private Specialization Specialize(Frame caller, Template template, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        if (arguments.Count != template.Syntax.Parameters.Count) throw Error(caller, $"Function expects {template.Syntax.Parameters.Count} arguments, got {arguments.Count}.", span);
        string key = string.Join(';', arguments.Select(argument => $"{argument.Type}:{argument.Function?.Id}:{(argument.Scope is null ? "" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(argument.Scope))}"));
        if (template.Specializations.TryGetValue(key, out var existing)) return existing;
        int id = template.Specializations.Count == 0 ? template.Id : functions.Count;
        if (id == functions.Count) functions.Add(functions[template.Id]);
        var asserted = template.Syntax.ReturnType is null ? null : ResolveType(caller, template.Syntax.ReturnType);
        Specialization specialization = new(id, new(asserted ?? new TypeRef(TypeKind.Never)));
        template.Specializations[key] = specialization;
        Frame frame = new(template.Source, template.SourceName) { ExpectedReturn = asserted };
        frame.SelfCaptureSlots[template.Id] = Enumerable.Range(0, template.Captures.Count).ToArray();
        foreach (var capture in template.Captures) frame.Environment[capture.Key] = new(frame.Allocate(), capture.Value.Info);
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = template.Syntax.Parameters[index];
            var info = arguments[index];
            if (parameter.Type is not null)
            {
                var type = ResolveType(frame, parameter.Type);
                if (parameter.Nullable) type = type.WithNull;
                CheckConversion(caller, info.Type, type, span);
                if (!type.Accepts(info.Type))
                {
                    int parameterSlot = frame.Locals;
                    frame.Emit(OpCode.LoadLocal, parameterSlot);
                    Convert(frame, info.Type, type, span);
                    frame.Emit(OpCode.StoreLocal, parameterSlot);
                    info = info with { Type = type };
                }
            }
            else if (info.Type.Nullable && !parameter.Nullable)
                throw Error(caller, $"Nullable argument requires nullable parameter '{parameter.Name}?'.", span);
            frame.Environment[parameter.Name] = new(frame.Allocate(), info);
        }
        if (template.Syntax.Name is not null) frame.Environment[template.Syntax.Name] = new(-1, new(TypeRef.Function, template), template);
        var result = Block(frame, template.Syntax.Body);
        foreach (var returned in frame.Returns) result = Join(result, returned);
        if (asserted is not null) { CheckConversion(frame, result.Type, asserted, template.Syntax.Body.Span); Convert(frame, result.Type, asserted, template.Syntax.Body.Span); result = result with { Type = asserted }; }
        frame.Emit(OpCode.Return);
        specialization.Result = result;
        functions[id] = new(template.Syntax.Name ?? $"<lambda{template.Id}>", arguments.Count, frame.Locals, frame.Code.ToArray(), result.Type,
            template.Captures.Count, arguments.Select(info => info.Type).ToArray());
        return specialization;
    }

    private Info Call(Frame frame, CallExpr call)
    {
        Frame scratch = CloneFrame(frame);
        var result = CallEager(scratch, call);
        if (result.Type.Kind == TypeKind.Range)
        {
            EmitThunk(frame, scratch, result, "<range-call>");
            frame.Emit(OpCode.DeferRange, text: result.Type.Element?.ToString(), span: call.Span);
        }
        else
        {
            int offset = frame.Code.Count;
            frame.Code.AddRange(scratch.Code.Select(instruction => instruction.Op is OpCode.Jump or OpCode.JumpIfFalse or OpCode.JumpIfTrue
                ? instruction with { A = instruction.A + offset }
                : instruction.Op == OpCode.IteratorNext ? instruction with { B = instruction.B + offset } : instruction));
            frame.Locals = scratch.Locals;
            frame.Environment = scratch.Environment;
        }
        return result;
    }

    private Info CallEager(Frame frame, CallExpr call)
    {
        if (call.Callee is NameExpr { Name: "decltype" })
        {
            if (call.Arguments.Count != 1) throw Error(frame, "decltype expects one unevaluated expression.", call.Span);
            Frame scratch = CloneFrame(frame);
            var inferred = Expression(scratch, call.Arguments[0]);
            return TypeConstant(frame, inferred.Type, call.Span);
        }
        var callee = Expression(frame, call.Callee);
        var arguments = call.Arguments.Select(argument => Expression(frame, argument)).ToArray();
        return Invoke(frame, callee, arguments, call.Span);
    }
    private Info Invoke(Frame frame, Info callee, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        if (callee.Type == TypeRef.Path || callee.Type == TypeRef.String)
        {
            foreach (var argument in arguments) Require(frame, argument.Type, new(TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Bool), span);
            frame.Emit(OpCode.ProcessCall, arguments.Count, span: span); return new(TypeRef.Process);
        }
        Require(frame, callee.Type, TypeRef.Function, span);
        if (callee.Function is not null)
        {
            var specialization = Specialize(frame, callee.Function, arguments, span);
            frame.Emit(OpCode.Call, arguments.Count, specialization.Id + 1, span: span);
            return specialization.Result;
        }
        if (callee.NativeName is not null) return InvokeNative(frame, callee.NativeName, arguments, span);
        throw Error(frame, "Function value has no inferred callable signature.", span);
    }
    private Info InvokeNative(Frame frame, string identity, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        var signature = NativeSignatures.Get(identity);
        if ((!signature.Variadic && arguments.Count != signature.Parameters.Count) || (signature.Variadic && arguments.Count < signature.Parameters.Count))
            throw Error(frame, $"Native '{identity}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, got {arguments.Count}.", span);
        for (int index = 0; index < arguments.Count; index++)
        {
            var accepted = signature.Parameters.Count == 0 ? TypeRef.Any : signature.Parameters[Math.Min(index, signature.Parameters.Count - 1)];
            CheckConversion(frame, arguments[index].Type, accepted, span);
        }
        Info? callbackResult = null;
        int callbackIndex = arguments.Count - 1;
        if (callbackIndex >= 1 && arguments[0].Type.Kind == TypeKind.Range && arguments[callbackIndex].Function is not null)
        {
            var item = new Info(arguments[0].Type.Element ?? TypeRef.Any);
            var specialization = Specialize(frame, arguments[callbackIndex].Function!, [item], span);
            callbackResult = specialization.Result;
            frame.Emit(OpCode.SpecializeClosure, specialization.Id, span: span);
            if (identity is "ranges.filter" or "ranges.take_while" or "ranges.drop_while" or "ranges.any" or "ranges.any_of" or "ranges.all_of" or "ranges.none" or "ranges.none_of" or "ranges.find_if")
                Require(frame, callbackResult.Type, TypeRef.Bool, span);
        }
        var result = signature.ResultRule switch
        {
            NativeResultRule.FirstArgument when arguments.Count > 0 => arguments[0],
            NativeResultRule.RangeOfArguments => new(new(TypeKind.Range, arguments.Aggregate(new TypeRef(TypeKind.Never), (type, argument) => TypeRef.Union(type, argument.Type)))),
            NativeResultRule.RangeItem when arguments.Count > 0 => new(arguments[0].Type.Element ?? TypeRef.Any),
            NativeResultRule.Transform when callbackResult is not null => new(new(TypeKind.Range, callbackResult.Type)),
            _ => new Info(signature.Result)
        };
        frame.Emit(OpCode.Call, arguments.Count, span: span);
        return result;
    }
    private Info Native(Frame frame, NativeDeclaration native, string? module)
    {
        string identity = module is null ? native.Name : $"{module}.{native.Name}";
        if (!NativeSignatures.TryGet(identity, out var signature))
        {
            var matches = NativeSignatures.All.Where(item => item.Name.EndsWith("." + identity, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw Error(frame, $"Native function '{identity}' has no unique registered implementation.", native.Span);
            identity = matches[0].Name; signature = matches[0];
        }
        if ((!signature.Variadic && native.Parameters.Count != signature.Parameters.Count) || (signature.Variadic && (native.Parameters.Count < signature.Parameters.Count || native.Parameters.Count > signature.Parameters.Count + 1)))
            throw Error(frame, $"Native declaration '{identity}' has the wrong parameter count.", native.Span);
        for (int index = 0; index < native.Parameters.Count; index++)
        {
            var parameter = native.Parameters[index];
            if (signature.Variadic && index >= signature.Parameters.Count) continue;
            if (parameter.Type is null)
            {
                if (signature.ResultRule != NativeResultRule.Fixed || signature.Parameters[index].Kind == (TypeKind.Any & ~TypeKind.Null)) continue;
                throw Error(frame, "This native parameter requires an explicit type.", parameter.Span);
            }
            var actual = ResolveType(frame, parameter.Type);
            if (parameter.Nullable) actual = actual.WithNull;
            if (!signature.Parameters[index].Accepts(actual)) throw Error(frame, $"Native '{identity}' parameter '{parameter.Name}' must accept {signature.Parameters[index]}, not {actual}.", parameter.Span);
        }
        var written = ResolveType(frame, native.ReturnType);
        if (signature.ResultRule == NativeResultRule.Fixed && !written.Accepts(signature.Result))
            throw Error(frame, $"Native '{identity}' returns {signature.Result}, not {written}.", native.ReturnType.Span);
        Constant(frame, new FunctionValue(-1, NativeName: identity), native.Span);
        Info info = new(TypeRef.Function, NativeName: identity);
        return info;
    }

    private Info Pipe(Frame frame, BinaryExpr binary)
    {
        // Reorder through a temporary so calls keep one universal callee/argument convention.
        var input = Expression(frame, binary.Left);
        Require(frame, input.Type, new(TypeKind.Range), binary.Left.Span);
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot);
        Info callee; IReadOnlyList<Expr> rest;
        if (binary.Right is CallExpr call) { callee = Expression(frame, call.Callee); rest = call.Arguments; }
        else { callee = Expression(frame, binary.Right); rest = []; }
        frame.Emit(OpCode.LoadLocal, slot);
        List<Info> arguments = [input];
        arguments.AddRange(rest.Select(expression => Expression(frame, expression)));
        return Invoke(frame, callee, arguments, binary.Span);
    }

    private Info For(Frame frame, ForExpr loop)
    {
        var sourceInfo = Expression(frame, loop.Source); Require(frame, sourceInfo.Type, new(TypeKind.Range), loop.Source.Span);
        frame.Emit(OpCode.GetIterator);
        int iterator = frame.Allocate(); frame.Emit(OpCode.StoreLocal, iterator);
        int begin = frame.Code.Count;
        int next = frame.Emit(OpCode.IteratorNext, iterator);
        var old = frame.Environment; frame.Environment = new(old);
        BindPattern(frame, loop.Pattern, new(sourceInfo.Type.Element ?? TypeRef.Any));
        Block(frame, loop.Body); frame.Emit(OpCode.Pop);
        frame.Environment = old;
        frame.Emit(OpCode.Jump, begin);
        frame.Code[next] = frame.Code[next] with { B = frame.Code.Count };
        frame.Emit(OpCode.Null);
        return new(TypeRef.Null);
    }
    private Info Loop(Frame frame, LoopExpr loop)
    {
        int start = frame.Code.Count;
        List<int> exits = []; List<Info> values = [];
        frame.Breaks.Add(exits); frame.BreakValues.Add(values);
        Block(frame, loop.Body); frame.Emit(OpCode.Pop); frame.Emit(OpCode.Jump, start);
        foreach (int at in exits) frame.Patch(at, frame.Code.Count);
        frame.Breaks.RemoveAt(frame.Breaks.Count - 1); frame.BreakValues.RemoveAt(frame.BreakValues.Count - 1);
        return values.Count == 0 ? new(new(TypeKind.Never)) : values.Aggregate(Join);
    }
    private Info Return(Frame frame, ReturnExpr returned)
    {
        var result = returned.Value is null ? Emit(frame, OpCode.Null, TypeRef.Null, returned.Span) : Expression(frame, returned.Value);
        if (frame.ExpectedReturn is not null)
        {
            CheckConversion(frame, result.Type, frame.ExpectedReturn, returned.Span);
            Convert(frame, result.Type, frame.ExpectedReturn, returned.Span);
            result = result with { Type = frame.ExpectedReturn };
        }
        frame.Returns.Add(result); frame.Emit(OpCode.Return, span: returned.Span); frame.Emit(OpCode.Null);
        return new(new(TypeKind.Never));
    }
    private Info Break(Frame frame, BreakExpr broken)
    {
        if (frame.Breaks.Count == 0) throw Error(frame, "break requires an enclosing loop.", broken.Span);
        var result = broken.Value is null ? Emit(frame, OpCode.Null, TypeRef.Null, broken.Span) : Expression(frame, broken.Value);
        frame.BreakValues[^1].Add(result); frame.Breaks[^1].Add(frame.Emit(OpCode.Jump)); frame.Emit(OpCode.Null);
        return new(new(TypeKind.Never));
    }

    private Info Match(Frame frame, MatchExpr match)
    {
        var value = Expression(frame, match.Value);
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot);
        CheckExhaustive(frame, value, match.Arms, match.Span);
        Info? result = null; List<int> ends = [];
        var old = frame.Environment;
        TypeRef remaining = value.Type;
        foreach (var arm in match.Arms)
        {
            frame.Environment = new(old);
            frame.Emit(OpCode.LoadLocal, slot);
            EmitPattern(frame, arm.Pattern, value, arm.Pattern.Span);
            int skip = frame.Emit(OpCode.JumpIfFalse);
            var refined = PatternType(frame, arm.Pattern, remaining);
            if (arm.Pattern is NullPattern or TypePattern) remaining = new(remaining.Kind & ~refined.Kind, remaining.Element);
            if (match.Value is NameExpr name && frame.Environment.TryGetValue(name.Name, out var bound))
                frame.Environment[name.Name] = bound with { Info = bound.Info with { Type = refined } };
            if (arm.Pattern is NamePattern { Name: not "_" } capture) frame.Environment[capture.Name] = new(slot, value with { Type = refined });
            var branch = Expression(frame, arm.Value);
            result = result is null ? branch : Join(result, branch);
            ends.Add(frame.Emit(OpCode.Jump));
            frame.Patch(skip, frame.Code.Count);
        }
        frame.Environment = old;
        frame.Emit(OpCode.Null);
        foreach (int end in ends) frame.Patch(end, frame.Code.Count);
        return result ?? new(TypeRef.Null);
    }
    private void EmitPattern(Frame frame, Pattern pattern, Info value, SourceSpan span)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern: frame.Emit(OpCode.Pop); frame.Emit(OpCode.True); break;
            case NullPattern: frame.Emit(OpCode.IsNull, span: span); break;
            case LiteralPattern literal: Expression(frame, literal.Value); frame.Emit(OpCode.Equal, span: span); break;
            case TypePattern type:
                TypeConstant(frame, ResolveType(frame, type.Type), span); frame.Emit(OpCode.IsType, span: span); break;
            case RegexPattern regex:
                Require(frame, value.Type, PathCompatible, span);
                frame.Emit(OpCode.RegexMatch, text: regex.Pattern, span: span); break;
            default: throw Error(frame, "This pattern is not supported in a match arm.", span);
        }
    }
    private TypeRef PatternType(Frame frame, Pattern pattern, TypeRef original) => pattern switch
    {
        NullPattern => TypeRef.Null,
        TypePattern type => new(original.Kind & ResolveType(frame, type.Type).Kind, original.Element),
        LiteralPattern { Value: BoolExpr } => TypeRef.Bool,
        LiteralPattern { Value: StringExpr } or RegexPattern => new(original.Kind & (TypeKind.String | TypeKind.Path), original.Element),
        _ => original
    };
    private void CheckExhaustive(Frame frame, Info value, IReadOnlyList<MatchArm> arms, SourceSpan span)
    {
        TypeKind covered = TypeKind.Never; bool hasTrue = false, hasFalse = false;
        foreach (var arm in arms)
        {
            if (arm.Pattern is WildcardPattern or NamePattern) return;
            if (arm.Pattern is NullPattern) covered |= TypeKind.Null;
            if (arm.Pattern is TypePattern type)
            {
                var matched = ResolveType(frame, type.Type);
                if (value.TypeValue is not null && matched.Accepts(value.TypeValue)) return;
                if (value.Type.Kind != TypeKind.Type) covered |= matched.Kind;
            }
            if (arm.Pattern is LiteralPattern { Value: BoolExpr boolean }) { hasTrue |= boolean.Value; hasFalse |= !boolean.Value; }
        }
        if (hasTrue && hasFalse) covered |= TypeKind.Bool;
        if ((value.Type.Kind & ~covered) != 0) throw Error(frame, $"Non-exhaustive match for {value.Type}; add the missing cases or '_'.", span);
    }

    private Info Standard(Frame frame, SourceSpan span)
    {
        if (frame.Environment.TryGetValue("std", out var existing))
        {
            frame.Emit(OpCode.LoadLocal, existing.Slot); return existing.Info;
        }
        Dictionary<string, Info> members = [];
        foreach (var module in libraries.Value.Keys.Order(StringComparer.Ordinal)) members[module] = StandardModule(frame, module, span);
        frame.Emit(OpCode.MakeScope, members.Count, text: string.Join('\0', members.Keys));
        var info = new Info(TypeRef.Scope, Scope: new(members));
        frame.Emit(OpCode.Dup); Store(frame, "std", info);
        return info;
    }
    private Info StandardModule(Frame frame, string name, SourceSpan span)
    {
        if (!libraries.Value.TryGetValue(name, out var library)) return StandardNativeModule(frame, name, span);
        Frame module = new(library.Source, $"msync-stdlib:///0.8.0/{name}.msync") { Locals = frame.Locals };
        List<string> keys = []; Dictionary<string, Info> members = [];
        foreach (var statement in library.Document.Statements)
        {
            string? memberName = null; Info? info = null;
            switch (statement)
            {
                case NativeDeclaration native: memberName = native.Name; info = Native(module, native, name); break;
                case FunctionDeclaration function: memberName = function.Name; info = Function(module, function.Function); break;
                case LetStatement { Pattern: NamePattern bound } let: memberName = bound.Name; info = Expression(module, let.Value); break;
            }
            if (memberName is null || info is null) throw Error(module, "A standard module contains only named declarations.", statement.Span);
            module.Emit(OpCode.Dup); Store(module, memberName, info);
            keys.Add(memberName); members[memberName] = info;
        }
        module.Emit(OpCode.MakeScope, keys.Count, text: string.Join('\0', keys), span: span);
        frame.Code.AddRange(module.Code); frame.Locals = module.Locals;
        var result = new Info(TypeRef.Scope, Scope: new(members));
        return result;
    }
    private Info StandardNativeModule(Frame frame, string name, SourceSpan span)
    {
        Dictionary<string, Info> members = [];
        foreach (var signature in NativeSignatures.All.Where(signature => signature.Name.StartsWith(name + ".", StringComparison.Ordinal)))
        {
            string key = signature.Name[(name.Length + 1)..];
            Constant(frame, new FunctionValue(-1, NativeName: signature.Name), span);
            var info = new Info(TypeRef.Function, NativeName: signature.Name); members[key] = info;
        }
        frame.Emit(OpCode.MakeScope, members.Count, text: string.Join('\0', members.Keys));
        return new(TypeRef.Scope, Scope: new(members));
    }

    private void Store(Frame frame, string name, Info value)
    {
        int slot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, slot); frame.Environment[name] = new(slot, value);
    }
    private void BindPattern(Frame frame, Pattern pattern, Info value)
    {
        switch (pattern)
        {
            case WildcardPattern or NamePattern { Name: "_" }: frame.Emit(OpCode.Pop); break;
            case NamePattern name: Store(frame, name.Name, value); break;
            case TuplePattern tuple:
                Require(frame, value.Type, new(TypeKind.Range), pattern.Span);
                int sourceSlot = frame.Allocate(); frame.Emit(OpCode.StoreLocal, sourceSlot);
                for (int index = 0; index < tuple.Items.Count; index++)
                {
                    frame.Emit(OpCode.LoadLocal, sourceSlot); Constant(frame, new NumberValue(index), tuple.Items[index].Span);
                    frame.Emit(OpCode.NativeCall, 2, text: "ranges.at", span: tuple.Items[index].Span);
                    BindPattern(frame, tuple.Items[index], new(value.Type.Element ?? TypeRef.Any));
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
            _ => throw Error(frame, $"Unknown type '{syntax.Name}'.", syntax.Span)
        };
        foreach (var alternative in syntax.Alternatives) type = TypeRef.Union(type, ResolveType(frame, alternative));
        return syntax.Nullable ? type.WithNull : type;
    }
    private static Info Join(Info left, Info right)
    {
        if (left.Type.Kind == TypeKind.Never) return right;
        if (right.Type.Kind == TypeKind.Never) return left;
        return new(TypeRef.Union(left.Type, right.Type), left.Function == right.Function ? left.Function : null,
            left.Scope == right.Scope ? left.Scope : null, left.TypeValue == right.TypeValue ? left.TypeValue : null);
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
        throw Error(frame, $"Expected {expected}, found {actual}.", span);
    }
    private static void Require(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        if (!expected.Accepts(actual)) throw Error(frame, $"Expected {expected}, found {actual}.", span);
    }
    private static CompileException Error(Frame frame, string message, SourceSpan span) => new(message, frame.Source, frame.SourceName, span);
    private static IEnumerable<string> Names(Node node)
    {
        if (node is NameExpr name) yield return name.Name;
        foreach (var child in Children(node)) foreach (var nested in Names(child)) yield return nested;
    }
    private static IEnumerable<Node> Children(Node node) => node switch
    {
        BlockExpr block => block.Statements,
        LetStatement let => [let.Value],
        ExpressionStatement expression => [expression.Expression],
        FunctionDeclaration declaration => [declaration.Function],
        ScopeDeclaration declaration => [declaration.Scope],
        FunctionExpr function => [function.Body],
        ScopeExpr scope => scope.Members.Select(member => (Node)member.Value).Concat(scope.Value is null ? [] : [scope.Value]),
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
