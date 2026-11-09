using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private static TypeRef? StorageType(TypeRef type, bool nullable) =>
        type.WithoutNull.Kind == TypeKind.Never ? null : nullable ? type.WithNull : type;

    private static Info Current(Frame frame, Binding binding) =>
        Current(frame, frame.Facts.GetValueOrDefault(binding.Identity, binding.Info));

    private static Info Current(Frame frame, Info info)
    {
        if (info.Indexed is { } indexed) return info with { Type = new(TypeKind.Range, indexed.Element.Type), Element = IndexedElement(indexed) };
        if (info.Scope is not { } scope) return info;
        if (scope.Memory.View is { } view)
        {
            var version = IndexedVersion(frame, scope.Memory.Clock);
            if (frame.Facts.TryGetValue(scope.Identity, out var known) && known.Scope is { } refined)
                scope = refined;
            if (version is not null && ReferenceEquals(scope.MemoryVersion, version)) return info with { Scope = scope };
            return info with { Scope = scope with { Members = IndexedMembers(view), FieldBindings = view.FieldBindings, MemoryVersion = version } };
        }
        if (frame.Facts.TryGetValue(scope.Identity, out var current)) scope = current.Scope!;
        return ReferenceEquals(info.Scope, scope) ? info : info with { Scope = scope };
    }

    private static void Refresh(Frame frame)
    {
        foreach (var (name, binding) in frame.Environment)
        {
            var info = Current(frame, binding);
            if (!ReferenceEquals(info, binding.Info)) frame.Bind(name, binding with { Info = info });
        }
    }

    private static void RestoreScope(Frame frame, ImmutableDictionary<string, Binding> environment)
    {
        frame.Environment = environment;
        Refresh(frame);
    }

    private void Update(Frame frame, Binding binding, Info info)
    {
        ObserveCallableStore(binding.Storage, info);
        frame.Facts = frame.Facts.SetItem(binding.Identity, info);
        if (frame.FieldOwners.TryGetValue(binding.Identity, out var owners))
            foreach (var (identity, field) in owners)
            {
                if (!frame.Facts.TryGetValue(identity, out var value) || value.Scope is not { } scope) continue;
                var members = scope.Members.ToDictionary();
                members[field] = info;
                frame.Facts = frame.Facts.SetItem(identity, value with { Scope = scope with { Members = members } });
            }
        Refresh(frame);
    }

    private static void RegisterScope(Frame frame, ScopeInfo scope)
    {
        foreach (var (name, binding) in scope.FieldBindings)
        {
            if (!frame.FieldOwners.TryGetValue(binding.Identity, out var owners)) frame.FieldOwners.Add(binding.Identity, owners = []);
            owners.Add((scope.Identity, name));
        }
    }

    private Info StoreInfo(Frame frame, Info value, TypeRef? constraint, bool nullable, SourceSpan span, string name)
    {
        if (value.Type.Nullable && !nullable && !value.EditorUnknown)
            throw Error(frame, $"'{name}' does not admit Null.", span,
                label: $"this value may be Null ({value.Type})",
                help: $"Add '?' to the declaration of '{name}', or handle Null before assigning.", code: "E0201");
        if (constraint is null || value.EditorUnknown) return value;
        CheckConversion(frame, value.Type, constraint, span);
        if (constraint.Accepts(value.Type)) return value;
        Convert(frame, value.Type, constraint, span);
        return Retype(value, value.Type.Nullable ? constraint : constraint.WithoutNull);
    }

    private Info Assign(Frame frame, AssignmentExpr assignment)
    {
        if (assignment.Target is NameExpr name)
        {
            if (!frame.Environment.TryGetValue(name.Name, out var binding) || binding.Self is not null)
                throw Error(frame, $"Cannot assign to undeclared binding '{name.Name}'.", name.Span,
                    help: $"Declare it first with 'let {name.Name} = ...;'.", code: "E0211");
            var value = Expression(frame, assignment.Value);
            value = StoreInfo(frame, value, binding.Storage.Type, binding.AdmitsNull, assignment.Value.Span, name.Name);
            CheckCastTargetStore(frame, binding.Storage, value, assignment.Value.Span);
            CheckConceptStore(frame, binding.Storage, value, assignment.Value.Span);
            if (binding.Storage.Type is null && StorageType(value.Type, binding.AdmitsNull) is { } solved)
            {
                Solve(binding.Storage, solved);
                binding = binding with { Constraint = solved };
                frame.Bind(name.Name, binding);
            }
            frame.Emit(OpCode.StoreLocal, binding.Slot, span: assignment.Span);
            Update(frame, binding, value);
            Observe(frame, name.Span, value, name.Name);
            if (binding.Origin is { } origin) CaptureDefinition(frame, name.Span, [origin]);
            frame.Emit(OpCode.Null);
            return new(TypeRef.Null);
        }
        if (assignment.Target is BinaryExpr { Operator: "::" } member && MemberName(member.Right) is { } field)
        {
            var receiver = Current(frame, Expression(frame, member.Left));
            if (receiver.Type.Nullable && receiver.Scope is not null)
                throw TypeError(frame, receiver.Type, TypeRef.Scope, member.Left.Span);
            int start = member.OperatorSpan?.End ?? member.Right.Span.Start;
            CaptureReceiver(frame, new(start, member.Right.Span.End - start), receiver);
            if (receiver.Scope is not { } scope || !scope.Members.TryGetValue(field, out var previous))
                throw Error(frame, $"Cannot assign field '{field}' on {receiver.Type}.", member.Span,
                    help: "Assign a declared member of a lexical Scope; endpoint-qualified paths are not fields.", code: "E0211");
            var value = Expression(frame, assignment.Value);
            bool nullable = scope.NullableMembers?.Contains(field) == true;
            var storage = scope.Storage[field];
            var constraint = storage.Type;
            value = StoreInfo(frame, value, constraint, nullable, assignment.Value.Span, field);
            CheckCastTargetStore(frame, storage, value, assignment.Value.Span);
            CheckConceptStore(frame, storage, value, assignment.Value.Span);
            ObserveCallableStore(storage, value);
            if (constraint is null && StorageType(value.Type, nullable) is { } solved) Solve(storage, solved);
            var members = scope.Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var types = scope.StorageTypes?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? scope.Members.ToDictionary(pair => pair.Key, pair => StorageType(pair.Value.Type, scope.NullableMembers?.Contains(pair.Key) == true), StringComparer.Ordinal);
            if (scope.Memory.View is not null)
            {
                DynamicElement(frame, value);
                AdvanceIndexedMemory(frame, scope.Memory.Clock);
                members = IndexedMembers(scope.Memory.View);
            }
            members[field] = value;
            types[field] = constraint ?? StorageType(value.Type, nullable);
            var updated = scope with { Members = members, StorageTypes = types,
                MemoryVersion = scope.Memory.View is null ? null : IndexedVersion(frame, scope.Memory.Clock) };
            frame.Facts = frame.Facts.SetItem(scope.Identity, receiver with { Scope = updated });
            if (scope.FieldBindings.TryGetValue(field, out var fieldBinding)) frame.Facts = frame.Facts.SetItem(fieldBinding.Identity, value);
            Refresh(frame);
            frame.Emit(OpCode.SetMember, FieldLayoutHint(scope, field), text: field, span: assignment.Span);
            Observe(frame, member.Right.Span, value, field);
            if (scope.Origins?.TryGetValue(field, out var origins) == true) CaptureDefinition(frame, member.Right.Span, origins);
            return new(TypeRef.Null);
        }
        throw Error(frame, "The left side of '=' is not writable.", assignment.Target.Span,
            help: "Assign a binding name or a declared Scope member, such as 'object::field = value;'.", code: "E0211");
    }

    private static string? MemberName(Expr expression) => expression switch
    {
        NameExpr name => name.Name,
        PathExpr path => path.Path,
        StringExpr text => text.Value,
        _ => null
    };

    private static Info? StorageInfo(Frame frame, Expr target) => target switch
    {
        NameExpr name when frame.Environment.TryGetValue(name.Name, out var binding) => Current(frame, binding),
        BinaryExpr { Operator: "::" } member when MemberName(member.Right) is { } name
            && StorageInfo(frame, member.Left)?.Scope is { } scope && scope.Members.TryGetValue(name, out var info) => Current(frame, info),
        _ => null
    };

    private static Info RefineInfo(Info info, TypeRef type, bool positive, bool denoted = false)
    {
        var kinds = positive ? info.Type.Kind & type.Kind : info.Type.Kind & ~type.Kind;
        if (denoted && (info.Type.Kind & TypeKind.Type) != 0)
        {
            kinds |= TypeKind.Type;
            bool exact = type.Kind != TypeKind.Range && type.Kind != TypeKind.Never
                && ((ushort)type.Kind & ((ushort)type.Kind - 1)) == 0;
            if (positive && kinds == TypeKind.Type && exact) return info with { Type = TypeRef.Type, TypeValue = type };
        }
        return info with { Type = new(kinds, info.Type.Element) };
    }

    private static ImmutableDictionary<string, Binding> RefineMember(Frame frame, Expr target, TypeRef type, bool positive, bool denoted = false)
    {
        if (target is not BinaryExpr { Operator: "::" } member || MemberName(member.Right) is not { } name
            || StorageInfo(frame, member.Left)?.Scope is not { } scope || !scope.Members.TryGetValue(name, out var info))
            return frame.Environment;
        var refined = RefineInfo(info, type, positive, denoted);
        var members = scope.Members.ToDictionary();
        members[name] = refined;
        frame.Facts = frame.Facts.SetItem(scope.Identity, new(TypeRef.Scope, Scope: scope with { Members = members }));
        if (scope.FieldBindings.TryGetValue(name, out var binding)) frame.Facts = frame.Facts.SetItem(binding.Identity, refined);
        Refresh(frame);
        return frame.Environment;
    }

    private static ImmutableDictionary<object, Info> JoinFacts(ImmutableDictionary<object, Info> left,
        ImmutableDictionary<object, Info> right)
    {
        var result = left;
        foreach (var (identity, info) in right)
            result = result.SetItem(identity, result.TryGetValue(identity, out var previous) ? Join(previous, info) : info);
        return result;
    }

    private static ImmutableDictionary<object, Info> SnapshotFacts(Frame frame)
    {
        void Scope(Info info)
        {
            if (info.Scope is not { } scope || frame.Facts.ContainsKey(scope.Identity)) return;
            frame.Facts = frame.Facts.SetItem(scope.Identity, info);
            RegisterScope(frame, scope);
            foreach (var member in scope.Members.Values) Scope(member);
        }
        foreach (var binding in frame.Environment.Values)
        {
            var info = Current(frame, binding);
            frame.Facts = frame.Facts.SetItem(binding.Identity, info);
            Scope(info);
        }
        return frame.Facts;
    }

    private void PrepareLoop(Frame frame, Action<Frame> analyze)
    {
        var environment = frame.Environment;
        var domain = SnapshotFacts(frame);
        string Signature(ImmutableDictionary<object, Info> facts)
        {
            HashSet<object> scopes = [];
            string Key(Info info)
            {
                if (info.Scope is { } scope)
                {
                    if (facts.TryGetValue(scope.Identity, out var current)) scope = current.Scope!;
                    if (!scopes.Add(scope.Identity)) return "Scope";
                    return "{" + string.Join(',', scope.Members.Select(pair => pair.Key + ':' + Key(pair.Value))) + "}";
                }
                // Joins rebuild Scope metadata without changing its abstract shape,
                // including when the Scope is an item of a Range.
                return InfoKey(info, Key);
            }
            return string.Join(';', environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Key(facts.GetValueOrDefault(pair.Value.Identity, pair.Value.Info))));
        }
        var signature = Signature(domain);
        while (true)
        {
            frame.Facts = domain;
            RestoreScope(frame, environment);
            var probe = Frame.Fork(frame);
            probe.EditorReportErrors = false;
            analyze(probe);
            var next = JoinFacts(domain, probe.Facts);
            var updated = Signature(next);
            domain = next;
            if (updated == signature) break;
            signature = updated;
        }
        frame.Facts = domain;
        RestoreScope(frame, environment);
        foreach (var binding in frame.Environment.Values)
            if (Current(frame, binding).KnownBool is not null) frame.Facts = frame.Facts.SetItem(binding.Identity, CaptureInfo(Current(frame, binding)));
        Refresh(frame);
    }

    private static void ApplyEffects(Frame frame, Specialization specialization)
    {
        // Repeated calls to a cached specialization are distinct memory writes.
        frame.Facts = frame.Facts.SetItems(specialization.Effects.Select(pair => pair.Value.MemoryVersion is null ? pair
            : new KeyValuePair<object, Info>(pair.Key, pair.Value with { MemoryVersion = new object() })));
        Refresh(frame);
    }

    private Info InstantiateResult(Frame frame, Specialization specialization, Template template, IReadOnlyList<Info> arguments, SourceSpan span)
    {
        var result = specialization.Result;
        if (!HasResultIdentity(result)) return Current(frame, result);
        var allocation = (template, span.Start, specialization.Id);
        if (frame.Allocations.TryGetValue(allocation, out var saved)) return Current(frame, saved);
        HashSet<object> external = [];
        HashSet<Template> externalFunctions = [];
        var externalBindings = template.Captures.Select(pair => pair.Value.Identity).ToHashSet();
        void Keep(Info info)
        {
            if (info.Function is { } function && externalFunctions.Add(function))
                foreach (var (_, binding) in function.Captures)
                {
                    externalBindings.Add(binding.Identity);
                    Keep(Current(frame, binding));
                }
            if (info.Element is { } element) Keep(element);
            foreach (var alternative in info.Alternatives ?? []) Keep(alternative);
            if (info.Scope is not { } scope || !external.Add(scope.Identity)) return;
            foreach (var member in scope.Members.Values) Keep(member);
        }
        foreach (var argument in arguments) Keep(argument);
        foreach (var capture in template.Captures) Keep(Current(frame, capture.Value));
        Dictionary<object, ScopeInfo> instances = [];
        Dictionary<object, Binding> bindings = [];
        Dictionary<Template, Template> callables = [];
        Binding FreshBinding(Binding binding)
        {
            if (externalBindings.Contains(binding.Identity)) return binding;
            if (bindings.TryGetValue(binding.Identity, out var existing)) return existing;
            var fresh = binding with
            {
                Identity = new(),
                Storage = new(binding.Storage.Type)
                {
                    IsUnwritten = binding.Storage.IsUnwritten,
                    CastContracts = binding.Storage.CastContracts,
                    DeferredTypeWrites = binding.Storage.DeferredTypeWrites,
                    ConceptContracts = binding.Storage.ConceptContracts,
                    ConceptMemberContracts = binding.Storage.ConceptMemberContracts
                }
            };
            bindings.Add(binding.Identity, fresh);
            fresh = fresh with { Info = Fresh(Current(frame, binding)) };
            ObserveCallableStore(fresh.Storage, fresh.Info);
            bindings[binding.Identity] = fresh;
            frame.Facts = frame.Facts.SetItem(fresh.Identity, fresh.Info);
            return fresh;
        }
        Template FreshFunction(Template function)
        {
            if (externalFunctions.Contains(function)) return function;
            if (callables.TryGetValue(function, out var existing)) return existing;
            List<KeyValuePair<string, Binding>> captures = [];
            Template fresh = new(function.Id, function.Syntax, captures, function.Source, function.SourceName)
            { Instance = true, Concepts = function.Concepts, StandardShadowed = function.StandardShadowed };
            callables.Add(function, fresh);
            templates.Add(fresh);
            foreach (var capture in function.Captures) captures.Add(new(capture.Key, FreshBinding(capture.Value)));
            return fresh;
        }
        Info Fresh(Info info)
        {
            info = Current(frame, info);
            if (info.Element is { } element) info = info with { Element = Fresh(element) };
            if (info.Alternatives is { } alternatives) info = info with { Alternatives = alternatives.Select(Fresh).ToArray() };
            if (info.Function is { } function) return info with { Function = FreshFunction(function) };
            if (info.Scope is not { } scope || scope.Named || external.Contains(scope.Identity)) return info;
            if (scope.Memory.View is not null) return ReadIndexedScope(frame, info);
            if (!instances.TryGetValue(scope.Identity, out var instance))
            {
                Dictionary<string, Info> members = [];
                Dictionary<string, Binding> fields = [];
                instance = scope with { Identity = new(), Memory = new(), Sources = null, Members = members, FieldBindings = fields };
                instances.Add(scope.Identity, instance);
                foreach (var (name, binding) in scope.FieldBindings) fields[name] = FreshBinding(binding);
                foreach (var (name, member) in scope.Members) members[name] = Fresh(member);
                instance = instance with
                {
                    Storage = scope.Storage.ToDictionary(pair => pair.Key,
                    pair => fields.TryGetValue(pair.Key, out var binding) ? binding.Storage : new StorageConstraint(pair.Value.Type), StringComparer.Ordinal)
                };
                instances[scope.Identity] = instance;
                frame.Facts = frame.Facts.SetItem(instance.Identity, info with { Scope = instance });
                RegisterScope(frame, instance);
            }
            return info with { Scope = instance };
        }
        var instance = Fresh(result);
        frame.Allocations[allocation] = instance;
        return instance;
    }

    private static bool HasResultIdentity(Info info)
    {
        if (info.Function is not null || info.Scope is not null) return true;
        if (info.Element is { } element && HasResultIdentity(element)) return true;
        foreach (var alternative in info.Alternatives ?? [])
            if (HasResultIdentity(alternative)) return true;
        return false;
    }

    private readonly List<StorageConstraint> solvedStorage = [];
    private void Solve(StorageConstraint storage, TypeRef type)
    {
        solvedStorage.Add(storage);
        storage.Type = type;
    }
}
