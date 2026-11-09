using Msync.Core;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Msync.Compiler;

internal sealed class EditorSnapshot(bool copySpecializations = true)
{
    private readonly Dictionary<(Info, Value?, bool), Info> infos = new(new IdentityComparer());
    private readonly Dictionary<(Template, FunctionValue?), Template> templates = [];
    private readonly Dictionary<StorageConstraint, StorageConstraint> storage = [];
    private readonly Dictionary<(ScopeInfo, ScopeValue?, bool), ScopeInfo> scopes = new(new ScopeIdentityComparer());
    private readonly Dictionary<ScopeMemory, ScopeMemory> memories = [];
    private readonly Dictionary<IndexedStorage, IndexedStorage> indexed = [];
    private StorageConstraint Storage(StorageConstraint constraint)
    {
        if (!storage.TryGetValue(constraint, out var result))
        {
            storage.Add(constraint, result = new(constraint.Type));
            PopulateStorage(constraint, result);
        }
        return result;
    }

    private void PopulateStorage(StorageConstraint original, StorageConstraint result)
    {
        result.Revision = original.Revision;
        result.IsUnwritten = original.IsUnwritten;
        result.CastContracts = original.CastContracts;
        result.ConceptContracts = original.ConceptContracts;
        result.ConceptMemberContracts = original.ConceptMemberContracts.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value with { Associated = pair.Value.Associated.ToDictionary(item => item.Key, item => CopyInfo(item.Value)) });
        result.DeferredTypeWrites = original.DeferredTypeWrites;
        result.Callables = original.Callables is { } domain ? Info(domain, null, unprovenCapture: true) : null;
        result.Sources = ImmutableHashSet.CreateRange(original.Sources.Select(Storage));
        result.MemoryAliases = ImmutableHashSet.CreateRange(original.MemoryAliases.Select(Storage));
        result.Contracts = original.Contracts.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value with { Result = Info(pair.Value.Result, null, unprovenCapture: true) });
    }

    private IndexedStorage Indexed(IndexedStorage original)
    {
        if (indexed.TryGetValue(original, out var saved)) return saved;
        bool exists = storage.TryGetValue(original.Storage, out var location);
        if (!exists) storage.Add(original.Storage, location = new(original.Storage.Type));
        IndexedStorage result = new(new(original.Element.Type)) { Storage = location! };
        indexed.Add(original, result);
        if (!exists) PopulateStorage(original.Storage, location!);
        result.Element = CopyInfo(original.Element);
        return result;
    }

    private sealed class IdentityComparer : IEqualityComparer<(Info, Value?, bool)>
    {
        public bool Equals((Info, Value?, bool) left, (Info, Value?, bool) right) =>
            ReferenceEquals(left.Item1, right.Item1) && ReferenceEquals(left.Item2, right.Item2) && left.Item3 == right.Item3;
        public int GetHashCode((Info, Value?, bool) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Item1),
            key.Item2 is null ? 0 : RuntimeHelpers.GetHashCode(key.Item2), key.Item3);
    }

    private sealed class ScopeIdentityComparer : IEqualityComparer<(ScopeInfo, ScopeValue?, bool)>
    {
        public bool Equals((ScopeInfo, ScopeValue?, bool) first, (ScopeInfo, ScopeValue?, bool) second) =>
            ReferenceEquals(first.Item1, second.Item1) && ReferenceEquals(first.Item2, second.Item2) && first.Item3 == second.Item3;
        public int GetHashCode((ScopeInfo, ScopeValue?, bool) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Item1),
            key.Item2 is null ? 0 : RuntimeHelpers.GetHashCode(key.Item2), key.Item3);
    }

    public Binding Binding(Binding binding, Value? value, bool unprovenCapture = false) => binding with
    {
        Info = Info(binding.Info, value, unprovenCapture),
        Self = binding.Self is null ? null : Template(binding.Self, value as FunctionValue),
        Storage = Storage(binding.Storage)
    };

    public Info CopyInfo(Info info) => Info(info, null);

    private Info Info(Info info, Value? value, bool unprovenCapture = false)
    {
        if (value is CellValue cell) value = cell.Data;
        if (infos.TryGetValue((info, value, unprovenCapture), out var saved)) return saved;
        var result = info with
        {
            Function = info.Function is null ? null : Template(info.Function, value as FunctionValue),
            Scope = info.Scope is null ? null : Scope(info.Scope, value as ScopeValue, unprovenCapture),
            Alternatives = info.Alternatives?.Select(item => Info(item, value, unprovenCapture)).ToArray(),
            Element = info.Element is null ? null : Info(info.Element, null, unprovenCapture),
            Storage = info.Storage is null ? null : Storage(info.Storage),
            Indexed = info.Indexed is null ? null : Indexed(info.Indexed),
            KnownConnection = value is ConnectionValue connection ? connection : info.KnownConnection,
            KnownPath = value is PathValue path ? path : unprovenCapture ? null : info.KnownPath,
            KnownText = value is StringValue text ? text.Data : info.KnownText
        };
        infos[(info, value, unprovenCapture)] = result;
        return result;
    }

    private ScopeInfo Scope(ScopeInfo original, ScopeValue? actual, bool unprovenCapture)
    {
        var key = (original, actual, unprovenCapture);
        if (scopes.TryGetValue(key, out var saved)) return saved;
        if (!memories.TryGetValue(original.Memory, out var memory))
            memories.Add(original.Memory, memory = new() { Clock = original.Memory.Clock });
        Dictionary<string, Info> members = new(StringComparer.Ordinal);
        Dictionary<string, StorageConstraint> locations = new(StringComparer.Ordinal);
        Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
        List<ScopeInfo>? sources = original.Sources is null ? null : [];
        var result = original with { Members = members, Storage = locations, FieldBindings = bindings, Memory = memory, Sources = sources };
        scopes.Add(key, result);
        foreach (var (name, member) in original.Members)
        {
            Value? item = actual is not null && actual.Members.TryGetValue(name, out var found) ? found : null;
            members[name] = Info(member, item, unprovenCapture && item is null);
        }
        foreach (var (name, location) in original.Storage) locations[name] = Storage(location);
        foreach (var (name, binding) in original.FieldBindings)
            bindings[name] = binding with
            {
                Info = members.GetValueOrDefault(name) ?? CopyInfo(binding.Info),
                Storage = Storage(binding.Storage),
                Self = binding.Self is null ? null : Template(binding.Self, null)
            };
        if (sources is not null)
            foreach (var source in original.Sources!) sources.Add(Scope(source, null, unprovenCapture));
        if (memory.View is null && original.Memory.View is { } view)
            memory.View = Scope(view, null, unprovenCapture);
        return result;
    }

    private Template Template(Template template, FunctionValue? value)
    {
        if (templates.TryGetValue((template, value), out var saved)) return saved;
        List<KeyValuePair<string, Binding>> captures = [];
        Template snapshot = new(template.Id, template.Syntax, captures, template.Source, template.SourceName)
        {
            Concepts = template.Concepts,
            StandardShadowed = template.StandardShadowed,
            Instance = template.Instance
        };
        templates[(template, value)] = snapshot;
        bool matches = value is not null && (value.Function == template.Id || template.Specializations.Values.Any(item => item.Id == value.Function));
        bool actual = matches && value!.Captures?.Count == template.Captures.Count;
        for (int index = 0; index < template.Captures.Count; index++)
        {
            var capture = template.Captures[index];
            captures.Add(new(capture.Key, Binding(capture.Value, actual ? value!.Captures![index] : null, unprovenCapture: !actual)));
        }
        if (!copySpecializations) return snapshot;
        foreach (var (key, specialization) in template.Specializations)
        {
            // Only completed signature facts are reused; editor calls specialize
            // into their own table, never into the live session's templates.
            Info result = new(specialization.Result.Type, EditorUnknown: specialization.Result.EditorUnknown);
            snapshot.Specializations.Add(key, new(specialization.Id, result, specialization.ParameterTypes));
        }
        return snapshot;
    }
}
