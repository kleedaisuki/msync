using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<HirTemplateId, bool> dependentTypeCalls = [];

    private void NarrowLayoutChoice(HirNodeId member, IReadOnlyCollection<HirTypeId> denotations, SemanticContext context)
    {
        var read = program.Node(member);
        if (read.Kind != HirKind.MemberRead) return;
        var receiver = program.Children(member)[0];
        if (Subject(receiver, context) is not { BranchValue: { Selector: not null } choice } subject) return;

        var selected = ImmutableArray.CreateBuilder<SemanticValue>();
        var ordinals = ImmutableArray.CreateBuilder<int>();
        for (int i = 0; i < choice.Arms.Length; i++)
        {
            var arm = choice.Arms[i];
            if (arm.Scope is not { } scope) return;
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            if (!TryScopeField(scope, read.Text!, context, out var field)) return;
            if (scope.Locations.TryGetValue(read.Text!, out var location))
                field = context.Values.GetValueOrDefault(location, field);
            if (field.DenotationDomain is not { } domain) return;
            if (!domain.Types.Any(denotations.Contains)) continue;
            selected.Add(arm with { Scope = scope });
            ordinals.Add(choice.Ordinal(i));
        }
        if (selected.Count == 0 || selected.Count == choice.Arms.Length) return;

        Trace?.Metric("finite_call.refined_layout_alternatives", selected.Count);
        var narrowed = selected.Aggregate(Join);
        SetSubject(receiver, narrowed with
        {
            Node = subject.Node,
            Location = subject.Location,
            StorageConstraint = subject.StorageConstraint,
            BranchValue = selected.Count == 1 ? null : new(selected.ToImmutable(), choice.Selector, ordinals.ToImmutable())
        }, context);
    }

    private SemanticValue StoreLayoutChoice(HirNodeId sourceNode, SemanticValue value,
        HirSourceRange source, SemanticContext context)
    {
        if (Runtime(value) != TypeRef.Scope || value.BranchValue is not { Selector: null } choice
            || program.Node(sourceNode).Kind is not (HirKind.If or HirKind.Match)
            || !OriginalChoice(value, choice, context)) return value;

        Trace?.Metric("finite_call.retained_layout_alternatives", choice.Arms.Length);
        if (context.Output is not { } output)
            return value with { BranchValue = choice with { Selector = new HirLocationId(-1) } };

        HirTypeId number = hir.Type(TypeRef.Number), nil = hir.Type(TypeRef.Null);
        var selector = output.ReserveLocation(new("<layout-choice>", number, source,
            context.Function, HirLocationKind.Temporary, Mutable: true, AdmitsNull: false));
        var initialize = output.Add(HirKind.Declare, nil, source,
            [output.Literal(new(HirLiteralKind.Number, Number: 0), number, source)],
            data: selector.Value, effects: HirEffects.WriteLocation);
        var alternatives = choice.Arms.Select((arm, i) => i == 0 ? arm.Node : Tag(arm, i)).ToArray();
        var selected = ReplaceChoice(value.Node, alternatives, value.Type, source, output);
        return value with
        {
            BranchValue = choice with { Selector = selector },
            Node = output.Add(HirKind.Block, value.Type, source, [initialize, selected])
        };

        HirNodeId Tag(SemanticValue arm, int ordinal)
        {
            var tag = output.Literal(new(HirLiteralKind.Number, Number: ordinal), number, source);
            var assign = output.Add(HirKind.Assign, nil, source, [tag],
                data: selector.Value, effects: HirEffects.WriteLocation);
            return output.Add(HirKind.Block, arm.Type, source, [assign, arm.Node]);
        }
    }

    private bool TryFiniteCall(SemanticValue callee, SemanticValue[] inputs,
        ReadOnlySpan<HirNodeId> sourceInputs, HirSourceRange source, SemanticContext context,
        out SemanticValue result)
    {
        result = null!;
        if (inputs.Length == 0 || !inputs.Any(HasCases) || callee.Callables.Length != 1
            || callee.Callables[0].Template is not { } template) return false;
        if (!dependentTypeCalls.TryGetValue(template, out bool dependent))
        {
            dependent = program.TypeWitnesses.Values.Any(witness =>
                hir.Locations[witness.Location.Value].TemplateOwner == template);
            dependentTypeCalls[template] = dependent;
        }
        if (!dependent) return false;

        // The last argument's original control-flow edge can carry the whole
        // record (the witness and its payload), without reconstructing its type.
        int last = inputs.Length - 1;
        if (program.Node(sourceInputs[last]).Kind is HirKind.If or HirKind.Match
            && inputs[last].BranchValue is { Selector: null } choice
            && OriginalChoice(inputs[last], choice, context))
        {
            result = EmitFiniteCall(callee, inputs, last, choice, true, source, context);
            return true;
        }

        int stored = Array.FindIndex(inputs, value => value.BranchValue?.Selector is not null);
        if (stored >= 0 && !inputs.Where((_, i) => i != stored).Any(HasCases))
        {
            var choiceValue = inputs[stored].BranchValue!;
            result = EmitFiniteCall(callee, inputs, stored, choiceValue, false, source, context);
            return true;
        }

        int index = Array.FindIndex(inputs, value =>
            Runtime(value) == TypeRef.Type && value.DenotedType is null && value.TypeCandidates is { Types.Length: > 1 });
        if (index < 0 || inputs.Where((_, i) => i != index).Any(HasUnresolvedType)) return false;
        var candidates = inputs[index].TypeCandidates!.Types;
        if (candidates.Select(candidate => hir.Types[candidate.Value].RuntimeType).Distinct().Count() != candidates.Length)
            return false;
        var alternatives = candidates.Select(candidate => inputs[index] with
        {
            DenotedType = candidate,
            TypeCandidates = null,
            BranchValue = null,
            Literal = new(HirLiteralKind.Type, Type: hir.Types[candidate.Value].RuntimeType)
        }).ToImmutableArray();
        result = EmitFiniteCall(callee, inputs, index, new(alternatives), false, source, context);
        return true;

        static bool HasCases(SemanticValue value) => HasUnresolvedType(value) || value.BranchValue is not null;
        static bool HasUnresolvedType(SemanticValue value) =>
            value.DenotedType is null && value.TypeCandidates is { Types.Length: > 1 };
    }

    private bool OriginalChoice(SemanticValue value, SemanticBranchValue choice, SemanticContext context)
    {
        if (!context.Publish) return true;
        if (value.Node.IsNone) return false;
        var select = hir.Nodes[value.Node.Value];
        var children = hir.Children(select);
        if (select.Kind == HirKind.If)
            return choice.Arms.Length == 2 && children[1] == choice.Arms[0].Node && children[2] == choice.Arms[1].Node;
        if (select.Kind != HirKind.Match) return false;
        var arms = hir.Matches[select.Data].Arms;
        return arms.Length == choice.Arms.Length &&
            arms.Select(arm => arm.Value).SequenceEqual(choice.Arms.Select(arm => arm.Node));
    }

    private HirNodeId ReplaceChoice(HirNodeId originalId, HirNodeId[] alternatives, HirTypeId type,
        HirSourceRange source, SemanticOutput output)
    {
        var original = hir.Nodes[originalId.Value];
        var condition = hir.Children(original)[0];
        if (original.Kind == HirKind.If)
            return output.Add(HirKind.If, type, source, [condition, alternatives[0], alternatives[1]]);
        int match = hir.Matches.Count;
        var patterns = hir.Matches[original.Data].Arms;
        output.AddMatch(new([.. patterns.Select((arm, i) => arm with { Value = alternatives[i] })]));
        return output.Add(HirKind.Match, type, source, [condition], data: match);
    }

    private SemanticValue EmitFiniteCall(SemanticValue callee, SemanticValue[] inputs, int index,
        SemanticBranchValue choice, bool reuse, HirSourceRange source, SemanticContext context)
    {
        var choices = choice.Arms;
        var selector = choice.Selector;
        Trace?.Metric(reuse ? "finite_call.existing_branch" : selector is not null
            ? "finite_call.retained_layout_choice" : "finite_call.encoded_choice", 1);
        Trace?.Metric("finite_call.alternatives", choices.Length);
        List<HirNodeId> prefix = [];
        SemanticOutput? output = context.Output;
        if (output is not null)
        {
            callee = Save(callee, "<finite-callee>");
            for (int i = 0; i < inputs.Length; i++)
                if (!reuse || i != index) inputs[i] = Save(inputs[i], "<finite-argument>");
        }

        var subject = inputs[index];
        List<SemanticContext> branches = [];
        var results = new SemanticValue[choices.Length];
        SemanticValue? result = null;
        for (int i = 0; i < choices.Length; i++)
        {
            var branch = CallAlternative(context);
            branches.Add(branch);
            inputs[index] = reuse ? choices[i] : choices[i] with
            {
                Node = selector is not null && output is not null
                    ? output.Add(HirKind.ReadLocation, choices[i].Type, source,
                        data: hir.Nodes[subject.Node.Value].Data, effects: HirEffects.ReadLocation)
                    : subject.Node
            };
            results[i] = Invoke(callee, inputs, source, branch);
            result = result is null ? results[i] : Join(result, results[i]);
        }
        var effects = MergeCallAlternatives(context, branches);
        if (output is null) return result! with { DeferredEffects = effects };

        HirNodeId node = results[^1].Node;
        if (reuse)
            node = ReplaceChoice(subject.Node, results.Select(item => item.Node).ToArray(), result!.Type, source, output);
        else for (int i = choices.Length - 2; i >= 0; i--)
        {
            HirNodeId selected = subject.Node, constant;
            if (selector is { } location)
            {
                HirTypeId number = hir.Type(TypeRef.Number);
                selected = output.Add(HirKind.ReadLocation, number, source, data: location.Value, effects: HirEffects.ReadLocation);
                constant = output.Literal(new(HirLiteralKind.Number, Number: choice.Ordinal(i)), number, source);
            }
            else constant = output.Add(HirKind.TypeValue, hir.Type(TypeRef.Type), source,
                data: choices[i].DenotedType!.Value.Value);
            var condition = output.Add(HirKind.Binary, hir.Type(TypeRef.Bool), source,
                [selected, constant], (int)HirBinaryOperator.Equal);
            node = output.Add(HirKind.If, result!.Type, source, [condition, results[i].Node, node]);
        }
        prefix.Add(node);
        return result! with
        {
            Node = output.Add(HirKind.Block, result.Type, source,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(prefix)),
            DeferredEffects = effects
        };

        SemanticValue Save(SemanticValue value, string name)
        {
            var location = output!.ReserveLocation(new(name, value.Type, source,
                context.Function, HirLocationKind.Temporary, Mutable: false,
                AdmitsNull: Runtime(value).Nullable));
            prefix.Add(output.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [value.Node],
                location.Value, effects: HirEffects.WriteLocation));
            return value with { Node = output.Add(HirKind.ReadLocation, value.Type, source,
                data: location.Value, effects: HirEffects.ReadLocation) };
        }
    }
}
