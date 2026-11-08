using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<ScopeExpr, int> scopeAllocationSites = new(ReferenceEqualityComparer.Instance);
    private int nextScopeAllocation;
    private Info Emit(Frame frame, OpCode op, TypeRef type, SourceSpan span)
    {
        frame.Emit(op, span: span); return new(type, KnownBool: op == OpCode.True ? true : op == OpCode.False ? false : null);
    }
    private Info Constant(Frame frame, Value value, SourceSpan span)
    {
        if (!constantIndices.TryGetValue(value, out int index))
        {
            index = constants.Count;
            constants.Add(value);
            constantIndices.Add(value, index);
        }
        frame.Emit(OpCode.Constant, index, span: span);
        return new(value.Type, KnownConnection: value as ConnectionValue, KnownPath: value as PathValue, KnownText: (value as StringValue)?.Data);
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
            if (binding.Origin is { } origin) CaptureDefinition(frame, span, [origin]);
            if (binding.Self is not null) EmitClosure(frame, binding.Self);
            else frame.Emit(OpCode.LoadLocal, binding.Slot, span: span);
            var current = Current(frame, binding);
            return current with { Storage = binding.Storage };
        }
        if (reference && name == "std") return Standard(frame, span);
        if (reference && RemovedRangeAlias(name)) throw RangeAliasError(frame, "$" + name, span);
        if (reference && name is "local" or "wsl" or "pwd" or "file" or "env" or "path")
        {
            frame.Emit(OpCode.LoadBuiltin, text: name, span: span);
            ConnectionValue connection = name switch
            {
                "wsl" => KnownEndpoints.Wsl,
                "env" => KnownEndpoints.Environment,
                "path" => KnownEndpoints.SearchPath,
                "pwd" => KnownEndpoints.WorkingDirectory,
                "file" => KnownEndpoints.SourceFile,
                _ => KnownEndpoints.Local
            };
            return new(TypeRef.Connection, KnownConnection: connection);
        }
        return Path(frame, name, span);
    }
    private static bool RemovedRangeAlias(string name) => name is "view" or "views" or "ranges";
    private static CompileException RangeAliasError(Frame frame, string name, SourceSpan span) =>
        Error(frame, $"The predefined range alias '{name}' has been removed.", span,
            help: "Use '$std::ranges' for range functions, or declare your own lexical binding explicitly.", code: "E0210");
    private static class KnownEndpoints
    {
        public static readonly ConnectionValue Local = new(ConnectionKind.Local);
        public static readonly ConnectionValue WorkingDirectory = new(ConnectionKind.Local, "pwd");
        public static readonly ConnectionValue SourceFile = new(ConnectionKind.Local, "file");
        public static readonly ConnectionValue Wsl = new(ConnectionKind.Wsl);
        public static readonly ConnectionValue Environment = new(ConnectionKind.Environment);
        public static readonly ConnectionValue SearchPath = new(ConnectionKind.SearchPath);
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
        var left = Current(frame, Expression(frame, binary.Left));
        if (binary.Operator == "/")
        {
            int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
            CaptureReceiver(frame, new(start, binary.Right.Span.End - start), left, EditorReceiverKind.PathJoin, binary);
        }
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
                frame.Emit(OpCode.PathJoin, span: binary.Span); return new(TypeRef.Path, KnownPath: JoinedPath(left, right));
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
                if (left.Type.Nullable || right.Type.Nullable) throw Error(frame, "Null has no boolean comparison; use 'is null' or 'is not null'.", binary.Span,
                    label: "this comparison includes a possibly Null value",
                    help: "Use 'value is null' or 'value is not null' for a Null test. Handle Null before comparing other values.", code: "E0201");
                var comparable = new TypeRef(TypeKind.Bool | TypeKind.String | TypeKind.Path | TypeKind.Number | TypeKind.Type | TypeKind.Connection);
                Require(frame, left.Type, comparable, binary.Left.Span); Require(frame, right.Type, comparable, binary.Right.Span);
                if (left.Type != right.Type && !(PathCompatible.Accepts(left.Type) && PathCompatible.Accepts(right.Type)))
                    throw Error(frame, $"Cannot compare {left.Type} and {right.Type}.", binary.Span,
                        label: $"left: {left.Type}; right: {right.Type}",
                        help: "Compare compatible values. Path and String can be compared implicitly; other types must agree.", code: "E0203");
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
        var before = SnapshotFacts(frame);
        frame.Environment = Refined(frame, binary.Left, binary.Operator == "and");
        var right = Expression(frame, binary.Right); Require(frame, right.Type, TypeRef.Bool, binary.Right.Span);
        frame.Facts = left.KnownBool is { } known
            ? known == (binary.Operator == "and") ? frame.Facts : before
            : JoinFacts(before, frame.Facts);
        RestoreScope(frame, old);
        frame.Patch(skip, frame.Code.Count);
        return new(TypeRef.Bool);
    }

    private Info Select(Frame frame, BinaryExpr binary)
    {
        if (binary.Left is NameExpr alias && RemovedRangeAlias(alias.Name) && !frame.Environment.ContainsKey(alias.Name))
            throw RangeAliasError(frame, alias.Name, alias.Span);
        var left = Expression(frame, binary.Left);
        int start = binary.OperatorSpan?.End ?? binary.Right.Span.Start;
        CaptureReceiver(frame, new(start, binary.Right.Span.End - start), left);
        if (frame.EditorRecovery && binary.Right is MissingExpr && left.Type != TypeRef.Connection) return UnknownInfo;
        var member = binary.Right switch { NameExpr name => name.Name, PathExpr path => path.Path, StringExpr text => text.Value, _ => null };
        if ((left.Type == TypeRef.Scope || left.Type == TypeRef.Process) && member is null)
            throw Error(frame, $"A {left.Type} member must be selected by name.", binary.Right.Span,
                label: "a computed expression is not a member name",
                help: "Write 'value::member' or use a literal String member name.", code: "E0205");
        if (left.Type == TypeRef.Scope && member is not null)
        {
            if (left.Scope is null || !left.Scope.Members.TryGetValue(member, out var info))
                throw Error(frame, $"Scope has no member '{member}'.", binary.Right.Span,
                    label: "no member with this name",
                    help: left.Scope is null ? "Select a declared member with scope::member."
                        : $"Available members: {string.Join(", ", left.Scope.Members.Keys.Order(StringComparer.Ordinal))}.", code: "E0205");
            Observe(frame, binary.Right.Span, info, member);
            if (left.Scope.Origins?.TryGetValue(member, out var origins) == true) CaptureDefinition(frame, binary.Right.Span, origins);
            frame.Emit(OpCode.GetMember, text: member, span: binary.Span);
            info = Current(frame, info);
            return info with { Storage = left.Scope.Storage[member] };
        }
        if (left.Type == TypeRef.Process && member is not null)
        {
            if (!ProcessCapabilities.Members.TryGetValue(member, out TypeRef? type))
                throw Error(frame, $"Process has no member '{member}'.", binary.Right.Span,
                    help: $"Available Process members: {string.Join(", ", ProcessCapabilities.Members.Keys)}.", code: "E0205");
            frame.Emit(OpCode.GetMember, text: member, span: binary.Span);
            return new(type);
        }
        if (left.Type != TypeRef.Connection)
            throw Error(frame, member is null ? $"Cannot use '::' on a {left.Type} value." : $"Cannot select '{member}' from a {left.Type} value.", binary.Left.Span,
                label: $"this is {left.Type}, not a Scope or Connection",
                note: "'::' selects a member of a lexical Scope, or qualifies a path with a Connection.",
                help: left.Type == TypeRef.Path
                    ? "Pass the Scope itself, not one of its Path members. An unbound bare name is a Path; use $local, $wsl or an SSH connection for endpoint qualification."
                    : "Use a Scope value for member access, or a Connection value for endpoint qualification.", code: "E0206");
        var first = BeginPhysicalPathFlow(frame, binary);
        var right = Expression(frame, binary.Right);
        CompletePhysicalPathFlow(binary.Right, first, left.KnownConnection);
        Require(frame, right.Type, PathCompatible, binary.Right.Span);
        frame.Emit(OpCode.Qualify, span: binary.Span); return new(TypeRef.Path, KnownPath: QualifiedPath(left, right));
    }

    private static PathValue? LiteralPath(Info info) => info.KnownPath
        ?? (info.KnownText is { } text ? new PathValue(text) : null);

    private PathValue? JoinedPath(Info left, Info right)
    {
        if (observe is null) return null;
        var path = LiteralPath(left);
        Value? item = right.KnownPath is { } rightPath ? rightPath : right.KnownText is { } text ? new StringValue(text) : null;
        return path is not null && item is not null ? PathSemantics.JoinValue(path, item) : null;
    }

    private PathValue? QualifiedPath(Info left, Info right)
    {
        if (observe is null) return null;
        var path = LiteralPath(right);
        if (path is null || left.KnownConnection is not { } connection) return null;
        return QualifyKnownPath(path, connection);
    }

    internal static bool HasStaticPathProjection(ConnectionValue? connection) =>
        connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } or { Kind: ConnectionKind.Local, Address.Length: 0 };

    internal static PathValue? QualifyKnownPath(PathValue path, ConnectionValue? connection)
    {
        bool deferredFile = path.Connection is { Kind: ConnectionKind.Local, Address: "file" }
            && !(OperatingSystem.IsWindows() ? System.IO.Path.IsPathFullyQualified(path.NativeText) : System.IO.Path.IsPathRooted(path.Raw));
        if (deferredFile && connection is not { Kind: ConnectionKind.Local, Address: "file" }) return null;
        if (connection is { Kind: ConnectionKind.Local, Address: "pwd" } && path.Raw != "pwd")
            return PathSemantics.Qualify(path, new(ConnectionKind.Local));
        if (connection is { Kind: ConnectionKind.Local, Address: "file" } && path.Raw is not ("this" or "parent"))
            return PathSemantics.Qualify(path, connection) with { WorkingDirectory = null, HomeDirectory = null };
        return HasStaticPathProjection(connection) ? PathSemantics.Qualify(path, connection!) : null;
    }

    private Info Scope(Frame frame, ScopeExpr scope, bool named = false)
    {
        var old = frame.Environment;
        var owner = frame.ContextOwner;
        bool includesEnd = frame.ContextIncludesEnd;
        frame.ContextOwner = scope.Span;
        frame.ContextIncludesEnd = scope.EditorIncomplete;
        CaptureContext(frame, scope.Span.Start);
        Dictionary<string, Info> members = [];
        Dictionary<string, TypeRef?> storageTypes = [];
        HashSet<string> nullableMembers = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<EditorLocation>>? origins = editorContexts is null ? null : new(StringComparer.Ordinal);
        List<string> keys = [];
        List<int> fieldSlots = [];
        Dictionary<string, Binding> fieldBindings = [];
        foreach (var member in scope.Members)
        {
            if (members.ContainsKey(member.Name))
            {
                var first = scope.Members.First(item => item.Name == member.Name);
                var error = Error(frame, $"Scope field '{member.Name}' is declared more than once.", member.NameSpan ?? member.Span,
                    label: "duplicate field declaration", help: "Rename this field, or assign to the existing field instead of declaring it again.", code: "E0212");
                error.Diagnostic.Related.Add(new(frame.SourceName, frame.Source, first.NameSpan ?? first.Span, "first field declared here"));
                throw error;
            }
            CaptureContext(frame, member.Span.Start);
            var info = Expression(frame, member.Value);
            if (info.Type.Nullable && !member.Nullable && !info.EditorUnknown)
                throw Error(frame, $"Scope field '{member.Name}' does not admit Null.", member.Value.Span,
                    label: $"this value may be Null ({info.Type})",
                    help: $"Declare '{member.Name}?: ...' to allow Null in this field.",
                    fix: member.NameSpan is { } fieldName ? new("Allow Null in this field", new(fieldName.End, 0), "?") : null,
                    code: "E0201");
            if (member.Nullable) nullableMembers.Add(member.Name);
            storageTypes[member.Name] = StorageType(info.Type, member.Nullable);
            if (member.NameSpan is { } memberName) Observe(frame, memberName, info, member.Name);
            Store(frame, member.Name, info, member.NameSpan, member.Nullable);
            fieldSlots.Add(frame.Environment[member.Name].Slot);
            fieldBindings[member.Name] = frame.Environment[member.Name];
            if (origins is not null && frame.Environment[member.Name].Origin is { } origin) origins[member.Name] = [origin];
            keys.Add(member.Name); members[member.Name] = info;
            CaptureContext(frame, member.Span.End);
        }
        if (scope.Value is not null) { Expression(frame, scope.Value); frame.Emit(OpCode.Pop); }
        foreach (int slot in fieldSlots) frame.Emit(OpCode.LoadStorage, slot);
        if (!scopeAllocationSites.TryGetValue(scope, out int allocation)) scopeAllocationSites.Add(scope, allocation = ++nextScopeAllocation);
        frame.Emit(OpCode.MakeScope, keys.Count, allocation, text: string.Join('\0', keys), span: scope.Span);
        CaptureContext(frame, scope.Span.End);
        RestoreScope(frame, old);
        frame.ContextOwner = owner;
        frame.ContextIncludesEnd = includesEnd;
        if (!frame.ScopeSites.TryGetValue(scope, out var identity)) frame.ScopeSites.Add(scope, identity = new());
        Info result = new(TypeRef.Scope, Scope: new(members, named, origins, nullableMembers, storageTypes)
        {
            Identity = identity,
            AllocationSite = allocation,
            FieldBindings = fieldBindings,
            Storage = fieldBindings.ToDictionary(pair => pair.Key, pair => pair.Value.Storage, StringComparer.Ordinal)
        });
        frame.Facts = frame.Facts.SetItem(result.Scope!.Identity, result);
        RegisterScope(frame, result.Scope);
        return result;
    }


}
