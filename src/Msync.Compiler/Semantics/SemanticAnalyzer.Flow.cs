using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Control(HirNodeId id, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context) => node.Kind switch
        {
            HirKind.If => Conditional(node, children, context),
            HirKind.Match => Match(node, children, context),
            HirKind.For => For(id, node, children, context),
            HirKind.Loop => Loop(id, node, children, context),
            HirKind.Break => Break(node, children, context),
            HirKind.Return => Return(node, children, context),
            HirKind.Barrier => Barrier(node, children, context),
            _ => throw new InvalidOperationException($"Not a control expression: {node.Kind}")
        };

    private SemanticValue Conditional(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue condition = Analyze(children[0], context);
        Require(condition, hir.Type(TypeRef.Bool), program.Node(children[0]).Source);
        if (condition.KnownBool is { } known)
        {
            SemanticContext selected = ForkFlow(context);
            RefineCondition(children[0], known, selected);
            SemanticValue value = Analyze(children[known ? 1 : 2], selected);
            MergeContexts(context, selected);
            AppendExits(context, selected);
            HirNodeId ignored = Null(node.Source, context).Node;
            return value with
            {
                Node = Add(node, value.Type,
                known ? [condition.Node, value.Node, ignored] : [condition.Node, ignored, value.Node], context)
            };
        }
        SemanticContext yes = ForkFlow(context), no = ForkFlow(context);
        RefineCondition(children[0], true, yes);
        RefineCondition(children[0], false, no);
        SemanticValue accepted = Analyze(children[1], yes);
        SemanticValue rejected = Analyze(children[2], no);
        if (Runtime(accepted).Kind == TypeKind.Never) MergeContexts(context, no);
        else if (Runtime(rejected).Kind == TypeKind.Never) MergeContexts(context, yes);
        else MergeContexts(context, yes, no);
        AppendExits(context, yes, no);
        SemanticValue result = Join(accepted, rejected);
        return result with
        {
            Node = Add(node, result.Type,
            [condition.Node, accepted.Node, rejected.Node], context)
        };
    }

    private SemanticValue Match(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue subject = Analyze(children[0], context);
        HirMatch match = hir.Matches[node.Data];
        CheckExhaustive(subject, match, node.Source, context);
        List<HirMatchArm> arms = [];
        List<SemanticContext> branches = [];
        SemanticValue? result = null;
        TypeRef remaining = Runtime(subject);
        foreach (HirMatchArm arm in match.Arms)
        {
            bool? known = PatternMatch(subject, arm.Pattern, context);
            if (known == false) continue;
            SemanticContext branch = ForkFlow(context);
            TypeRef narrowed = PatternType(arm.Pattern, remaining, context);
            if (arm.Pattern.Kind == HirPatternKind.Type)
                RefineTypeSubject(children[0], ResolveHirType(new(arm.Pattern.Data), branch), true, branch);
            else RefineSubject(children[0], narrowed, branch);
            if (arm.Pattern.Binding is { } source)
            {
                HirLocationId location = branch.Location(source);
                var value = (Subject(children[0], branch) ?? Retype(subject, narrowed)) with { Location = location };
                branch.Values[location] = value;
                branch.Storage[location] = value.Type;
                if (context.Publish)
                    context.Output!.SetLocation(location, hir.Locations[location.Value] with { StorageType = value.Type });
            }
            SemanticValue valueResult = Analyze(arm.Value, branch);
            arms.Add(arm with { Value = valueResult.Node });
            result = result is null ? valueResult : Join(result, valueResult);
            if (Runtime(valueResult).Kind != TypeKind.Never) branches.Add(branch);
            AppendExits(context, branch);
            if (arm.Pattern.Kind is HirPatternKind.Null or HirPatternKind.Type)
            {
                TypeKind covered = arm.Pattern.Kind == HirPatternKind.Type ? narrowed.Kind & ~TypeKind.Type : narrowed.Kind;
                remaining = new(remaining.Kind & ~covered, remaining.Element);
            }
            if (known == true) break;
        }
        if (branches.Count != 0) MergeContexts(context, branches.ToArray());
        result ??= Null(node.Source, context);
        int index = node.Data;
        if (context.Publish)
        {
            index = hir.Matches.Count;
            context.Output!.AddMatch(new(arms.ToImmutableArray()));
        }
        return result with { Node = Add(node with { Data = index }, result.Type, [subject.Node], context) };
    }

    private SemanticValue For(HirNodeId id, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue sequence = Analyze(children[0], context);
        Require(sequence, hir.Type(new TypeRef(TypeKind.Range)), program.Node(children[0]).Source);
        ApplyDeferredEffects(sequence, context);
        HirFor iteration = hir.Fors[node.Data];
        SemanticContext bodyContext = ForkFlow(context);
        bodyContext.FlowAnalysis = PrepareFlow(id, context);
        HirLocationId item = bodyContext.Location(iteration.Item);
        SemanticValue element = sequence.Element ?? new(hir.Type(Runtime(sequence).Element ?? TypeRef.Any), HirNodeId.None);
        bodyContext.Values[item] = element;
        bodyContext.Storage[item] = element.Type;
        if (context.Publish)
            context.Output!.SetLocation(item, hir.Locations[item.Value] with { StorageType = element.Type });
        bodyContext.Breaks[iteration.Loop] = [];
        SemanticValue body = Analyze(children[1], bodyContext);
        MergeContexts(context, context.Fork(), bodyContext);
        context.Returns.AddRange(bodyContext.Returns);
        int index = node.Data;
        if (context.Publish)
        {
            index = hir.Fors.Count;
            context.Output!.AddFor(iteration with { Item = item });
        }
        HirTypeId result = hir.Type(TypeRef.Null);
        return new(result, Add(node with { Data = index }, result, [sequence.Node, body.Node], context));
    }

    private SemanticValue Loop(HirNodeId id, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        HirLoopId loop = new(node.Data);
        SemanticContext bodyContext = ForkFlow(context);
        bodyContext.FlowAnalysis = PrepareFlow(id, context);
        bodyContext.Breaks[loop] = [];
        SemanticValue body = Analyze(children[0], bodyContext);
        context.Returns.AddRange(bodyContext.Returns);
        var exits = bodyContext.Breaks[loop];
        SemanticValue result = exits.Count == 0
            ? new(hir.Type(new TypeRef(TypeKind.Never)), HirNodeId.None)
            : exits.Aggregate(Join);
        if (exits.Count != 0) MergeContexts(context, bodyContext);
        return result with { Node = Add(node, result.Type, [body.Node], context) };
    }

    private SemanticValue Break(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        HirLoopId loop = new(node.Data);
        if (!context.Breaks.TryGetValue(loop, out var values))
            throw Error(node.Source, "break requires an enclosing loop in this function.", "E0207");
        bool iteration = hir.Fors.Any(item => item.Loop == loop);
        if (iteration && children.Length != 0)
            throw Error(node.Source, "A for expression cannot return a break value.", "E0207");
        SemanticValue value = children.Length == 0 ? Null(node.Source, context) : Analyze(children[0], context);
        values.Add(value);
        HirTypeId never = hir.Type(new TypeRef(TypeKind.Never));
        return new(never, Add(node, never, children.Length == 0 ? [] : [value.Node], context));
    }

    private SemanticValue Return(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue value = children.Length == 0 ? Null(node.Source, context) : Analyze(children[0], context);
        if (context.ExpectedReturn is { } expected)
        {
            expected = Resolve(expected, context);
            Require(value, expected, node.Source, allowImplicit: true, context: context);
            value = Convert(value, expected, node.Source, context);
        }
        context.Returns.Add(value);
        HirTypeId never = hir.Type(new TypeRef(TypeKind.Never));
        return new(never, Add(node, never, [value.Node], context));
    }

    private SemanticValue Barrier(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue value = children.Length == 0 ? Null(node.Source, context) : Analyze(children[0], context);
        HirBarrier barrier = hir.Barriers[node.Data];
        if (barrier.Release && children.Length != 0 && context.Publish)
            value = value with { Node = ScheduleBarrierValue(value.Node, barrier.Acquire ? 3 : 1, context.Output!) };
        return value with { Node = Add(node, value.Type, children.Length == 0 ? [] : [value.Node], context) };
    }

    private HirNodeId ScheduleBarrierValue(HirNodeId id, int scheduling, SemanticOutput output)
    {
        HirNode node = hir.Nodes[id.Value];
        var children = hir.Children(node).ToArray();
        if (node.Kind is HirKind.Assign or HirKind.MemberAssign)
        {
            children[^1] = ScheduleBarrierValue(children[^1], scheduling, output);
            return output.Add(node.Kind, node.Type, node.Source, children, node.Data, node.Text, node.Effects);
        }
        if (node.Kind != HirKind.Call || hir.Calls[node.Data].Kind != HirCallKind.Path) return id;
        int call = hir.Calls.Count;
        output.AddCall(hir.Calls[node.Data] with { Scheduling = scheduling });
        return output.Add(HirKind.Call, node.Type, node.Source, children, call, node.Text, node.Effects);
    }

    private SemanticValue Null(HirSourceRange source, SemanticContext context)
    {
        HirTypeId type = hir.Type(TypeRef.Null);
        HirLiteral literal = new(HirLiteralKind.Null);
        return new(type, context.Publish ? context.Output!.Literal(literal, type, source) : HirNodeId.None, literal);
    }

    private SemanticValue Retype(SemanticValue value, TypeRef type) =>
        value with { Type = hir.Type(hir.Types[value.Type.Value] with { RuntimeType = type }) };

    private void RefineCondition(HirNodeId expression, bool positive, SemanticContext context)
    {
        HirNode node = program.Node(expression);
        var children = program.Children(expression);
        if (node.Kind == HirKind.Unary && (HirUnaryOperator)node.Data == HirUnaryOperator.Not)
        {
            RefineCondition(children[0], !positive, context);
            return;
        }
        if (node.Kind == HirKind.Binary && ((HirBinaryOperator)node.Data == HirBinaryOperator.And && positive
            || (HirBinaryOperator)node.Data == HirBinaryOperator.Or && !positive))
        {
            RefineCondition(children[0], positive, context);
            RefineCondition(children[1], positive, context);
            return;
        }
        if (node.Kind == HirKind.IsNull)
            RefineSubject(children[0], Intersect(ReadType(children[0], context), TypeRef.Null, positive), context);
        else if (node.Kind == HirKind.IsType)
        {
            HirTypeId? target = children.Length == 2
                ? program.Node(children[1]).Kind == HirKind.TypeValue
                    ? ResolveHirType(new(program.Node(children[1]).Data), context) : Subject(children[1], context)?.DenotedType
                : node.Data >= 0 ? ResolveHirType(new(node.Data), context) : null;
            if (target is { } known) RefineTypeSubject(children[0], known, positive, context);
        }
    }

    private TypeRef ReadType(HirNodeId expression, SemanticContext context)
    {
        HirNode node = program.Node(expression);
        if (Subject(expression, context) is { } value) return Runtime(value);
        return hir.Types[Resolve(node.Type, context).Value].RuntimeType;
    }

    private void RefineSubject(HirNodeId expression, TypeRef type, SemanticContext context)
    {
        if (Subject(expression, context) is { } value) SetSubject(expression, Retype(value, type), context);
    }

    private void RefineTypeSubject(HirNodeId expression, HirTypeId target, bool positive, SemanticContext context)
    {
        if (Subject(expression, context) is not { } value) return;
        TypeRef expected = hir.Types[target.Value].RuntimeType;
        TypeRef actual = Runtime(value);
        TypeRef refined = Intersect(actual, expected, positive);
        if ((actual.Kind & TypeKind.Type) != 0)
        {
            refined = refined with { Kind = refined.Kind | TypeKind.Type };
            bool exact = expected.Kind is not (TypeKind.Range or TypeKind.Never)
                && ((ushort)expected.Kind & ((ushort)expected.Kind - 1)) == 0;
            if (positive && refined.Kind == TypeKind.Type && exact)
                value = value with { DenotedType = target, Literal = new(HirLiteralKind.Type, Type: expected) };
        }
        SetSubject(expression, Retype(value, refined), context);
    }

    private SemanticValue? Subject(HirNodeId expression, SemanticContext context)
    {
        HirNode node = program.Node(expression);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            return context.Values.GetValueOrDefault(context.Location(new(node.Data)));
        if (node.Kind != HirKind.MemberRead || Subject(program.Children(expression)[0], context)?.Scope is not { } scope)
            return null;
        if (context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        if (scope.Locations.TryGetValue(node.Text!, out var location) && context.Values.TryGetValue(location, out var value)) return value;
        return scope.Fields.GetValueOrDefault(node.Text!);
    }

    private void SetSubject(HirNodeId expression, SemanticValue value, SemanticContext context)
    {
        HirNode node = program.Node(expression);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
        {
            context.Values[context.Location(new(node.Data))] = value;
            return;
        }
        if (node.Kind != HirKind.MemberRead || Subject(program.Children(expression)[0], context)?.Scope is not { } scope) return;
        if (context.Objects.TryGetValue(scope.Region, out var latest)) scope = latest;
        context.Objects[scope.Region] = scope with { Fields = scope.Fields.SetItem(node.Text!, value) };
        if (scope.Locations.TryGetValue(node.Text!, out var location)) context.Values[location] = value;
    }

    private static TypeRef Intersect(TypeRef actual, TypeRef expected, bool positive) =>
        new(positive ? actual.Kind & expected.Kind : actual.Kind & ~expected.Kind, actual.Element);

    private bool? PatternMatch(SemanticValue value, HirPattern pattern, SemanticContext context) => pattern.Kind switch
    {
        HirPatternKind.Any => true,
        HirPatternKind.Null => Runtime(value) == TypeRef.Null ? true : !Runtime(value).Nullable ? false : null,
        HirPatternKind.Type => TypePatternMatch(value, Resolve(new(pattern.Data), context)),
        HirPatternKind.Literal => value.KnownBool is { } boolean && hir.Literals[pattern.Data].Kind == HirLiteralKind.Bool
            ? boolean == hir.Literals[pattern.Data].Boolean : null,
        HirPatternKind.Regex => (Runtime(value).Kind & (TypeKind.Path | TypeKind.String)) == 0 ? false : null,
        _ => null
    };

    private bool? TypePatternMatch(SemanticValue value, HirTypeId type)
    {
        TypeRef expected = hir.Types[type.Value].RuntimeType;
        if (value.Literal is { Kind: HirLiteralKind.Type, Type: { } denoted }) return expected.Accepts(denoted);
        if ((Runtime(value).Kind & TypeKind.Type) != 0) return null;
        return expected.Accepts(Runtime(value)) ? true : (Runtime(value).Kind & expected.Kind) == 0 ? false : null;
    }

    private TypeRef PatternType(HirPattern pattern, TypeRef original, SemanticContext context) => pattern.Kind switch
    {
        HirPatternKind.Null => TypeRef.Null,
        HirPatternKind.Type => MatchType(original, hir.Types[ResolveHirType(new(pattern.Data), context).Value].RuntimeType),
        HirPatternKind.Regex => new(original.Kind & (TypeKind.Path | TypeKind.String), original.Element),
        HirPatternKind.Literal => hir.Literals[pattern.Data].Kind switch
        {
            HirLiteralKind.Bool => TypeRef.Bool,
            HirLiteralKind.String => new(original.Kind & (TypeKind.Path | TypeKind.String), original.Element),
            _ => original
        },
        _ => original
    };

    private static TypeRef MatchType(TypeRef original, TypeRef tested)
    {
        TypeRef result = Intersect(original, tested, true);
        return (original.Kind & TypeKind.Type) != 0 ? result with { Kind = result.Kind | TypeKind.Type } : result;
    }

    private void CheckExhaustive(SemanticValue value, HirMatch match, HirSourceRange source,
        SemanticContext context)
    {
        TypeKind covered = TypeKind.Never;
        bool hasTrue = false, hasFalse = false;
        foreach (HirMatchArm arm in match.Arms)
        {
            HirPattern pattern = arm.Pattern;
            if (pattern.Kind == HirPatternKind.Any) return;
            if (pattern.Kind == HirPatternKind.Null) covered |= TypeKind.Null;
            if (pattern.Kind == HirPatternKind.Type)
            {
                HirTypeId type = Resolve(new(pattern.Data), context);
                if (TypePatternMatch(value, type) == true) return;
                TypeRef runtime = hir.Types[type.Value].RuntimeType;
                covered |= runtime.Kind & ~TypeKind.Type;
                if (runtime == TypeRef.Any) covered |= TypeKind.Type;
            }
            if (pattern.Kind == HirPatternKind.Literal && hir.Literals[pattern.Data].Kind == HirLiteralKind.Bool)
            {
                hasTrue |= hir.Literals[pattern.Data].Boolean;
                hasFalse |= !hir.Literals[pattern.Data].Boolean;
            }
        }
        if (hasTrue && hasFalse) covered |= TypeKind.Bool;
        TypeKind missing = Runtime(value).Kind & ~covered;
        if (missing != TypeKind.Never)
            throw Error(source, $"Non-exhaustive match for {Runtime(value)}; missing {new TypeRef(missing)} or a final '_'.", "E0209");
    }

    private void MergeContexts(SemanticContext destination, params SemanticContext[] branches)
    {
        if (branches.Length == 0) return;
        MergeSessionContracts(destination, branches);
        foreach (SemanticContext branch in branches)
        {
            destination.CellWrites.UnionWith(branch.CellWrites);
            destination.IndexedWrites.UnionWith(branch.IndexedWrites);
            destination.FieldWrites.UnionWith(branch.FieldWrites);
            destination.ReplacedProgramCells = destination.ReplacedProgramCells.Union(branch.ReplacedProgramCells);
        }
        MergeSingletonFlow(destination, branches);
        MergeIndexedContexts(destination, branches);
        if (!destination.Publish) { MergeQueryContexts(destination, branches); return; }
        foreach (var pair in branches[0].Values) destination.Values[pair.Key] = pair.Value;
        foreach (var pair in branches[0].Storage) destination.Storage[pair.Key] = pair.Value;
        foreach (var pair in branches[0].Objects) destination.Objects[pair.Key] = pair.Value;
        foreach (SemanticContext branch in branches.Skip(1))
        {
            foreach (var (location, value) in branch.Values)
                destination.Values[location] = destination.Values.TryGetValue(location, out var previous) ? Join(previous, value) : value;
            foreach (var (location, type) in branch.Storage) destination.Storage[location] = type;
            foreach (var (region, scope) in branch.Objects)
            {
                if (!destination.Objects.TryGetValue(region, out var previous)) { destination.Objects[region] = scope; continue; }
                var fields = previous.Fields;
                foreach (var (name, value) in scope.Fields)
                    fields = fields.SetItem(name, fields.TryGetValue(name, out var before) ? Join(before, value) : value);
                destination.Objects[region] = previous with { Fields = fields };
            }
        }
    }

    private void MergeIndexedContexts(SemanticContext destination, SemanticContext[] branches)
    {
        HashSet<int>? changed = null;
        foreach (SemanticContext branch in branches)
        {
            destination.IndexedWrites.UnionWith(branch.IndexedWrites);
            foreach (var (region, value) in branch.Indexed.LocalEntries)
                if (!destination.Indexed.TryGetValue(region, out var previous) || !ReferenceEquals(previous, value))
                    (changed ??= []).Add(region);
        }
        if (changed is null) return;
        Dictionary<int, SemanticIndexed> merged = new(changed.Count);
        foreach (int region in changed)
        {
            SemanticIndexed? resource = null;
            foreach (SemanticContext branch in branches)
                if (branch.Indexed.TryGetValue(region, out var value))
                    resource = resource is null ? value : JoinIndexed(resource, value);
            if (resource is not null) merged.Add(region, resource);
        }
        foreach (var (region, resource) in merged) destination.Indexed[region] = resource;
    }

    private void MergeQueryContexts(SemanticContext destination, SemanticContext[] branches)
    {
        HashSet<HirLocationId>? values = null, storage = null;
        HashSet<int>? objects = null;
        foreach (SemanticContext branch in branches)
        {
            foreach (var pair in branch.Values.LocalEntries) (values ??= []).Add(pair.Key);
            foreach (var pair in branch.Storage.LocalEntries) (storage ??= []).Add(pair.Key);
            foreach (var pair in branch.Objects.LocalEntries) (objects ??= []).Add(pair.Key);
        }
        if (values is not null)
            foreach (HirLocationId location in values)
            {
                SemanticValue? merged = null;
                foreach (SemanticContext branch in branches)
                    if (branch.Values.TryGetValue(location, out var value)) merged = merged is null ? value : Join(merged, value);
                if (merged is not null) destination.Values[location] = merged;
            }
        if (storage is not null)
            foreach (HirLocationId location in storage)
                foreach (SemanticContext branch in branches)
                    if (branch.Storage.TryGetValue(location, out var type)) destination.Storage[location] = type;
        if (objects is null) return;
        foreach (int region in objects)
        {
            SemanticScope? merged = null;
            foreach (SemanticContext branch in branches)
            {
                if (!branch.Objects.TryGetValue(region, out var scope)) continue;
                if (merged is null) { merged = scope; continue; }
                var fields = merged.Fields;
                foreach (var (name, value) in scope.Fields)
                    fields = fields.SetItem(name, fields.TryGetValue(name, out var previous) ? Join(previous, value) : value);
                merged = merged with { Fields = fields };
            }
            if (merged is not null) destination.Objects[region] = merged;
        }
    }

    private static void AppendExits(SemanticContext destination, params SemanticContext[] branches)
    {
        foreach (SemanticContext branch in branches)
        {
            destination.Returns.AddRange(branch.Returns);
            foreach (var (loop, values) in branch.Breaks)
                if (destination.Breaks.TryGetValue(loop, out var target)) target.AddRange(values);
        }
    }

    private static SemanticContext ForkFlow(SemanticContext context)
    {
        SemanticContext result = context.Fork();
        foreach (HirLoopId loop in context.Breaks.Keys) result.Breaks.Add(loop, []);
        return result;
    }
}
