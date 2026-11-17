using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private string InstanceKey(HirTemplateId template, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext caller)
    {
        StringBuilder key = new();
        key.Append(template.Value).Append(caller.DeferredReads ? 'd' : 'i').Append(':');
        var definition = hir.Templates[template.Value];
        for (int i = 0; i < arguments.Count; i++)
            AppendStaticInput(key, template, definition.Parameters[i], arguments[i], caller, capture: false);
        key.Append('|');
        for (int i = 0; i < captures.Count; i++)
            AppendStaticInput(key, template, definition.Captures[i].Target, captures[i], caller, capture: true);
        Trace?.Metric("key.static_inputs", arguments.Count + captures.Count);
        return key.ToString();
    }

    private void AppendStaticInput(StringBuilder key, HirTemplateId template, HirLocationId input,
        SemanticValue value, SemanticContext caller, bool capture)
    {
        AppendStaticValue(key, value, caller, capture);
        if (Usage(template).FieldReads.TryGetValue(program.Original(input), out var fields))
            foreach (var field in fields.Order(StringComparer.Ordinal))
                AppendStaticMember(value, field, caller, key);
        key.Append(';');
    }

    private void AppendStaticMember(SemanticValue member, string path, SemanticContext context, StringBuilder key)
    {
        HirTypeId? carrier = null;
        foreach (string field in path.Split('\0'))
        {
            if (member.Scope is not { } row) return;
            row = context.Objects.GetValueOrDefault(row.Region, row);
            if (!TryScopeField(row, field, context, out member)) return;
            carrier = FieldCarrier(row, field, member, context);
        }
        bool hasRefinement = !context.DeferredReads && carrier is { } storage
            && hir.Types[storage.Value].TypeParameter < 0
            && Runtime(member) != TypeRef.Null && hir.Types[storage.Value].RuntimeType != Runtime(member);
        if (member.DenotedType is null && member.TypeCandidates is null
            && member.Callables.IsDefaultOrEmpty && member.FutureCallableDomain is null && !hasRefinement) return;
        key.Append('{').Append(path.Replace('\0', '.')).Append(':');
        AppendStaticValue(key, member, context);
        key.Append('}');
        Trace?.Metric("key.static_members", 1);
    }

    private HirTypeId? FieldCarrier(SemanticScope row, string name, SemanticValue member, SemanticContext context) =>
        row.Locations.TryGetValue(name, out var location) ? StorageType(location, context) : member.StorageConstraint;

    private void AppendStaticValue(StringBuilder key, SemanticValue value, SemanticContext? context, bool capture = false)
    {
        key.Append(PlanType(value).Value).Append(value.EagerRange ? '!' : '.');
        var layouts = hir.Types[value.Type.Value].ScopeDomain;
        for (int i = 0; i < layouts.Count; i++) key.Append('L').Append(layouts[i]);
        if (capture && value.StorageConstraint is { } storage) key.Append('S').Append(storage.Value);
        if (value.DenotedType is { } denoted) key.Append('T').Append(denoted.Value);
        else if (value.TypeCandidates is { } candidates)
        {
            key.Append('D');
            foreach (var type in candidates.Types) key.Append(type.Value).Append(',');
        }
        AppendCallables(value.Callables);
        if (value.FutureCallableDomain is { } future && !RedundantFutureDomain(value, future))
        {
            key.Append('U');
            AppendCallables(future.Choices);
            key.Append('C');
            AppendCallables(future.Current);
        }
        if (value.IndexedRegion is { } region && context?.Indexed.TryGetValue(region, out var indexed) == true)
            key.Append('I').Append(indexed.FixedCarrier?.Value ?? indexed.Element.Type.Value);

        void AppendCallables(System.Collections.Immutable.ImmutableArray<SemanticCallable> choices)
        {
            if (choices.IsDefaultOrEmpty) return;
            foreach (var callable in choices)
            {
                key.Append('F').Append(callable.NativeName ?? callable.Template?.Value.ToString()).Append('(');
                if (!callable.Captures.IsDefaultOrEmpty)
                    foreach (var slot in callable.Captures)
                    {
                        var captured = slot.Mode == HirCaptureMode.Location
                            ? context?.Values.GetValueOrDefault(slot.Source, slot.Value) ?? slot.Value : slot.Value;
                        key.Append(captured.Type.Value).Append('T').Append(captured.DenotedType?.Value ?? -1).Append(';');
                    }
                key.Append(')');
            }
        }
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
        Dictionary<HirLocationId, (HirLocationId Root, string? Path)> locals = [];
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
            if (!fieldReads.TryGetValue(loc, out var set)) fieldReads[loc] = set = new(StringComparer.Ordinal);
            set.Add(field);
        }

        void Visit(HirNodeId id)
        {
            var node = program.Node(id);
            if (node.Kind == HirKind.Declare)
            {
                var target = program.Original(new(node.Data));
                var initializer = program.Children(id)[0];
                if (!program.WrittenLocations.Contains(target) && InputPath(initializer) is { } initializerPath)
                    locals[target] = initializerPath;
            }
            if (node.Kind == HirKind.MemberRead && InputPath(id) is { Path: { } path } access)
            {
                AddFieldRead(access.Root, path);
                return;
            }
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
                wholeReads.Add(program.Original(new(node.Data)));
            if (node.Kind == HirKind.Call)
            {
                var inputs = program.Children(id);
                var target = program.Node(inputs[0]);
                HirTemplateId? known = target.Kind == HirKind.TemplateClosure
                    ? hir.TemplateClosures[target.Data].Template
                    : target.Kind is HirKind.ReadLocation or HirKind.ReadStorage
                        && program.FunctionLocations.TryGetValue(program.Original(new(target.Data)), out var routine)
                        ? routine : null;
                if (known is { } callee && !stack.Contains(callee))
                {
                    var uses = Usage(callee, stack);
                    var parameters = hir.Templates[callee.Value].Parameters;
                    for (int i = 0; i < Math.Min(parameters.Length, inputs.Length - 1); i++)
                        if (InputPath(inputs[i + 1]) is { } actual
                            && uses.FieldReads.TryGetValue(program.Original(parameters[i]), out var fields))
                            foreach (string field in fields)
                                AddFieldRead(actual.Root, Extend(actual.Path, field));
                }
            }
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
                    if (innerUsage.FieldReads.TryGetValue(inner, out var innerFields))
                        foreach (var f in innerFields)
                            if (locals.TryGetValue(outer, out var binding))
                                AddFieldRead(binding.Root, Extend(binding.Path, f));
                            else
                                AddFieldRead(outer, f);
                }
            }
            if (node.Kind == HirKind.Match)
                foreach (var arm in hir.Matches[node.Data].Arms) Visit(arm.Value);
            foreach (var child in program.Children(id)) Visit(child);
        }

        (HirLocationId Root, string? Path)? InputPath(HirNodeId id)
        {
            var node = program.Node(id);
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            {
                var root = program.Original(new(node.Data));
                return locals.GetValueOrDefault(root, (root, null));
            }
            return node.Kind == HirKind.MemberRead && InputPath(program.Children(id)[0]) is { } parent
                ? (parent.Root, Extend(parent.Path, node.Text!)) : null;
        }

        static string Extend(string? path, string field) => path is null ? field : path + '\0' + field;
    }

    private static bool IsScalarSchema(SemanticValue value) => value.Scope is null
        && value.Element is null && value.Callables.IsDefaultOrEmpty && value.IndexedRegion is null
        && value.DeferredEffects.IsDefaultOrEmpty && value.IndexedAliases.IsDefaultOrEmpty
        && value.FutureCallableDomain is null;

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

}
