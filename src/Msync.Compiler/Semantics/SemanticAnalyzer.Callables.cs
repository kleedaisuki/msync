using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Call(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context, bool allowDeferral = true)
    {
        if (TryMetaCall(node, children, context, out SemanticValue metadata)) return metadata;
        ReturnShape? shape = allowDeferral ? CallShape(node, children, context) : null;
        if (shape is { Fact.Type.Kind: TypeKind.Range, Eager: false })
            return DeferredCall(node, children, context);
        var call = hir.Calls[node.Data];
        if (call.Kind == HirCallKind.Native)
        {
            SemanticValue nativeCallee = Analyze(children[0], context);
            if (TryMetaCall(nativeCallee, node, children, context, out SemanticValue nativeMetadata)) return nativeMetadata;
            SemanticValue[] arguments = new SemanticValue[children.Length - 1];
            for (int index = 0; index < arguments.Length; index++) arguments[index] = Analyze(children[index + 1], context);
            return InvokeNative(call.NativeName!, arguments, node.Source, context, nativeCallee);
        }
        SemanticValue callee = Analyze(children[0], context);
        if (TryMetaCall(callee, node, children, context, out SemanticValue computedMetadata)) return computedMetadata;
        SemanticValue[] inputs = new SemanticValue[children.Length - 1];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = Analyze(children[index + 1], context);
        HirTypeId? resultDomain = shape is { } projected && !projected.Eager
            && projected.Fact.Type.WithoutNull != TypeRef.Any.WithoutNull
            && (projected.Fact.Type.Kind & TypeKind.Range) != 0
                ? projected.Fact.SemanticType : null;
        SemanticValue result = Invoke(callee, inputs, node.Source, context, resultDomain);
        return shape?.Eager == true ? result with { EagerRange = true } : result;
    }

    private SemanticValue InvokeType(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context) => Invoke(callee, arguments, source, QueryContext(context, callee, arguments));

    private SemanticValue Invoke(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context, HirTypeId? resultDomain = null)
    {
        if (Runtime(callee).WithoutNull == TypeRef.Path)
        {
            var processResult = ValueForType(hir.Type(TypeRef.Process));
            return processResult with { Node = EmitCall(new(HirCallKind.Path, new(-1)), callee, arguments, processResult.Type, source, context) };
        }
        HirTypeId? closedResult = CheckClosedFutureCall(callee, arguments, source, context);
        var choices = Choices(callee).ToArray();
        if (choices.Length == 0) throw Error(source, $"{Runtime(callee)} has no resolved callable identity.", "E0204");
        if (choices.Length == 1 && choices[0].NativeName is { } native)
        {
            SemanticValue nativeResult = InvokeNative(native, arguments, source, context, callee);
            return closedResult is { } required ? Convert(nativeResult, required, source, context) : nativeResult;
        }

        SemanticValue? result = null;
        List<HirDispatchTarget> dispatch = [];
        ImmutableArray<SemanticDeferredWrite>.Builder deferredEffects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        List<SemanticContext>? alternatives = choices.Length == 1 ? null : [];
        foreach (var choice in choices)
        {
            if (choice.Template is not { } template) throw Error(source, "A mixed native/source call domain needs a resolved dispatch signature.", "E0204");
            SemanticContext selected = alternatives is null ? context : CallAlternative(context);
            var instance = Instantiate(template, choice.Captures, arguments, source, selected);
            alternatives?.Add(selected);
            result = result is null ? instance.Result : Join(result, instance.Result!);
            if (context.DeferredReads && alternatives is null) deferredEffects.AddRange(instance.Effects);
            dispatch.Add(new(choice.RuntimeIdentity ?? HirCallableId.From(template), instance.Id));
        }
        if (alternatives is not null) deferredEffects.AddRange(MergeCallAlternatives(context, alternatives));
        var call = choices.Length == 1
            ? new HirCall(HirCallKind.Function, dispatch[0].Target)
            : new HirCall(HirCallKind.Dynamic, new(-1), Targets: dispatch.Distinct().ToImmutableArray());
        if (resultDomain is { } domain) result = result! with { Type = domain };
        int slot = OpenSessionCall(callee, arguments, result!.Type, source, context);
        if (slot >= 0)
        {
            call = call with { SessionSlot = slot };
            result = FutureRead(result, result.Type, context) with
            { OpenSessionCalls = AddSessionCall(result.OpenSessionCalls, SessionBinding(slot, callee, arguments, context)) };
        }
        SemanticValue emitted = result! with { Node = EmitCall(call, callee, arguments, result!.Type, source, context), Location = null,
            DeferredEffects = result.DeferredEffects.IsDefaultOrEmpty ? deferredEffects.ToImmutable()
                : result.DeferredEffects.AddRange(deferredEffects) };
        return closedResult is { } requiredResult ? Convert(emitted, requiredResult, source, context) : emitted;
    }

    private static SemanticContext CallAlternative(SemanticContext context) => new(context.Function, context.Output, context)
    {
        DeferredReads = context.DeferredReads, Queries = context.Queries,
        ExpectedReturn = context.ExpectedReturn, FlowAnalysis = context.FlowAnalysis,
        NextRegion = context.NextRegion, InitializedSingletons = context.InitializedSingletons,
        MaybeInitializedSingletons = context.MaybeInitializedSingletons,
        ActiveSingletonGuards = context.ActiveSingletonGuards,
        WriteEvents = context.WriteEvents is null ? null : new(context.WriteEvents)
    };

    private ImmutableArray<SemanticDeferredWrite> MergeCallAlternatives(SemanticContext context,
        List<SemanticContext> alternatives)
    {
        SemanticContext[] branches = alternatives.ToArray();
        MergeSessionContracts(context, branches);
        MergeSingletonFlow(context, branches);
        MergeIndexedContexts(context, branches);
        MergeQueryContexts(context, branches);
        var effects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        foreach (HirLocationId location in branches.SelectMany(branch => branch.CellWrites).Distinct())
        {
            context.CellWrites.Add(location);
            effects.Add(new(SemanticWriteKind.Location, context.Values[location], Location: location));
        }
        foreach (var (region, field) in branches.SelectMany(branch => branch.FieldWrites).Distinct())
        {
            context.FieldWrites.Add((region, field));
            effects.Add(new(SemanticWriteKind.Field, context.Objects[region].Fields[field], Region: region, Field: field));
        }
        foreach (int region in branches.SelectMany(branch => branch.IndexedWrites).Distinct())
        {
            context.IndexedWrites.Add(region);
            effects.Add(new(SemanticWriteKind.Indexed, context.Indexed[region].Element, Region: region));
        }
        foreach (SemanticContext branch in branches)
            foreach (var definition in branch.Definitions.LocalEntries) context.Definitions[definition.Key] = definition.Value;
        return context.DeferredReads ? effects.ToImmutable() : [];
    }

    private HirNodeId EmitCall(HirCall call, SemanticValue? callee, IReadOnlyList<SemanticValue> arguments,
        HirTypeId result, HirSourceRange source, SemanticContext context, HirEffects effects = HirEffects.None)
    {
        if (!context.Publish) return HirNodeId.None;
        if (call.SessionSlot >= 0)
            effects |= HirEffects.Invoke | HirEffects.ReadMemory | HirEffects.WriteMemory | HirEffects.MayFail;
        int data = hir.Calls.Count; context.Output!.AddCall(call);
        HirNodeId[] children = new HirNodeId[arguments.Count + (callee is null ? 0 : 1)];
        int offset = 0;
        if (children.Length != arguments.Count) children[offset++] = callee!.Node;
        for (int index = 0; index < arguments.Count; index++) children[offset + index] = arguments[index].Node;
        return context.Output!.Add(HirKind.Call, result, source, children, data, effects: effects);
    }
}
