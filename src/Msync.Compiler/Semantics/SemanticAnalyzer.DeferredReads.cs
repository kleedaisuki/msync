using System.Collections.Immutable;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private BoundCallableWrites? futureCallableWrites;

    // A deferred read preserves location/shape provenance, not an observation of
    // a writable cell made while the lazy operation was being constructed.
    private SemanticValue FutureRead(SemanticValue value, HirTypeId storage, SemanticContext context,
        HirLocationId? location = null)
    {
        value = FutureCallables(value, location, context);
        if (value.Scope is not null || value.Element is not null) return FutureRead(value, storage, context, []);
        return value.Type == storage && value.Literal is null && value.Observation is null
            && value.DenotedType is null ? value : value with
            { Type = storage, Literal = null, Observation = null, DenotedType = null };
    }

    private SemanticValue FutureRead(SemanticValue value, HirTypeId storage,
        SemanticContext context, HashSet<int> active)
    {
        SemanticValue result = value with { Type = storage, Literal = null, Observation = null,
            DenotedType = null };
        if (value.Element is { } element)
        {
            HirType descriptor = hir.Types[storage.Value];
            HirTypeId carrier = descriptor.Element >= 0 ? new(descriptor.Element) : element.Type;
            if (value.IndexedRegion is { } indexed && context.Indexed.TryGetValue(indexed, out SemanticIndexed? resource))
            {
                if (!active.Add(indexed)) return result;
                carrier = resource.FixedCarrier ?? resource.Element.Type;
                element = FutureRead(resource.Element, carrier, context, active);
                context.Indexed[indexed] = resource with { Element = element };
                active.Remove(indexed);
            }
            else element = FutureRead(element, carrier, context, active);
            result = result with { Element = element };
        }
        if (value.Scope is not { } initial) return result;
        SemanticScope scope = context.Objects.GetValueOrDefault(initial.Region, initial);
        if (scope.Region >= 0 && !active.Add(scope.Region)) return result;
        var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        foreach (HirField field in hir.ScopeLayouts[scope.Layout].Fields)
        {
            if (!scope.Fields.TryGetValue(field.Name, out SemanticValue? current)) continue;
            HirTypeId domain = field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType;
            if (scope.Locations.TryGetValue(field.Name, out HirLocationId location))
            {
                current = context.Values.GetValueOrDefault(location, current);
                domain = context.Storage.GetValueOrDefault(location, domain);
            }
            current = FutureCallables(current, scope.Locations.TryGetValue(field.Name, out HirLocationId source)
                ? source : current.FutureCallableDomain?.Owner, context, scope);
            SemanticValue future = FutureRead(current, domain, context, active);
            fields[field.Name] = future;
            if (scope.Locations.TryGetValue(field.Name, out location)) context.Values[location] = future;
        }
        scope = scope with { Fields = fields.ToImmutable() };
        if (scope.Region >= 0)
        {
            active.Remove(scope.Region);
            context.Objects[scope.Region] = scope;
        }
        return result with { Scope = scope };
    }

    private SemanticValue FutureCallables(SemanticValue value, HirLocationId? location, SemanticContext context,
        SemanticScope? receiver = null)
    {
        location ??= value.FutureCallableDomain?.Owner;
        if (location is not { } source || hir.Types[value.Type.Value].RuntimeType.WithoutNull != Msync.Core.TypeRef.Function)
            return value;
        if (value.FutureCallableDomain is { } admitted
            && StorageOwner(admitted.Owner, context) == StorageOwner(source, context))
            return value with { Callables = admitted.Choices, Alternatives = default };
        HirLocationId definition = program.Original(context.Definitions.GetValueOrDefault(source, source));
        var choices = Choices(value).ToList();
        HashSet<HirLocationId> active = [];
        Collect(definition);
        ImmutableArray<SemanticCallable> admittedChoices = choices.Distinct(SemanticCallableIdentity.Instance).ToImmutableArray();
        return value with { Callables = admittedChoices, Alternatives = default,
            FutureCallableDomain = new(StorageOwner(source, context), admittedChoices, value.Callables) };

        void Collect(HirLocationId target)
        {
            if (!active.Add(target)) return;
            foreach (HirNodeId write in (futureCallableWrites ??= new(program)).Values(target))
            {
                if (receiver is not null)
                {
                    HirNodeId[] owners = futureCallableWrites.Receivers(target, write).ToArray();
                    if (owners.Length != 0 && owners.All(owner => !FutureReceiver(owner, receiver, context, []))) continue;
                }
                if (futureCallableWrites.Writer(target, write) is { } writer
                    && !FutureCapturedWriter(writer, target, source, context)) continue;
                HirNode expression = program.Node(write);
                if (expression.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
                    Collect(program.Original(new(expression.Data)));
                foreach (SemanticCallable choice in BoundCallables(write, context))
                    if (!choices.Contains(choice, SemanticCallableIdentity.Instance))
                        choices.Add(choice);
            }
        }
    }

    private bool FutureCapturedWriter(HirTemplateId writer, HirLocationId definition, HirLocationId source,
        SemanticContext context)
    {
        bool unknown = false;
        foreach (HirNodeId site in futureCallableWrites!.CallSites(writer))
        {
            SemanticCallable[] choices = FutureWriterChoices(site, context, []).Where(choice => choice.Template == writer).ToArray();
            if (choices.Length == 0) { unknown = true; continue; }
            foreach (SemanticCallable choice in choices)
            {
                bool found = false;
                foreach (SemanticCapture capture in choice.Captures.IsDefault ? [] : choice.Captures)
                {
                    HirLocationId origin = program.Original(context.Definitions.GetValueOrDefault(capture.Source, capture.Source));
                    if (origin != definition) continue;
                    found = true;
                    if (capture.Mode == HirCaptureMode.Value) { unknown = true; continue; }
                    if (StorageOwner(capture.Source, context) == StorageOwner(source, context)) return true;
                }
                if (!found) unknown = true;
            }
        }
        return unknown;
    }

    private IEnumerable<SemanticCallable> FutureWriterChoices(HirNodeId site, SemanticContext context,
        HashSet<HirLocationId> active)
    {
        SemanticCallable[] choices = BoundCallables(site, context).ToArray();
        if (choices.Length != 0) return choices;
        HirNode node = program.Node(site);
        if (node.Kind is not (HirKind.ReadLocation or HirKind.ReadStorage)) return [];
        HirLocationId parameter = program.Original(new(node.Data));
        if (!active.Add(parameter)) return [];
        var result = futureCallableWrites!.Arguments(parameter).Concat(futureCallableWrites.Values(parameter))
            .SelectMany(input => FutureWriterChoices(input, context, active)).ToArray();
        active.Remove(parameter);
        return result;
    }

    private bool FutureReceiver(HirNodeId id, SemanticScope expected, SemanticContext context,
        HashSet<HirLocationId> active)
    {
        HirNode node = program.Node(id);
        if (StableReceiver(id) && BoundValue(id, context)?.Scope is { } actual)
        {
            int[] first = ScopeAliases(expected).ToArray(), second = ScopeAliases(actual).ToArray();
            return first.Length == 0 || second.Length == 0 || first.Intersect(second).Any();
        }
        if (node.Kind is not (HirKind.ReadLocation or HirKind.ReadStorage)) return true;
        HirLocationId parameter = program.Original(new(node.Data));
        if (!active.Add(parameter)) return true;
        HirNodeId[] inputs = futureCallableWrites!.Arguments(parameter).ToArray();
        bool result = inputs.Length == 0 || inputs.Any(input => FutureReceiver(input, expected, context, active));
        active.Remove(parameter);
        return result;
    }

    private bool StableReceiver(HirNodeId id)
    {
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            return !program.WrittenLocations.Contains(program.Original(new(node.Data)));
        return node.Kind == HirKind.MemberRead && !futureCallableWrites!.WritesMember(node.Text!)
            && StableReceiver(program.Children(id)[0]);
    }
}
