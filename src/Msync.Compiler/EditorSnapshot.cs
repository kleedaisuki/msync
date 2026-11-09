using Msync.Core;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Msync.Compiler;

internal sealed class EditorSnapshot
{
    private readonly Dictionary<(Info, Value?, bool), Info> infos = new(new IdentityComparer());
    private readonly Dictionary<(Template, FunctionValue?), Template> templates = [];
    private readonly Dictionary<StorageConstraint, StorageConstraint> storage = [];
    private StorageConstraint Storage(StorageConstraint constraint)
    {
        if (!storage.TryGetValue(constraint, out var result))
        {
            storage.Add(constraint, result = new(constraint.Type) { Revision = constraint.Revision });
            result.CastContracts = constraint.CastContracts;
            result.ConceptContracts = constraint.ConceptContracts;
            result.ConceptMemberContracts = constraint.ConceptMemberContracts;
            result.DeferredTypeWrites = constraint.DeferredTypeWrites;
            result.Callables = constraint.Callables is { } domain ? Info(domain, null, unprovenCapture: true) : null;
            result.Sources = System.Collections.Immutable.ImmutableHashSet.CreateRange(constraint.Sources.Select(Storage));
            result.Contracts = constraint.Contracts.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value with { Result = Info(pair.Value.Result, null, unprovenCapture: true) });
        }
        return result;
    }

    private sealed class IdentityComparer : IEqualityComparer<(Info, Value?, bool)>
    {
        public bool Equals((Info, Value?, bool) left, (Info, Value?, bool) right) =>
            ReferenceEquals(left.Item1, right.Item1) && ReferenceEquals(left.Item2, right.Item2) && left.Item3 == right.Item3;
        public int GetHashCode((Info, Value?, bool) key) => HashCode.Combine(RuntimeHelpers.GetHashCode(key.Item1),
            key.Item2 is null ? 0 : RuntimeHelpers.GetHashCode(key.Item2), key.Item3);
    }

    public Binding Binding(Binding binding, Value? value, bool unprovenCapture = false) => binding with
    {
        Info = Info(binding.Info, value, unprovenCapture),
        Self = binding.Self is null ? null : Template(binding.Self, value as FunctionValue),
        Storage = Storage(binding.Storage)
    };

    private Info Info(Info info, Value? value, bool unprovenCapture = false)
    {
        if (value is CellValue cell) value = cell.Data;
        if (infos.TryGetValue((info, value, unprovenCapture), out var saved)) return saved;
        ScopeInfo? scope = info.Scope;
        if (scope is not null)
        {
            var actual = value as ScopeValue;
            Dictionary<string, Info> members = [];
            foreach (var (name, member) in scope.Members)
            {
                Value? item = actual is not null && actual.Members.TryGetValue(name, out Value? found) ? found : null;
                members[name] = Info(member, item, unprovenCapture && item is null);
            }
            scope = new(members, scope.Named, scope.Origins, scope.NullableMembers, scope.StorageTypes)
            {
                Identity = scope.Identity,
                AllocationSite = scope.AllocationSite,
                Storage = scope.Storage.ToDictionary(pair => pair.Key, pair => Storage(pair.Value), StringComparer.Ordinal),
                FieldBindings = scope.FieldBindings.ToDictionary(pair => pair.Key, pair => pair.Value with { Storage = Storage(pair.Value.Storage) }, StringComparer.Ordinal)
            };
        }
        var result = info with
        {
            Function = info.Function is null ? null : Template(info.Function, value as FunctionValue),
            Scope = scope,
            Alternatives = info.Alternatives?.Select(item => Info(item, value, unprovenCapture)).ToArray(),
            Element = info.Element is null ? null : Info(info.Element, null, unprovenCapture),
            Storage = info.Storage is null ? null : Storage(info.Storage),
            KnownConnection = value is ConnectionValue connection ? connection : info.KnownConnection,
            KnownPath = value is PathValue path ? path : unprovenCapture ? null : info.KnownPath,
            KnownText = value is StringValue text ? text.Data : info.KnownText
        };
        infos[(info, value, unprovenCapture)] = result;
        return result;
    }

    private Template Template(Template template, FunctionValue? value)
    {
        if (templates.TryGetValue((template, value), out var saved)) return saved;
        List<KeyValuePair<string, Binding>> captures = [];
        Template snapshot = new(template.Id, template.Syntax, captures, template.Source, template.SourceName)
        {
            Concepts = template.Concepts
        };
        templates[(template, value)] = snapshot;
        bool matches = value is not null && (value.Function == template.Id || template.Specializations.Values.Any(item => item.Id == value.Function));
        bool actual = matches && value!.Captures?.Count == template.Captures.Count;
        for (int index = 0; index < template.Captures.Count; index++)
        {
            var capture = template.Captures[index];
            captures.Add(new(capture.Key, Binding(capture.Value, actual ? value!.Captures![index] : null, unprovenCapture: !actual)));
        }
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
