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
                BranchValue = null,
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
            [condition.Node, accepted.Node, rejected.Node], context),
            BranchValue = CorrelatedBranches(accepted, rejected) ? new([accepted, rejected]) : null
        };
    }

    private bool CorrelatedBranches(SemanticValue yes, SemanticValue no)
    {
        if (yes.DenotedType is { } first && no.DenotedType is { } second) return first != second;
        if (yes.Scope is not { } left || no.Scope is not { } right) return false;
        foreach (var field in left.Fields)
            if (field.Value.DenotedType is { } known && right.Fields.TryGetValue(field.Key, out var other)
                && other.DenotedType is { } different && known != different) return true;
        return false;
    }

    private SemanticValue Match(HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue subject = Analyze(children[0], context);
        HirMatch match = hir.Matches[node.Data];
        CheckExhaustive(subject, match, node.Source, context);
        List<HirMatchArm> arms = [];
        List<SemanticValue> armValues = [];
        List<SemanticContext> branches = [];
        SemanticValue? result = null;
        TypeRef remaining = Runtime(subject);
        var denotations = remaining == TypeRef.Type ? subject.DenotationDomain : null;
        foreach (HirMatchArm arm in match.Arms)
        {
            bool? known = PatternMatch(subject, arm.Pattern, context);
            var typeCases = denotations?.Types ?? [];
            if (denotations is not null && arm.Pattern.Kind == HirPatternKind.Type)
            {
                typeCases = MatchingDenotations(denotations, arm.Pattern, context);
                if (typeCases.IsEmpty) continue;
                if (typeCases.Length == denotations.Types.Length) known = true;
            }
            if (known == false) continue;
            SemanticContext branch = ForkFlow(context);
            TypeRef narrowed = PatternType(arm.Pattern, remaining, context);
            if (arm.Pattern.Kind == HirPatternKind.Type)
                RefineTypeSubject(children[0], ResolveHirType(new(arm.Pattern.Data), branch), true, branch);
            else RefineSubject(children[0], narrowed, branch);
            if (denotations is not null && !typeCases.IsEmpty
                && Subject(children[0], branch) is { } narrowedValue)
            {
                NarrowLayoutChoice(children[0], typeCases, branch);
                SetSubject(children[0], WithDenotations(narrowedValue, typeCases), branch);
            }
            if (arm.Pattern.Binding is { } source)
            {
                HirLocationId location = branch.Location(source);
                var value = Subject(children[0], branch) ?? Retype(subject, narrowed);
                if (denotations is not null && !typeCases.IsEmpty) value = WithDenotations(value, typeCases);
                value = value with { Location = location };
                branch.Values[location] = value;
                branch.Storage[location] = value.Type;
                if (context.Publish)
                    context.Output!.SetLocation(location, hir.Locations[location.Value] with { StorageType = value.Type });
            }
            SemanticValue valueResult = Analyze(arm.Value, branch);
            var pattern = known == true ? new HirPattern(HirPatternKind.Any, Binding: arm.Pattern.Binding) : arm.Pattern;
            arms.Add(new(pattern, valueResult.Node));
            armValues.Add(valueResult);
            result = result is null ? valueResult : Join(result, valueResult);
            if (Runtime(valueResult).Kind != TypeKind.Never) branches.Add(branch);
            AppendExits(context, branch);
            if (arm.Pattern.Kind is HirPatternKind.Null or HirPatternKind.Type)
            {
                TypeKind covered = arm.Pattern.Kind == HirPatternKind.Type ? narrowed.Kind & ~TypeKind.Type : narrowed.Kind;
                remaining = new(remaining.Kind & ~covered, remaining.Element);
            }
            if (known == true) break;
            if (denotations is not null && arm.Pattern.Kind == HirPatternKind.Type)
                denotations = new(denotations.Types.Except(typeCases));
        }
        if (branches.Count != 0) MergeContexts(context, branches.ToArray());
        result ??= Null(node.Source, context);
        int index = node.Data;
        if (context.Publish)
        {
            index = hir.Matches.Count;
            context.Output!.AddMatch(new(arms.ToImmutableArray()));
        }
        var branchValue = armValues.Count > 1 && armValues.All(value => Runtime(value).Kind != TypeKind.Never)
            && armValues.Skip(1).Any(value => CorrelatedBranches(armValues[0], value))
            ? new SemanticBranchValue([.. armValues]) : null;
        return result with { Node = Add(node with { Data = index }, result.Type, [subject.Node], context),
            BranchValue = branchValue };
    }

    private SemanticValue For(HirNodeId id, HirNode node, ReadOnlySpan<HirNodeId> children,
        SemanticContext context)
    {
        SemanticValue sequence = Analyze(children[0], context);
        Require(sequence, hir.Type(new TypeRef(TypeKind.Range)), program.Node(children[0]).Source);
        ApplyDeferredEffects(sequence, context);
        ActivateLoopWrites(node, context);
        HirFor iteration = hir.Fors[node.Data];
        SemanticContext bodyContext = ForkFlow(context);
        bodyContext.FlowAnalysis = PrepareFlow(id, context, sequence);
        HirLocationId item = bodyContext.Location(iteration.Item);
        SemanticValue element = sequence.Element ?? new(hir.Type(Runtime(sequence).Element ?? TypeRef.Any), HirNodeId.None);
        bodyContext.Values[item] = element;
        bodyContext.Storage[item] = element.Type;
        if (context.Publish)
            context.Output!.SetLocation(item, hir.Locations[item.Value] with { StorageType = element.Type });
        bodyContext.Breaks[iteration.Loop] = [];
        SemanticValue body = Analyze(children[1], bodyContext);
        var exits = bodyContext.Breaks[iteration.Loop].Select(exit => exit.Events).ToList();
        if (Runtime(body).Kind != TypeKind.Never) exits.Add(bodyContext.WriteEvents?.ToImmutableArray() ?? []);
        MergeLoopExits(context, bodyContext, exits, node.Source, maySkip: true);
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
        ActivateLoopWrites(node, context);
        SemanticContext bodyContext = ForkFlow(context);
        bodyContext.FlowAnalysis = PrepareFlow(id, context);
        bodyContext.Breaks[loop] = [];
        SemanticValue body = Analyze(children[0], bodyContext);
        context.Returns.AddRange(bodyContext.Returns);
        var exits = bodyContext.Breaks[loop];
        SemanticValue result = exits.Count == 0
            ? new(hir.Type(new TypeRef(TypeKind.Never)), HirNodeId.None)
            : exits.Select(exit => exit.Value).Aggregate(Join);
        if (exits.Count != 0)
            MergeLoopExits(context, bodyContext, exits.Select(exit => exit.Events).ToArray(), node.Source, maySkip: false);
        return result with { Node = Add(node, result.Type, [body.Node], context) };
    }

    private void ActivateLoopWrites(HirNode node, SemanticContext context)
    {
        if (context.WriteEvents is not null) return;
        if (Trace?.Detail is not { } detail) { context.ActivateWriteEvents(); return; }
        detail.Run("compiler.semantic.loop_write_events", (Context: context, Trace: detail), static state =>
        {
            state.Context.ActivateWriteEvents();
            state.Trace.Metric("events.initial", state.Context.WriteEvents!.Count);
        }, HirTelemetry.Source(hir.Sources, node.Source));
    }

    private void MergeLoopExits(SemanticContext context, SemanticContext body,
        IReadOnlyList<ImmutableArray<SemanticWriteEvent>> exits, HirSourceRange source, bool maySkip)
    {
        int prefix = context.WriteEvents?.Count ?? 0;
        var sequences = exits.Select(events => events.Skip(prefix).ToImmutableArray()).ToList();
        if (maySkip) sequences.Add([]);
        // Retain new lexical definitions, not one iteration's post-state of
        // existing cells. The same transfer program joins every exit at entry.
        foreach (var pair in body.Values.LocalEntries) context.Values.TryAdd(pair.Key, pair.Value);
        foreach (var pair in body.Objects.LocalEntries) context.Objects.TryAdd(pair.Key, pair.Value);
        foreach (var pair in body.Indexed.LocalEntries) context.Indexed.TryAdd(pair.Key, pair.Value);
        foreach (var pair in body.Storage.LocalEntries) context.Storage.TryAdd(pair.Key, pair.Value);
        foreach (var pair in body.Definitions.LocalEntries) context.Definitions[pair.Key] = pair.Value;
        foreach (var pair in body.AliasViews.LocalEntries)
            context.AliasViews[pair.Key] = context.AliasViews.GetValueOrDefault(pair.Key, []).Union(pair.Value);
        foreach (var pair in body.MemberContracts.LocalEntries)
            context.MemberContracts[pair.Key] = context.MemberContracts.GetValueOrDefault(pair.Key, [])
                .AddRange(pair.Value).Distinct().ToImmutableArray();
        MergeSessionContracts(context, [body]);
        ApplyEventPlan(MergeEventSequences(sequences).ToImmutableArray(), context, source);
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
        values.Add(new(value, context.WriteEvents?.ToImmutableArray() ?? []));
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
        context.Returns.Add(new(value, context.WriteEvents?.ToImmutableArray() ?? []));
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
        value with { Type = hir.Type(hir.Types[value.Type.Value] with { RuntimeType = type, Nullable = type.Nullable }) };

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
            if (actual == TypeRef.Type && value.TypeCandidates is { } domain)
            {
                var remaining = domain.Types.Where(candidate =>
                    expected.Accepts(hir.Types[candidate.Value].RuntimeType) == positive).ToArray();
                if (remaining.Length != 0)
                {
                    NarrowLayoutChoice(expression, remaining, context);
                    SetSubject(expression, WithDenotations(value, remaining), context);
                    return;
                }
            }
            bool exact = expected.Kind is not (TypeKind.Range or TypeKind.Never)
                && ((ushort)expected.Kind & ((ushort)expected.Kind - 1)) == 0;
            if (positive && refined.Kind == TypeKind.Type && exact)
                value = WithDenotations(value, [target]);
        }
        SetSubject(expression, Retype(value, refined), context);
    }

    private SemanticValue WithDenotations(SemanticValue value, IReadOnlyList<HirTypeId> candidates) =>
        candidates.Count == 1
            ? value with { DenotedType = candidates[0], TypeCandidates = null,
                Literal = new(HirLiteralKind.Type, Type: hir.Types[candidates[0].Value].RuntimeType) }
            : value with { DenotedType = null, TypeCandidates = new(candidates), Literal = null };

    private ImmutableArray<HirTypeId> MatchingDenotations(FlowDenotationDomain domain,
        HirPattern pattern, SemanticContext context)
    {
        if (pattern.Kind == HirPatternKind.Any) return domain.Types;
        if (pattern.Kind != HirPatternKind.Type) return [];
        var expected = hir.Types[ResolveHirType(new(pattern.Data), context).Value].RuntimeType;
        return [.. domain.Types.Where(type => expected.Accepts(hir.Types[type.Value].RuntimeType))];
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
        return TryScopeField(scope, node.Text!, context, out var member) ? member : null;
    }

    private void SetSubject(HirNodeId expression, SemanticValue value, SemanticContext context)
    {
        HirNode node = program.Node(expression);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
        {
            context.Values[context.Location(new(node.Data))] = value;
            return;
        }
        if (node.Kind != HirKind.MemberRead) return;
        HirNodeId receiver = program.Children(expression)[0];
        if (Subject(receiver, context) is not { Scope: { } scope } subject) return;
        if (scope.Region < 0)
        {
            SetSubject(receiver, subject with { Scope = scope with { Fields = scope.Fields.SetItem(node.Text!, value) } }, context);
            return;
        }
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
        if (Runtime(value) == TypeRef.Type && value.TypeCandidates is { } domain)
        {
            var coveredTypes = match.Arms.SelectMany(arm => MatchingDenotations(domain, arm.Pattern, context)).ToHashSet();
            var missingTypes = domain.Types.Where(type => !coveredTypes.Contains(type)).ToArray();
            if (missingTypes.Length == 0) return;
            var missingNames = string.Join(", ", missingTypes.Select(type => hir.Types[type.Value].RuntimeType));
            throw Error(source, $"Non-exhaustive match for Type; missing {missingNames} or a final '_'.", "E0209");
        }
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
            foreach (var pair in branch.Definitions.LocalEntries) destination.Definitions[pair.Key] = pair.Value;
            destination.CellWrites.UnionWith(branch.CellWrites);
            destination.IndexedWrites.UnionWith(branch.IndexedWrites);
            destination.FieldWrites.UnionWith(branch.FieldWrites);
            destination.ReplacedProgramCells = destination.ReplacedProgramCells.Union(branch.ReplacedProgramCells);
        }
        MergeSingletonFlow(destination, branches);
        MergeIndexedContexts(destination, branches);
        MergeFlowFacts(destination, branches);
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

    private void MergeFlowFacts(SemanticContext destination, SemanticContext[] branches)
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
                if (merged is not null)
                {
                    if (merged.BranchValue is null && Runtime(merged) == TypeRef.Scope
                        && destination.Values.GetValueOrDefault(location)?.BranchValue is { Selector: not null } original
                        && branches.All(branch => !branch.CellWrites.Contains(location)))
                        merged = merged with { BranchValue = original };
                    destination.Values[location] = merged;
                }
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
