using Msync.Hir;
using Msync.Core;

namespace Msync.Compiler.Semantics;

// The source-owned write domain names binding definitions, not runtime cells.
// Points-to facts here only select Scope field declarations; object identities
// remain separate in semantic analysis and in the interpreter.
internal sealed class BoundCallableWrites
{
    private readonly BoundProgram program;
    private readonly Dictionary<HirLocationId, HashSet<HirNodeId>> writes = [];
    private readonly Dictionary<HirLocationId, HashSet<int>> scopes = [];
    private readonly List<HirNodeId> members = [];
    private readonly List<HirNodeId> calls = [];
    private readonly Dictionary<HirLocationId, HashSet<HirTemplateId>> functions = [];
    private readonly HashSet<HirNodeId> nodes = [];
    private readonly HashSet<string> memberNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> possibleMemberNames = new(StringComparer.Ordinal);
    private bool resolved;
    private readonly HashSet<HirTemplateId> reachable = [];
    private readonly Dictionary<(HirLocationId Location, HirNodeId Value), HashSet<HirNodeId>> receivers = [];
    private readonly Dictionary<HirLocationId, HashSet<HirNodeId>> arguments = [];
    private readonly Dictionary<(HirLocationId Location, HirNodeId Value), HirTemplateId> writeOwners = [];
    private readonly Dictionary<HirTemplateId, HashSet<HirNodeId>> callSites = [];

    internal BoundCallableWrites(BoundProgram program)
    {
        this.program = program;
        foreach (var pair in program.StandardModules)
            if (program.StandardLocations.TryGetValue(pair.Key, out HirLocationId location))
                Scope(location).Add(program.Node(pair.Value.Body).Data);
        foreach (var pair in program.FunctionLocations) Function(pair.Key).Add(pair.Value);
        Visit(program.Body);
        foreach (BoundModule module in program.StandardModules.Values) Visit(module.Body);
        HashSet<HirNodeId> inspected = [];
        foreach (BoundTemplate template in program.Templates) MemberNames(template.Body, inspected);
    }

    private void Resolve()
    {
        if (resolved) return;
        resolved = true;
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in writes)
                foreach (HirNodeId expression in pair.Value)
                {
                    changed |= Union(Scope(pair.Key), ScopeValues(expression, []));
                    changed |= Union(Function(pair.Key), FunctionValues(expression, []));
                }
            for (int index = 0; index < calls.Count; index++)
            {
                HirNodeId id = calls[index];
                changed |= Arguments(id);
                changed |= Reach(id);
            }
            foreach (HirTemplateId template in reachable)
                foreach (HirCapture capture in program.Hir.Templates[template.Value].Captures)
                {
                    changed |= Union(Scope(capture.Target), Scope(capture.SourceLocation));
                    changed |= Union(Function(capture.Target), Function(capture.SourceLocation));
                }
            foreach (HirNodeId id in members)
            {
                HirNode node = program.Node(id);
                var children = program.Children(id);
                foreach (int scope in ScopeValues(children[0], []))
                    foreach (HirLocationId field in program.Hir.Scopes[scope].Fields)
                        if (program.Hir.Locations[field.Value].Name == node.Text)
                        {
                            changed |= Write(field).Add(children[1]);
                            var key = (program.Original(field), children[1]);
                            if (!receivers.TryGetValue(key, out var owners)) receivers.Add(key, owners = []);
                            owners.Add(children[0]);
                        }
            }
        } while (changed);
    }

    internal IEnumerable<HirNodeId> Values(HirLocationId location)
    {
        location = program.Original(location);
        if (program.WrittenLocations.Contains(location) || possibleMemberNames.Contains(program.Hir.Locations[location.Value].Name))
            Resolve();
        return writes.TryGetValue(location, out var values) ? values : [];
    }

    internal IEnumerable<HirNodeId> Receivers(HirLocationId location, HirNodeId value) =>
        receivers.TryGetValue((program.Original(location), value), out var owners) ? owners : [];

    internal IEnumerable<HirNodeId> Arguments(HirLocationId parameter) =>
        arguments.TryGetValue(program.Original(parameter), out var values) ? values : [];

    internal bool WritesMember(string name) => memberNames.Contains(name);

    internal HirTemplateId? Writer(HirLocationId location, HirNodeId value) =>
        writeOwners.TryGetValue((program.Original(location), value), out HirTemplateId owner) ? owner : null;

    internal IEnumerable<HirNodeId> CallSites(HirTemplateId template) =>
        callSites.TryGetValue(template, out var values) ? values : [];

    private bool Reach(HirNodeId id)
    {
        var children = program.Children(id);
        bool changed = false;
        foreach (HirTemplateId target in FunctionValues(children[0], []))
        {
            changed |= Body(target);
            Site(target, children[0]);
        }
        string? native = program.Hir.Calls[program.Node(id).Data].NativeName
            ?? Native(children[0]);
        if (native is not null && NativeSignatures.TryGet(native, out var signature)
            && (signature.Behavior.GetValueOrDefault() & NativeBehavior.Invokes) != 0)
            for (int index = 1; index < children.Length; index++)
                foreach (HirTemplateId callback in FunctionValues(children[index], []))
                {
                    changed |= Body(callback);
                    Site(callback, children[index]);
                }
        return changed;
    }

    private void Site(HirTemplateId template, HirNodeId callee)
    {
        if (!callSites.TryGetValue(template, out var sites)) callSites.Add(template, sites = []);
        sites.Add(callee);
    }

    private string? Native(HirNodeId id) => Native(id, []);

    private string? Native(HirNodeId id, HashSet<HirNodeId> active)
    {
        if (!active.Add(id)) return null;
        HirNode node = program.Node(id);
        if (node.Kind == HirKind.NativeFunction) return node.Text;
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            return program.NativeLocations.GetValueOrDefault(program.Original(new(node.Data)));
        if (node.Kind == HirKind.Call)
        {
            List<string?> names = [];
            foreach (HirTemplateId template in FunctionValues(program.Children(id)[0], []))
            {
                HirNodeId body = program.Hir.Templates[template.Value].Body;
                HirNodeId[] children = program.Children(body).ToArray();
                if (program.Node(body).Kind == HirKind.Block && children.Length != 0) body = children[^1];
                names.Add(Native(body, new(active)));
                foreach (HirNodeId value in ReturnValues(program.Hir.Templates[template.Value].Body))
                    names.Add(Native(value, new(active)));
            }
            return names.Count != 0 && names.All(name => name == names[0]) ? names[0] : null;
        }
        if (node.Kind != HirKind.MemberRead) return null;
        foreach (int scope in ScopeValues(program.Children(id)[0], []))
            foreach (HirLocationId field in program.Hir.Scopes[scope].Fields)
                if (program.Hir.Locations[field.Value].Name == node.Text
                    && program.NativeLocations.TryGetValue(field, out string? native)) return native;
        return null;
    }

    private bool Body(HirTemplateId template)
    {
        if (program.Hir.Templates[template.Value].Body.IsNone) return false;
        if (!reachable.Add(template)) return false;
        Visit(program.Hir.Templates[template.Value].Body);
        return true;
    }

    private void Visit(HirNodeId id)
    {
        if (id.IsNone) return;
        if (!nodes.Add(id)) return;
        HirNode node = program.Node(id);
        var children = program.Children(id);
        if (node.Kind == HirKind.TypeOf) return;
        if (node.Kind == HirKind.Call
            && SemanticAnalyzer.IsDependentMeta(program.Hir.Calls[node.Data].NativeName ?? Native(children[0])))
        {
            Visit(children[0]);
            return;
        }
        if (node.Kind == HirKind.If && KnownBoolean(children[0]) is { } selected)
        {
            Visit(children[0]);
            Visit(children[selected ? 1 : 2]);
            return;
        }
        if (node.Kind is HirKind.Declare or HirKind.Assign) Write(new(node.Data)).Add(children[^1]);
        if (node.Kind == HirKind.Assign && program.TryDeclaration(new(node.Data), out BoundDeclaration declaration)
            && declaration.Template is { } owner)
            writeOwners[(program.Original(new(node.Data)), children[^1])] = owner;
        if (node.Kind == HirKind.MemberAssign)
        {
            members.Add(id);
            memberNames.Add(node.Text!);
            possibleMemberNames.Add(node.Text!);
        }
        if (node.Kind == HirKind.Call) calls.Add(id);
        foreach (HirNodeId child in children) Visit(child);
        if (node.Kind == HirKind.Match)
            foreach (HirMatchArm arm in program.Hir.Matches[node.Data].Arms) Visit(arm.Value);
    }

    private void MemberNames(HirNodeId id, HashSet<HirNodeId> inspected)
    {
        if (id.IsNone) return;
        if (!inspected.Add(id)) return;
        HirNode node = program.Node(id);
        if (node.Kind == HirKind.MemberAssign) possibleMemberNames.Add(node.Text!);
        foreach (HirNodeId child in program.Children(id)) MemberNames(child, inspected);
        if (node.Kind == HirKind.Match)
            foreach (HirMatchArm arm in program.Hir.Matches[node.Data].Arms) MemberNames(arm.Value, inspected);
    }

    private bool? KnownBoolean(HirNodeId id)
    {
        HirNode node = program.Node(id);
        if (node.Kind == HirKind.Literal && program.Hir.Literals[node.Data].Kind == HirLiteralKind.Bool)
            return program.Hir.Literals[node.Data].Boolean;
        return node.Kind == HirKind.Unary && (HirUnaryOperator)node.Data == HirUnaryOperator.Not
            && KnownBoolean(program.Children(id)[0]) is { } known ? !known : null;
    }

    private bool Arguments(HirNodeId id)
    {
        var children = program.Children(id);
        bool changed = false;
        foreach (HirTemplateId target in FunctionValues(children[0], []))
        {
            HirTemplate template = program.Hir.Templates[target.Value];
            for (int index = 0; index < Math.Min(template.Parameters.Length, children.Length - 1); index++)
            {
                HirLocationId parameter = program.Original(template.Parameters[index]);
                if (!arguments.TryGetValue(parameter, out var inputs)) arguments.Add(parameter, inputs = []);
                changed |= inputs.Add(children[index + 1]);
                changed |= Union(Scope(template.Parameters[index]), ScopeValues(children[index + 1], []));
                changed |= Union(Function(template.Parameters[index]), FunctionValues(children[index + 1], []));
            }
        }
        return changed;
    }

    private IEnumerable<int> ScopeValues(HirNodeId id, HashSet<HirNodeId> active)
    {
        if (id.IsNone) return [];
        if (!active.Add(id)) return [];
        HirNode node = program.Node(id);
        var children = program.Children(id);
        IEnumerable<int> result = node.Kind switch
        {
            HirKind.Scope => [node.Data],
            HirKind.ReadLocation or HirKind.ReadStorage => Scope(new(node.Data)).ToArray(),
            HirKind.Block => children.Length == 0 ? [] : ScopeValues(children[^1], active),
            HirKind.Return or HirKind.Coerce or HirKind.Convert => children.Length == 0 ? [] : ScopeValues(children[0], active),
            HirKind.If => ScopeValues(children[1], active).Concat(ScopeValues(children[2], active)).ToArray(),
            HirKind.MemberRead => ScopeMembers(children[0], node.Text!, active),
            HirKind.Call => ScopeResults(children[0], active),
            _ => []
        };
        int[] values = result.Distinct().ToArray();
        active.Remove(id);
        return values;
    }

    private IEnumerable<int> ScopeMembers(HirNodeId receiver, string name, HashSet<HirNodeId> active)
    {
        List<int> result = [];
        foreach (int scope in ScopeValues(receiver, active))
            foreach (HirLocationId field in program.Hir.Scopes[scope].Fields)
                if (program.Hir.Locations[field.Value].Name == name) result.AddRange(Scope(field));
        return result;
    }

    private IEnumerable<int> ScopeResults(HirNodeId callee, HashSet<HirNodeId> active)
    {
        List<int> result = [];
        foreach (HirTemplateId template in FunctionValues(callee, []))
        {
            result.AddRange(ScopeValues(program.Hir.Templates[template.Value].Body, active));
            foreach (HirNodeId expression in ReturnValues(program.Hir.Templates[template.Value].Body))
                result.AddRange(ScopeValues(expression, active));
        }
        return result;
    }

    private IEnumerable<HirTemplateId> FunctionValues(HirNodeId id, HashSet<HirNodeId> active)
    {
        if (id.IsNone) return [];
        if (!active.Add(id)) return [];
        HirNode node = program.Node(id);
        var children = program.Children(id);
        HirTemplateId[] result = node.Kind switch
        {
            HirKind.TemplateClosure => [program.Hir.TemplateClosures[node.Data].Template],
            HirKind.ReadLocation or HirKind.ReadStorage => Function(new(node.Data)).ToArray(),
            HirKind.Block => children.Length == 0 ? [] : FunctionValues(children[^1], active).ToArray(),
            HirKind.Return => children.Length == 0 ? [] : FunctionValues(children[0], active).ToArray(),
            HirKind.If => FunctionValues(children[1], active).Concat(FunctionValues(children[2], active)).Distinct().ToArray(),
            HirKind.MemberRead => FunctionMembers(children[0], node.Text!, active).ToArray(),
            HirKind.Call => FunctionResults(children[0], active).ToArray(),
            _ => []
        };
        active.Remove(id);
        return result;
    }

    private IEnumerable<HirTemplateId> FunctionResults(HirNodeId callee, HashSet<HirNodeId> active)
    {
        List<HirTemplateId> result = [];
        foreach (HirTemplateId template in FunctionValues(callee, active))
        {
            result.AddRange(FunctionValues(program.Hir.Templates[template.Value].Body, active));
            foreach (HirNodeId expression in ReturnValues(program.Hir.Templates[template.Value].Body))
                result.AddRange(FunctionValues(expression, active));
        }
        return result;
    }

    private IEnumerable<HirNodeId> ReturnValues(HirNodeId id)
    {
        if (id.IsNone) yield break;
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.TemplateClosure or HirKind.Closure) yield break;
        HirNodeId[] children = program.Children(id).ToArray();
        if (node.Kind == HirKind.Return && children.Length != 0) { yield return children[0]; yield break; }
        if (node.Kind == HirKind.If && KnownBoolean(children[0]) is { } selected)
            children = [children[selected ? 1 : 2]];
        foreach (HirNodeId child in children)
            foreach (HirNodeId result in ReturnValues(child)) yield return result;
        if (node.Kind == HirKind.Match)
            foreach (HirMatchArm arm in program.Hir.Matches[node.Data].Arms)
                foreach (HirNodeId result in ReturnValues(arm.Value)) yield return result;
    }

    private IEnumerable<HirTemplateId> FunctionMembers(HirNodeId receiver, string name, HashSet<HirNodeId> active)
    {
        List<HirTemplateId> result = [];
        foreach (int scope in ScopeValues(receiver, active))
            foreach (HirLocationId field in program.Hir.Scopes[scope].Fields)
                if (program.Hir.Locations[field.Value].Name == name) result.AddRange(Function(field));
        return result;
    }

    private HashSet<HirNodeId> Write(HirLocationId location)
    {
        location = program.Original(location);
        if (!writes.TryGetValue(location, out var values)) writes.Add(location, values = []);
        return values;
    }

    private HashSet<int> Scope(HirLocationId location)
    {
        location = program.Original(location);
        if (!scopes.TryGetValue(location, out var values)) scopes.Add(location, values = []);
        return values;
    }

    private HashSet<HirTemplateId> Function(HirLocationId location)
    {
        location = program.Original(location);
        if (!functions.TryGetValue(location, out var values)) functions.Add(location, values = []);
        return values;
    }

    private static bool Union<T>(HashSet<T> destination, IEnumerable<T> values)
    {
        bool changed = false;
        foreach (T value in values) changed |= destination.Add(value);
        return changed;
    }
}
