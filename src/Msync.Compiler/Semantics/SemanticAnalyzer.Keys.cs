using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private string InstanceKey(HirTemplateId id, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext caller, ref SemanticInputSchema? schema,
        bool recursive = false)
    {
        StringBuilder key = new();
        key.Append(id.Value).Append(caller.DeferredReads ? 'd' : 'i').Append(':');
        if (arguments.All(IsScalarSchema) && captures.All(IsScalarSchema))
        {
            foreach (var value in arguments) AppendScalarKey(value, capture: false);
            key.Append('|');
            foreach (var value in captures) AppendScalarKey(value, capture: true);
            return key.ToString();
        }
        int prefix = key.Length;
        if (AppendInputKey(key, id, arguments, captures, caller, new(readOnlyInput: true, recursive: recursive)))
            return key.ToString();
        key.Length = prefix;
        // The recursive key can omit write-only inputs, but its formal input
        // graph is the same snapshot until the invocation starts.
        schema ??= CodeSchema(arguments, captures, caller);
        AppendInputKey(key, id, schema.Arguments, schema.Captures, schema.Context, new(recursive: recursive));
        return key.ToString();

        void AppendScalarKey(SemanticValue value, bool capture) => key.Append(value.Type.Value).Append('T')
            .Append(value.DenotedType?.Value ?? -1).Append('S').Append(capture ? value.StorageConstraint?.Value ?? -1 : -1).Append(';');
    }

    private bool AppendInputKey(StringBuilder key, HirTemplateId template, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext context, KeyProvenance provenance)
    {
        // The direct projection cuts cycles at concrete scope, indexed-resource,
        // and cell references. The fallback also admits structural Scope views.
        HashSet<SemanticValue>? active = provenance.ReadOnlyInput ? null : new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < arguments.Count && provenance.Supported; index++)
        {
            SemanticValue argument = arguments[index];
            var usage = Usage(template);
            var target = program.Original(hir.Templates[template.Value].Parameters[index]);
            HashSet<string>? usedFields = usage.WholeReads.Contains(target)
                ? null : usage.FieldReads.GetValueOrDefault(target) ?? [];
            AppendValueKey(key, IsScalarSchema(argument) ? argument with { StorageConstraint = null } : argument,
                context, active, provenance, usedFields);
            key.Append(';');
        }
        key.Append('|');
        for (int index = 0; index < captures.Count && provenance.Supported; index++)
        {
            var templateCaptures = hir.Templates[template.Value].Captures;
            if (index < templateCaptures.Length && StandardModuleCapture(templateCaptures[index].SourceLocation, context) is { } stdModule)
                key.Append('M').Append(stdModule.Value);
            else if (ReadsCapture(template, index, out var usedFields))
                AppendValueKey(key, captures[index], context, active, provenance, usedFields);
            else
            {
                provenance.Skip(captures[index]);
                key.Append(captures[index].Type.Value);
            }
            key.Append(';');
        }
        if (provenance.Supported) provenance.AppendAliases(key);
        return provenance.Supported && provenance.HasCompleteDetachedCells;
    }

    private sealed record TemplateInputUsage(
        HashSet<HirLocationId> WholeReads,
        Dictionary<HirLocationId, HashSet<string>> FieldReads);

    private readonly Dictionary<HirTemplateId, TemplateInputUsage> templateUsages = [];

    private bool ReadsCapture(HirTemplateId template, int index, out HashSet<string>? usedFields)
    {
        var target = program.Original(hir.Templates[template.Value].Captures[index].Target);
        var usage = Usage(template);
        if (usage.WholeReads.Contains(target))
        {
            usedFields = null;
            return true;
        }
        if (usage.FieldReads.TryGetValue(target, out usedFields))
            return true;
        usedFields = null;
        return false;
    }

    private TemplateInputUsage Usage(HirTemplateId template) => Usage(template, []);

    private TemplateInputUsage Usage(HirTemplateId template, HashSet<HirTemplateId> stack)
    {
        if (templateUsages.TryGetValue(template, out var cached)) return cached;
        HashSet<HirLocationId> wholeReads = [];
        Dictionary<HirLocationId, HashSet<string>> fieldReads = [];
        if (!stack.Add(template)) return new(wholeReads, fieldReads);

        foreach (var witness in program.TypeWitnesses.Values)
            if (hir.Locations[witness.Location.Value].TemplateOwner == template)
                wholeReads.Add(program.Original(witness.Location));

        Visit(hir.Templates[template.Value].Body);
        stack.Remove(template);
        var computed = new TemplateInputUsage(wholeReads, fieldReads);
        // Only cache non-cyclic roots so recursive templates don't poison incomplete results.
        if (stack.Count == 0) templateUsages[template] = computed;
        return computed;

        void AddFieldRead(HirLocationId loc, string field)
        {
            if (wholeReads.Contains(loc)) return;
            if (!fieldReads.TryGetValue(loc, out var set)) fieldReads[loc] = set = new(StringComparer.Ordinal);
            set.Add(field);
        }

        void Visit(HirNodeId id)
        {
            var node = program.Node(id);
            if (node.Kind == HirKind.MemberRead && node.Text is { } fieldName)
            {
                var children = program.Children(id);
                if (children.Length > 0)
                {
                    var receiver = program.Node(children[0]);
                    if (receiver.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
                    {
                        AddFieldRead(program.Original(new(receiver.Data)), fieldName);
                        for (int i = 1; i < children.Length; i++) Visit(children[i]);
                        return;
                    }
                }
            }
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Assign)
                wholeReads.Add(program.Original(new(node.Data)));
            if (node.Kind == HirKind.TemplateClosure)
            {
                var closure = hir.TemplateClosures[node.Data];
                var innerUsage = Usage(closure.Template, stack);
                bool cyclic = !templateUsages.ContainsKey(closure.Template) && stack.Contains(closure.Template);
                foreach (var cap in closure.Captures)
                {
                    var outer = program.Original(cap.SourceLocation);
                    if (cyclic || cap.Mode != HirCaptureMode.Location)
                    {
                        wholeReads.Add(outer);
                        continue;
                    }
                    var inner = program.Original(cap.Target);
                    if (innerUsage.WholeReads.Contains(inner))
                        wholeReads.Add(outer);
                    else if (innerUsage.FieldReads.TryGetValue(inner, out var innerFields))
                        foreach (var f in innerFields) AddFieldRead(outer, f);
                }
            }
            if (node.Kind == HirKind.Match)
                foreach (var arm in hir.Matches[node.Data].Arms) Visit(arm.Value);
            foreach (var child in program.Children(id)) Visit(child);
        }
    }

    private static bool IsScalarSchema(SemanticValue value) => value.Scope is null
        && value.Element is null && value.Callables.IsDefaultOrEmpty && value.IndexedRegion is null
        && value.DeferredEffects.IsDefaultOrEmpty && value.IndexedAliases.IsDefaultOrEmpty
        && value.FutureCallableDomain is null;

    private string ValueKey(SemanticValue value)
    {
        StringBuilder key = new();
        KeyProvenance provenance = new();
        AppendValueKey(key, value, null, new(ReferenceEqualityComparer.Instance), provenance);
        provenance.AppendAliases(key);
        return key.ToString();
    }

    private void AppendValueKey(StringBuilder key, SemanticValue value, SemanticContext? context,
        HashSet<SemanticValue>? active, KeyProvenance provenance, HashSet<string>? usedScopeFields = null)
    {
        if (!provenance.Supported) return;
        SemanticIndexed? indexed = value.IndexedRegion is { } resource ? context?.Indexed.GetValueOrDefault(resource) : null;
        if (provenance.ReadOnlyInput && ((value.IndexedRegion is not null && indexed is null)
            || !value.IndexedAliases.IsDefaultOrEmpty || !value.DeferredEffects.IsDefaultOrEmpty
            || !value.OpenSessionCalls.IsDefaultOrEmpty))
        {
            provenance.Supported = false;
            return;
        }
        key.Append(value.Type.Value).Append(value.EagerRange ? '!' : '.').Append('S').Append(value.StorageConstraint?.Value ?? -1);
        if (value.DenotedType is { } denoted) key.Append('T').Append(denoted.Value);
        if (active?.Add(value) == false)
        {
            key.Append('^');
            return;
        }
        if (!value.Callables.IsDefaultOrEmpty)
        {
            if (provenance.ReadOnlyInput && value.Callables.Length > 1) provenance.MaySkipCells = true;
            AppendCallableKey(key, Choices(value), context, active, provenance);
        }
        if (!provenance.Supported) return;
        if (value.FutureCallableDomain is { } future && !RedundantFutureDomain(value, future))
        {
            key.Append("U(");
            AppendCallableKey(key, future.Choices, context, active, provenance);
            if (!provenance.Supported) return;
            key.Append("|C(");
            if (!future.Current.IsDefaultOrEmpty) AppendCallableKey(key, future.Current, context, active, provenance);
            key.Append("))");
        }
        if (!provenance.Supported) return;
        if (value.Scope is { } scope)
            AppendScopeKey(key, context?.Objects.GetValueOrDefault(scope.Region, scope) ?? scope, context, active, provenance, usedScopeFields);
        if (!provenance.Supported) return;
        if (value.Element is { } element) { key.Append('['); AppendValueKey(key, element, context, active, provenance); key.Append(']'); }
        if (value.IndexedRegion is { } region && indexed is not null)
        {
            key.Append('I').Append(provenance.Region(region)).Append(':').Append(indexed.FixedCarrier?.Value ?? -1);
            if (!indexed.CarrierConstraints.IsDefaultOrEmpty)
                foreach (var constraint in indexed.CarrierConstraints) key.Append('C').Append(constraint.Value);
            if (provenance.DefineIndexed(region)) AppendValueKey(key, indexed.Element, context, active, provenance);
            else key.Append('^');
        }
        if (!value.IndexedAliases.IsDefaultOrEmpty)
            foreach (int alias in value.IndexedAliases)
            {
                key.Append('J').Append(provenance.Region(alias));
                if (context?.Indexed.TryGetValue(alias, out var storage) == true)
                {
                    key.Append('C').Append(storage.FixedCarrier?.Value ?? -1);
                    if (!storage.CarrierConstraints.IsDefaultOrEmpty)
                        foreach (var constraint in storage.CarrierConstraints) key.Append('C').Append(constraint.Value);
                    if (provenance.DefineIndexed(alias)) AppendValueKey(key, storage.Element, context, active, provenance);
                    else key.Append('^');
                }
            }
        if (!value.DeferredEffects.IsDefaultOrEmpty)
            foreach (var write in value.DeferredEffects)
            {
                key.Append('W').Append((int)write.Kind).Append(write.Field);
                key.Append(write.Kind == SemanticWriteKind.Location
                    ? provenance.Location(write.Location) : provenance.Region(write.Region));
                AppendValueKey(key, write.After, context, active, provenance);
            }
        active?.Remove(value);
    }

    private void AppendCallableKey(StringBuilder key, IEnumerable<SemanticCallable> choices,
        SemanticContext? context, HashSet<SemanticValue>? active, KeyProvenance provenance)
    {
        foreach (var callable in choices)
        {
            key.Append('F').Append(callable.NativeName ?? callable.Template?.Value.ToString()).Append('(');
            if (!callable.Captures.IsDefault)
                for (int index = 0; index < callable.Captures.Length; index++)
                {
                    var capture = callable.Captures[index];
                    if (capture.Mode == HirCaptureMode.Location && StandardModuleCapture(capture.Source, context) is { } stdModule)
                    {
                        key.Append('M').Append(stdModule.Value);
                        continue;
                    }
                    SemanticValue? live = context?.Values.GetValueOrDefault(capture.Source);
                    if (provenance.ReadOnlyInput && (capture.Mode != HirCaptureMode.Location
                        || (live is null && !provenance.RecordUnbound(capture.Source, capture.Value))))
                    {
                        provenance.Supported = false;
                        return;
                    }
                    key.Append('C').Append(provenance.Location(capture.Source));
                    HashSet<string>? usedFields = null;
                    if (callable.Template is { } template)
                    {
                        if (!ReadsCapture(template, index, out usedFields))
                        {
                            provenance.Skip(live ?? capture.Value);
                            key.Append(':').Append(capture.Value.Type.Value);
                            continue;
                        }
                    }
                    if (live is not null || provenance.ReadOnlyInput)
                    {
                        if (provenance.DefineLocation(capture.Source)) AppendValueKey(key, live ?? capture.Value, context, active, provenance, usedFields);
                        else key.Append('^');
                    }
                    else AppendValueKey(key, capture.Value, context, active, provenance, usedFields);
                    if (!provenance.Supported) return;
                }
            key.Append(')');
        }
    }

    private HirLocationId? StandardModuleCapture(HirLocationId location, SemanticContext? context)
    {
        HirLocationId definition = context is not null && context.Definitions.TryGetValue(location, out var mapped)
            ? mapped
            : CanonicalDefinition(location) ?? location;
        var declaration = StandardDeclaration(definition)
            ?? (CanonicalDefinition(location) is { } canonical ? StandardDeclaration(canonical) : null);
        if (declaration is not { Kind: DeclarationKind.StandardModule or DeclarationKind.StandardRoot })
            return null;
        return context?.ReplacedProgramCells.Contains(declaration.Location) == true ? null : declaration.Location;
    }

    private static bool RedundantFutureDomain(SemanticValue value, SemanticCallableDomain future)
    {
        if (value.Callables.IsDefaultOrEmpty || future.Choices.IsDefaultOrEmpty) return false;
        var choices = Choices(value).ToArray();
        if (future.Choices.Length != choices.Length) return false;
        foreach (var candidate in future.Choices)
            if (!ContainsCallableIdentity(choices, candidate)) return false;
        if (!future.Current.IsDefaultOrEmpty)
        {
            if (future.Current.Length != choices.Length) return false;
            foreach (var candidate in future.Current)
                if (!ContainsCallableIdentity(choices, candidate)) return false;
        }
        return true;

        static bool ContainsCallableIdentity(SemanticCallable[] items, SemanticCallable target)
        {
            foreach (var item in items)
                if (SemanticCallableIdentity.Instance.Equals(item, target)) return true;
            return false;
        }
    }

    private void AppendScopeKey(StringBuilder key, SemanticScope scope, SemanticContext? context,
        HashSet<SemanticValue>? active, KeyProvenance provenance, HashSet<string>? usedFields = null)
    {
        if (provenance.ReadOnlyInput && (scope.Region < 0 || !scope.Aliases.IsDefaultOrEmpty))
        {
            provenance.Supported = false;
            return;
        }
        key.Append('{').Append(scope.Region >= 0 ? 'S' : 'V')
            .Append(scope.Region >= 0 ? provenance.Region(scope.Region) : provenance.Structural(scope));
        var plan = provenance.BeginScope(scope, usedFields);
        if (!plan.ShouldEmit)
        {
            key.Append("^}");
            return;
        }
        if (plan.IsFirst && !scope.Aliases.IsDefaultOrEmpty)
            provenance.Alias(scope, 'A', scope.Aliases);
        if (plan.IsFirst && context?.AliasViews.TryGetValue(scope.Region, out var views) == true)
            provenance.Alias(scope, 'V', views);
        foreach (var field in scope.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!plan.Include(field.Key))
            {
                if (plan.Skipped(field.Key)) provenance.Skip(field.Value);
                continue;
            }
            key.Append(field.Key).Append(':');
            bool hasLocation = scope.Locations.TryGetValue(field.Key, out var location);
            var member = hasLocation
                ? context?.Values.GetValueOrDefault(location, field.Value) ?? field.Value : field.Value;
            if (provenance.ReadOnlyInput && Runtime(member) == TypeRef.Null)
            {
                HirTypeId? carrier = hasLocation && context is not null ? StorageType(location, context) : member.StorageConstraint;
                if (carrier is { } storage && hir.Types[storage.Value].TypeParameter < 0)
                    member = member with { Type = storage };
            }
            AppendValueKey(key, member, context, active, provenance);
            if (!provenance.Supported) return;
            if (context?.MemberContracts.TryGetValue((scope.Region, field.Key), out var contracts) == true)
                foreach (var contract in contracts)
                {
                    key.Append('K').Append(contract.Concept).Append(':').Append(contract.Requirement.Type.Value)
                        .Append(':').Append(contract.Requirement.Signature);
                    foreach (var associated in contract.Associated.OrderBy(pair => pair.Key.Value))
                        key.Append('A').Append(associated.Key.Value).Append(':').Append(associated.Value.Value);
                }
        }
        key.Append('}');
    }

    // Canonical alias partitions contain no allocation identities: two unrelated
    // calls with the same alias topology share code. Aliased and disjoint inputs
    // cannot share flow proofs after writes to nullable fields or captured cells.
    private sealed class KeyProvenance(bool readOnlyInput = false, bool recursive = false)
    {
        public readonly record struct ScopeEmitPlan(bool ShouldEmit, bool IsFirst, HashSet<string>? Filter, HashSet<string>? AlreadyEmitted)
        {
            public bool Include(string field) => (Filter is null || Filter.Contains(field)) && AlreadyEmitted?.Contains(field) != true;
            public bool Skipped(string field) => Filter is not null && !Filter.Contains(field) && AlreadyEmitted?.Contains(field) != true;
        }

        public bool ReadOnlyInput { get; } = readOnlyInput;
        public bool Recursive { get; } = recursive;
        public bool Supported { get; set; } = true;
        public bool MaySkipCells { get; set; }
        public bool HasCompleteDetachedCells => !MaySkipCells || unbound is null;
        private Dictionary<HirLocationId, SemanticValue>? unbound;
        private Dictionary<int, int>? regions;
        private Dictionary<HirLocationId, int>? locations;
        private Dictionary<int, HashSet<string>?>? definedScopeFields;
        private HashSet<int>? definedIndexed;
        private HashSet<HirLocationId>? definedLocations;
        private Dictionary<SemanticScope, int>? structural;
        private Dictionary<SemanticScope, HashSet<string>?>? definedStructuralFields;

        // Distinct callable alternatives and write-only captures can omit
        // nested occurrences which still contribute to formal cell fallback.
        public void Skip(SemanticValue value)
        {
            if (ReadOnlyInput && !IsScalarSchema(value)) MaySkipCells = true;
        }

        public bool RecordUnbound(HirLocationId location, SemanticValue value)
        {
            unbound ??= [];
            if (unbound.TryAdd(location, value)) return true;
            var previous = unbound[location];
            // A formal cell can be visited through several detached closures.
            // Its fallback is independent of traversal order only if they agree.
            return ReferenceEquals(previous, value) || (previous.Type == value.Type
                && previous.DenotedType == value.DenotedType && previous.EagerRange == value.EagerRange
                && IsScalarSchema(previous) && IsScalarSchema(value)
                && previous.OpenSessionCalls.IsDefaultOrEmpty && value.OpenSessionCalls.IsDefaultOrEmpty);
        }
        private List<(string Source, char Kind, IEnumerable<int> Targets)>? aliases;

        public void Alias(SemanticScope source, char kind, IEnumerable<int> targets)
        {
            string anchor = source.Region >= 0 ? $"S{Region(source.Region)}" : $"V{Structural(source)}";
            (aliases ??= []).Add((anchor, kind, targets.Distinct()));
        }

        public void AppendAliases(StringBuilder key)
        {
            if (aliases is null) return;
            // Ordinary graph paths anchor identities before unordered edges are
            // numbered. External-only targets are distinguished by incidence,
            // never by their allocation numbers. Equal incidences are symmetric.
            var external = aliases.SelectMany(group => group.Targets.Select(target => (group.Source, group.Kind, Target: target)))
                .Where(edge => edge.Target >= 0 && regions?.ContainsKey(edge.Target) != true)
                .GroupBy(edge => edge.Target)
                .Select(group => (Target: group.Key, Signature: string.Join(";", group
                    .Select(edge => $"{edge.Source}{edge.Kind}").Order(StringComparer.Ordinal))))
                .OrderBy(node => node.Signature, StringComparer.Ordinal);
            foreach (var node in external) Region(node.Target);
            key.Append("|aliases[");
            foreach (var group in aliases.OrderBy(group => group.Source, StringComparer.Ordinal).ThenBy(group => group.Kind))
            {
                key.Append(group.Source).Append(group.Kind).Append('(');
                foreach (int target in group.Targets.Select(Region).Order()) key.Append(target).Append(',');
                key.Append(");");
            }
            key.Append(']');
        }

        public ScopeEmitPlan BeginScope(SemanticScope scope, HashSet<string>? usedFields) =>
            scope.Region >= 0
                ? PlanFields(definedScopeFields ??= [], scope.Region, usedFields)
                : PlanFields(definedStructuralFields ??= new(ReferenceEqualityComparer.Instance), scope, usedFields);

        private static ScopeEmitPlan PlanFields<TKey>(Dictionary<TKey, HashSet<string>?> table, TKey id,
            HashSet<string>? usedFields) where TKey : notnull
        {
            if (!table.TryGetValue(id, out var existing))
            {
                table[id] = usedFields is null ? null : new HashSet<string>(usedFields, StringComparer.Ordinal);
                return new(true, true, usedFields, null);
            }
            if (existing is null) return default;
            if (usedFields is null)
            {
                table[id] = null;
                return new(true, false, null, existing);
            }
            if (usedFields.IsSubsetOf(existing)) return default;
            var snapshot = new HashSet<string>(existing, StringComparer.Ordinal);
            existing.UnionWith(usedFields);
            return new(true, false, usedFields, snapshot);
        }
        public bool DefineIndexed(int region) => (definedIndexed ??= []).Add(region);
        public bool DefineLocation(HirLocationId location) => (definedLocations ??= []).Add(location);
        public int Structural(SemanticScope scope)
        {
            structural ??= new(ReferenceEqualityComparer.Instance);
            if (!structural.TryGetValue(scope, out var ordinal)) structural[scope] = ordinal = structural.Count;
            return ordinal;
        }
        public int Region(int region)
        {
            if (region < 0) return -1;
            regions ??= [];
            if (!regions.TryGetValue(region, out var ordinal)) regions[region] = ordinal = regions.Count;
            return ordinal;
        }
        public int Location(HirLocationId location)
        {
            locations ??= [];
            if (!locations.TryGetValue(location, out var ordinal)) locations[location] = ordinal = locations.Count;
            return ordinal;
        }
    }
}
