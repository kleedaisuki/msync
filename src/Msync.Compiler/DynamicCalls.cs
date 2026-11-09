using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private sealed class MutableCall(Frame caller, StorageConstraint storage, IReadOnlyList<Info> arguments,
        SourceSpan span, IReadOnlyList<Expr>? expressions, CallableContract contract)
    {
        public Frame Caller { get; } = caller;
        public StorageConstraint Storage { get; } = storage;
        public IReadOnlyList<Info> Arguments { get; } = arguments;
        public SourceSpan Span { get; } = span;
        public IReadOnlyList<Expr>? Expressions { get; } = expressions;
        public Info Result { get; set; } = contract.Result;
        public CallableContract Contract { get; } = contract;
        public int Revision { get; set; } = -1;
        public string? Dispatch { get; set; }
        public int? DirectTarget { get; set; }
    }
    // Retained compiler-pool sites let later submissions relink live closures.
    // Artifacts and interpreters receive only rendered bytecode dispatch.
    private const string MutableCallPrefix = "@msync-cell:";
    private readonly List<MutableCall?> mutableCalls = [];
    private readonly Queue<int> freeMutableCalls = [];
    private readonly List<int> newMutableCalls = [];
    private int callableRevision;
    private readonly Dictionary<StorageConstraint, (Info? Domain, int Revision, System.Collections.Immutable.ImmutableHashSet<StorageConstraint> Sources,
        System.Collections.Immutable.ImmutableDictionary<string, CallableContract> Contracts)> changedCallableStorage = [];

    private void ObserveCallableStore(StorageConstraint storage, Info value)
    {
        if (value.Type.WithoutNull != TypeRef.Function) return;
        if (value.Storage is { } source && !ReferenceEquals(source, storage) && !storage.Sources.Contains(source))
        {
            RememberCallableStorage(storage);
            storage.Sources = storage.Sources.Add(source);
        }
        var choices = CallableChoices(value).Where(candidate => candidate.Function is not null || candidate.NativeName is not null).ToArray();
        if (choices.Length == 0) return;
        var existing = storage.Callables;
        foreach (var choice in choices)
        {
            if (existing is not null && CallableChoices(existing).Any(candidate => candidate.Function == choice.Function && candidate.NativeName == choice.NativeName)) continue;
            RememberCallableStorage(storage);
            var item = choice with { Storage = null };
            existing = existing is null ? item : Join(existing, item);
            storage.Callables = existing;
            storage.Revision++;
            callableRevision++;
        }
    }

    private void RememberCallableStorage(StorageConstraint storage) =>
        changedCallableStorage.TryAdd(storage, (storage.Callables, storage.Revision, storage.Sources, storage.Contracts));

    private void RefreshCallableDomain(StorageConstraint storage)
    {
        HashSet<StorageConstraint> visited = [];
        void RefreshDomain(StorageConstraint destination)
        {
            if (!visited.Add(destination)) return;
            foreach (var source in destination.Sources)
            {
                RefreshDomain(source);
                if (source.Callables is { } domain) ObserveCallableStore(destination, domain);
            }
        }
        RefreshDomain(storage);
    }

    private Info RecordMutableCall(Frame frame, Info callee, IReadOnlyList<Info> arguments, SourceSpan span,
        IReadOnlyList<Expr>? expressions, Info result)
    {
        if (callee.Storage is not { } storage || frame.EditorProbe && !frame.EditorReportErrors) return result;
        ObserveCallableStore(storage, callee);
        string signature = string.Join(';', arguments.Select(InfoKey));
        if (!storage.Contracts.TryGetValue(signature, out var contract))
        {
            RememberCallableStorage(storage);
            contract = new(CallableSignatureResult(frame, callee, arguments, result), frame.Source, frame.SourceName, span);
            storage.Contracts = storage.Contracts.Add(signature, contract);
        }
        int id = freeMutableCalls.TryDequeue(out var reused) ? reused : mutableCalls.Count;
        MutableCall call = new(CloneFrame(frame), storage, arguments.ToArray(), span, expressions, contract);
        CheckCallableResult(call, callee, result);
        if (id == mutableCalls.Count) mutableCalls.Add(call);
        else mutableCalls[id] = call;
        newMutableCalls.Add(id);
        frame.Code[^1] = frame.Code[^1] with { B = 0, Text = MutableCallPrefix + id };
        return frame.DeferredCaptures && contract.Result.Type.Accepts(result.Type) ? Retype(result, contract.Result.Type) : result;
    }

    private IReadOnlyList<FunctionCode> FinalizeMutableCalls()
    {
        bool changed;
        int revision;
        var active = ActiveMutableCalls();
        do
        {
            changed = false;
            revision = callableRevision;
            for (int index = 0; index < mutableCalls.Count; index++)
            {
                if (!active.Contains(index) || mutableCalls[index] is not { } call) continue;
                RefreshCallableDomain(call.Storage);
                if (call.Revision == call.Storage.Revision) continue;
                Dictionary<int, int> targets = [];
                var choices = CallableChoices(call.Storage.Callables ?? new Info(TypeRef.Function)).ToArray();
                MergeCaptureDomains(choices);
                foreach (var candidate in choices)
                {
                    Info result;
                    if (candidate.Function is { } function)
                    {
                        var specialization = Specialize(call.Caller, function, call.Arguments, call.Span, call.Expressions);
                        result = specialization.Result;
                        targets.TryAdd(function.Id, specialization.Id);
                    }
                    else if (candidate.NativeName is { } native)
                    {
                        var scratch = CloneFrame(call.Caller);
                        result = InvokeNative(scratch, native, call.Arguments, call.Span, call.Expressions);
                    }
                    else continue;
                    CheckCallableResult(call, candidate, result);
                }
                call.Dispatch = targets.Count == 0 ? null : string.Join(';', targets.Select(pair => $"{pair.Key}:{pair.Value}"));
                call.DirectTarget = targets.Count == 1 ? targets.Values.Single() : null;
                call.Revision = call.Storage.Revision;
                changed = true;
            }
            active = ActiveMutableCalls();
        } while (changed && (revision != callableRevision || active.Any(index => mutableCalls[index] is { } call && call.Revision != call.Storage.Revision)));
        return functions.Select(RenderMutableFunction).ToArray();
    }

    private FunctionCode RenderMutableFunction(FunctionCode function) =>
        function.Instructions.Any(instruction => instruction.Op == OpCode.Call && instruction.Text?.StartsWith(MutableCallPrefix, StringComparison.Ordinal) == true)
            ? function with { Instructions = function.Instructions.Select(RenderMutableCall).ToArray() } : function;

    private Instruction RenderMutableCall(Instruction instruction)
    {
        if (instruction.Op != OpCode.Call || instruction.Text?.StartsWith(MutableCallPrefix, StringComparison.Ordinal) != true) return instruction;
        var call = mutableCalls[int.Parse(instruction.Text.AsSpan(MutableCallPrefix.Length), System.Globalization.CultureInfo.InvariantCulture)]!;
        return call.DirectTarget is { } direct ? instruction with { B = direct + 1, Text = null } : instruction with { B = 0, Text = call.Dispatch };
    }

    private HashSet<int> ActiveMutableCalls() => functions.SelectMany(function => function.Instructions)
        .Where(instruction => instruction.Op == OpCode.Call && instruction.Text?.StartsWith(MutableCallPrefix, StringComparison.Ordinal) == true)
        .Select(instruction => int.Parse(instruction.Text!.AsSpan(MutableCallPrefix.Length), System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private void PruneMutableCalls()
    {
        var active = ActiveMutableCalls();
        for (int index = 0; index < mutableCalls.Count; index++)
            if (!active.Contains(index)) mutableCalls[index] = null;
        RebuildFreeMutableCalls();
    }

    private void RebuildFreeMutableCalls()
    {
        while (mutableCalls.Count > 0 && mutableCalls[^1] is null) mutableCalls.RemoveAt(mutableCalls.Count - 1);
        freeMutableCalls.Clear();
        for (int index = 0; index < mutableCalls.Count; index++) if (mutableCalls[index] is null) freeMutableCalls.Enqueue(index);
    }

    private void MergeCaptureDomains(IReadOnlyList<Info> choices)
    {
        foreach (var group in choices.Where(choice => choice.Function is not null).GroupBy(choice => choice.Function!.Id))
        {
            var functions = group.Select(choice => choice.Function!).ToArray();
            for (int index = 0; index < functions[0].Captures.Count; index++)
            {
                var storage = functions.Select(function => function.Captures[index].Value.Storage).Distinct().ToArray();
                foreach (var destination in storage)
                    foreach (var source in storage)
                        if (!ReferenceEquals(destination, source) && source.Callables is { } domain) ObserveCallableStore(destination, domain);
            }
        }
    }

    private static Info DeferredCapture(Frame frame, Binding binding)
    {
        Dictionary<object, ScopeInfo> scopes = [];
        Info Widen(Info info, TypeRef? constraint)
        {
            info = CaptureInfo(info) with { Type = constraint ?? info.Type };
            if (info.Scope is not { } scope) return info;
            if (!scopes.TryGetValue(scope.Identity, out var widened))
            {
                Dictionary<string, Info> members = [];
                widened = scope with { Members = members };
                scopes.Add(scope.Identity, widened);
                foreach (var (name, member) in scope.Members) members[name] = Widen(member, scope.Storage[name].Type ?? TypeRef.Any);
            }
            return info with { Scope = widened };
        }
        return Widen(Current(frame, binding), binding.Storage.Type);
    }

    private static void InstallCaptureFacts(Frame frame, Info info)
    {
        HashSet<object> visited = [];
        void Install(Info value)
        {
            if (value.Scope is not { } scope || !visited.Add(scope.Identity)) return;
            frame.Facts = frame.Facts.SetItem(scope.Identity, value);
            foreach (var (name, member) in scope.Members)
            {
                if (scope.FieldBindings.TryGetValue(name, out var field)) frame.Facts = frame.Facts.SetItem(field.Identity, member);
                Install(member);
            }
        }
        Install(info);
    }

    private static bool CompatibleResult(Info expected, Info actual) => CompatibleResult(expected, actual, []);

    private Info CallableSignatureResult(Frame caller, Info callee, IReadOnlyList<Info> arguments, Info result)
    {
        if (callee.Function is not { } function || function.Syntax.ReturnType is not null) return result with { Storage = null };
        if (!Accessor(function.Syntax.Body)) return result.Storage?.Type is { } type ? result with { Type = type } : result;
        Frame frame = new(function.Source, function.SourceName) { Facts = caller.Facts, EditorProbe = true, Concepts = function.Concepts };
        foreach (var (name, binding) in function.Captures)
        {
            var info = DeferredCapture(caller, binding);
            frame.Bind(name, binding with { Slot = frame.Allocate(), Info = info });
            InstallCaptureFacts(frame, info);
        }
        frame.GlobalBindings = frame.Environment;
        for (int index = 0; index < arguments.Count; index++)
        {
            var parameter = function.Syntax.Parameters[index];
            var info = CaptureInfo(arguments[index]);
            if (parameter.Type is { } annotation)
            {
                var type = ResolveType(frame, annotation);
                if (parameter.Nullable) type = type.WithNull;
                if (!type.Accepts(info.Type)) info = Retype(info, type);
            }
            frame.Bind(parameter.Name, new(frame.Allocate(), info, Constraint: info.Type));
        }
        try
        {
            var signature = Block(frame, function.Syntax.Body);
            foreach (var returned in frame.Returns) signature = Join(signature, returned);
            return signature;
        }
        catch (CompileException) { return result.Storage?.Type is { } type ? result with { Type = type } : result; }
    }

    private static bool Accessor(Node node) => node switch
    {
        NameExpr or PathExpr or StringExpr or NumberExpr or BoolExpr or NullExpr or TypeExpr => true,
        BlockExpr block => block.Statements.Count(statement => statement is not GlobalStatement) switch
        {
            0 => true,
            1 => Accessor(block.Statements.First(statement => statement is not GlobalStatement)),
            _ => false
        },
        ExpressionStatement statement => Accessor(statement.Expression),
        ReturnExpr { Value: not null } returned => Accessor(returned.Value),
        UnaryExpr { Operator: "?" or "not" or "$" } unary => Accessor(unary.Operand),
        BinaryExpr { Operator: "::" or "is" or "is not" or "and" or "or" } binary => Accessor(binary.Left) && Accessor(binary.Right),
        IfExpr conditional => Accessor(conditional.Condition) && Accessor(conditional.Then) && (conditional.Else is null || Accessor(conditional.Else)),
        MatchExpr match => Accessor(match.Value) && match.Arms.All(arm => Accessor(arm.Value)),
        _ => false
    };

    private static bool CompatibleResult(Info expected, Info actual, HashSet<(object, object)> visited)
    {
        if (!expected.Type.Accepts(actual.Type)) return false;
        if (actual.Type.Kind == TypeKind.Never) return true;
        if (expected.Scope is not { } shape) return true;
        if (actual.Scope is not { } other || shape.Members.Count != other.Members.Count) return false;
        if (!visited.Add((shape.Identity, other.Identity))) return true;
        foreach (var (name, member) in shape.Members)
            if (!other.Members.TryGetValue(name, out var value) || !CompatibleResult(member, value, visited)
                || (shape.NullableMembers?.Contains(name) == true) != (other.NullableMembers?.Contains(name) == true)) return false;
        return true;
    }

    private static string CallableResultText(Info info)
    {
        HashSet<object> visited = [];
        string Describe(Info value)
        {
            if (value.Scope is not { } scope || !visited.Add(scope.Identity)) return value.Type.ToString();
            return "Scope { " + string.Join(", ", scope.Members.Select(pair => pair.Key
                + (scope.NullableMembers?.Contains(pair.Key) == true ? "?" : "") + ": " + Describe(pair.Value))) + " }";
        }
        return Describe(info);
    }

    private void CheckCallableResult(MutableCall call, Info candidate, Info result)
    {
        result = CallableSignatureResult(call.Caller, candidate, call.Arguments, result);
        if (call.Result.Storage?.Type is { } type) call.Result = call.Result with { Type = type };
        if (call.Result.Type.Kind == TypeKind.Never) call.Result = result;
        if (call.Caller.EditorRecovery && (call.Result.EditorUnknown || result.EditorUnknown)) return;
        if (CompatibleResult(call.Result, result)) return;
        var function = candidate.Function;
        string actual = CallableResultText(result), expected = CallableResultText(call.Result);
        var error = function is null
            ? Error(call.Caller, $"This callable returns {actual}, but the binding's call contract requires {expected}.", call.Span,
                help: "Callable bindings keep their inferred signature. Use a compatible function or an explicit shared return union.", code: "E0213")
            : new CompileException($"This function returns {actual}, but the binding's call contract requires {expected}.",
                function.Source, function.SourceName, function.Syntax.ReturnType?.Span ?? function.Syntax.Span,
                label: "incompatible callable return type", help: "Callable bindings keep their inferred signature. Use a compatible function or an explicit shared return union.", code: "E0213");
        error.Diagnostic.Related.Add(new(call.Contract.SourceName, call.Contract.Source, call.Contract.Span,
            $"this call established the {expected} return contract for ({string.Join(", ", call.Arguments.Select(argument => argument.Type))})"));
        throw error;
    }
}
