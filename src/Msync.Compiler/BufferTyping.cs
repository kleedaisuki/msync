using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    // Frame facts carry versions so control-flow forks retain independent proofs.
    private readonly object indexedMemoryClock = new();
    private readonly Dictionary<ScopeMemory, (ScopeInfo? View, object Clock)> changedScopeMemory = [];
    private readonly Dictionary<IndexedStorage, Info> changedIndexedElements = [];
    private readonly Dictionary<StorageConstraint, ImmutableHashSet<StorageConstraint>> changedMemoryAliases = [];

    private void ClearIndexedMutations()
    {
        changedScopeMemory.Clear();
        changedIndexedElements.Clear();
        changedMemoryAliases.Clear();
    }

    private void RestoreIndexedMutations()
    {
        foreach (var (memory, previous) in changedScopeMemory) { memory.View = previous.View; memory.Clock = previous.Clock; }
        foreach (var (storage, element) in changedIndexedElements) storage.Element = element;
        foreach (var (storage, aliases) in changedMemoryAliases) storage.MemoryAliases = aliases;
        ClearIndexedMutations();
    }

    private void LinkIndexedLocations(StorageConstraint first, StorageConstraint second)
    {
        if (ReferenceEquals(first, second) || first.MemoryAliases.Contains(second)) return;
        changedMemoryAliases.TryAdd(first, first.MemoryAliases);
        changedMemoryAliases.TryAdd(second, second.MemoryAliases);
        first.MemoryAliases = first.MemoryAliases.Add(second);
        second.MemoryAliases = second.MemoryAliases.Add(first);
    }

    private static object? IndexedVersion(Frame frame, object clock) => frame.Facts.GetValueOrDefault(clock)?.MemoryVersion;

    private static void AdvanceIndexedMemory(Frame frame, object clock) =>
        frame.Facts = frame.Facts.SetItem(clock, new(TypeRef.Null, MemoryVersion: new object()));

    private static Dictionary<string, Info> IndexedMembers(ScopeInfo scope) => scope.Members.ToDictionary(
        pair => pair.Key, pair => pair.Value with { Type = scope.Storage[pair.Key].Type ?? pair.Value.Type }, StringComparer.Ordinal);

    private static Info ReadIndexedScope(Frame frame, Info info) => info.Scope is { Memory.View: not null } scope
        ? info with { Scope = scope with { Identity = new(), MemoryVersion = IndexedVersion(frame, scope.Memory.Clock) } } : info;

    private Info? IndexedNativeResult(Frame frame, NativeSignature signature, IReadOnlyList<Info> arguments,
        SourceSpan span, IReadOnlyList<Expr>? expressions)
    {
        if (!signature.Name.StartsWith("storage.buffer_", StringComparison.Ordinal)) return null;
        if (signature.Name == "storage.buffer_new")
        {
            var element = DynamicElement(frame, RangeItem(arguments[0]));
            IndexedStorage storage = new(element);
            ObserveCallableStore(storage.Storage, element);
            return RangeResult(element) with { Indexed = storage };
        }
        var storageInfo = arguments[0].Indexed;
        if (storageInfo is null)
            throw Error(frame, "Indexed storage operations require a Buffer-owned Range.", expressions?[0].Span ?? span,
                help: "Create indexed storage with Buffer::new; an ordinary Range is not mutable storage.", code: "E0222");
        if (signature.Name is "storage.buffer_set" or "storage.buffer_push" or "storage.buffer_insert")
        {
            int index = arguments.Count - 1;
            var value = arguments[index];
            if (storageInfo.Element.Type.Kind == TypeKind.Never)
            {
                changedIndexedElements.TryAdd(storageInfo, storageInfo.Element);
                storageInfo.Element = DynamicElement(frame, value);
                Solve(storageInfo.Storage, value.Type);
            }
            CheckIndexedValue(frame, storageInfo.Element, value, expressions?[index].Span ?? span, []);
            if (!storageInfo.Element.Type.Accepts(value.Type))
                Convert(frame, value.Type, storageInfo.Element.Type, expressions?[index].Span ?? span);
            ObserveCallableStore(storageInfo.Storage, value);
        }
        if (signature.Name is "storage.buffer_set" or "storage.buffer_push" or "storage.buffer_insert"
            or "storage.buffer_remove" or "storage.buffer_pop" or "storage.buffer_clear" or "storage.buffer_swap")
            AdvanceIndexedMemory(frame, indexedMemoryClock);
        return signature.Name switch
        {
            "storage.buffer_get" or "storage.buffer_pop" or "storage.buffer_remove" => ReadIndexedScope(frame, IndexedElement(storageInfo)),
            "storage.buffer_snapshot" => RangeResult(IndexedElement(storageInfo) with { Storage = null }),
            _ => new(signature.Result)
        };
    }

    private static Info IndexedElement(IndexedStorage storage) => storage.Element.Type.WithoutNull == TypeRef.Function
        ? (storage.Storage.Callables ?? storage.Element) with { Storage = storage.Storage }
        : storage.Element;

    private void CheckIndexedValue(Frame frame, Info expected, Info actual, SourceSpan span,
        HashSet<(object Expected, object Actual)> visited)
    {
        CheckConversion(frame, actual.Type, expected.Type, span);
        if (expected.Scope is not { } shape || actual.Scope is not { } value) return;
        if (!visited.Add((shape.Identity, value.Identity))) return;
        foreach (var (name, member) in shape.Members)
        {
            if (!value.Members.TryGetValue(name, out var field))
                throw Error(frame, $"Buffer element is missing field '{name}'.", span,
                    help: "Stored values retain the Buffer's inferred element schema.", code: "E0222");
            var slot = shape.Storage[name];
            var target = slot.Type;
            if (target is null && field.Type.WithoutNull.Kind != TypeKind.Never)
            {
                target = shape.NullableMembers?.Contains(name) == true ? field.Type.WithNull : field.Type;
                Solve(slot, target);
            }
            if (target is not null && !target.Accepts(field.Type)) throw TypeError(frame, field.Type, target, span);
            CheckIndexedValue(frame, target is null ? member : member with { Type = target }, field, span, visited);
            CheckConceptStore(frame, slot, field, span);
            ObserveCallableStore(slot, field with { Storage = value.Storage[name] });
        }
        DynamicElement(frame, actual);
        LinkIndexedFields(shape, value, []);
    }

    private Info DynamicElement(Frame frame, Info element)
    {
        Dictionary<object, ScopeInfo> views = [];
        Info Escape(Info info)
        {
            var item = info.Element is null ? null : Escape(info.Element);
            // A copied element is a value, not the prototype parameter location.
            if (info.Scope is not { } scope) return info with
            {
                KnownBool = null, KnownConnection = null, KnownPath = null, KnownText = null,
                TypeValue = null, Element = item, Storage = null
            };
            if (scope.Memory.View is { } previous) return info with { Scope = previous, Element = item, Storage = null };
            if (views.TryGetValue(scope.Identity, out var existing)) return info with { Scope = existing, Element = item, Storage = null };
            Dictionary<string, Info> members = new(StringComparer.Ordinal);
            var view = scope with { Members = members, FieldBindings = new Dictionary<string, Binding>() };
            views[scope.Identity] = view;
            changedScopeMemory.TryAdd(scope.Memory, (scope.Memory.View, scope.Memory.Clock));
            scope.Memory.View = view;
            scope.Memory.Clock = indexedMemoryClock;
            if (IndexedVersion(frame, indexedMemoryClock) is null) AdvanceIndexedMemory(frame, indexedMemoryClock);
            foreach (var (name, member) in scope.Members)
            {
                var escaped = Escape(member);
                var slot = scope.Storage[name];
                members[name] = escaped with { Type = slot.Type ?? escaped.Type, Storage = slot };
                ObserveCallableStore(slot, member);
            }
            if (scope.Sources is not null)
                foreach (var source in scope.Sources)
                {
                    Escape(new(TypeRef.Scope, Scope: source));
                    LinkIndexedFields(view, source, []);
                }
            return info with { Scope = view, Element = item, Storage = null };
        }
        return Escape(Current(frame, element));
    }

    private void LinkIndexedFields(ScopeInfo destination, ScopeInfo source, HashSet<(object, object)> visited)
    {
        if (!visited.Add((destination.Identity, source.Identity))) return;
        foreach (var (name, member) in destination.Members)
        {
            if (!source.Members.TryGetValue(name, out var value)) continue;
            LinkIndexedLocations(destination.Storage[name], source.Storage[name]);
            ObserveCallableStore(destination.Storage[name], value with { Storage = source.Storage[name] });
            if (member.Scope is { } nested && value.Scope is { } actual) LinkIndexedFields(nested, actual, visited);
        }
    }
}
