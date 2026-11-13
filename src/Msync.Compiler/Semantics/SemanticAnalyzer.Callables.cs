using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Call(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context, bool allowDeferral = true)
    {
        if (TryMetaCall(node, children, context, out SemanticValue metadata)) return metadata;
        ReturnShape? shape = null;
        if (allowDeferral && !HasImmediateResult(node, children[0], context))
        {
            var locations = CallLocations(children);
            shape = CallShape(node, children, context, locations.Read);
            if (shape is { Fact.Type.Kind: TypeKind.Range, Eager: false })
                return DeferredCall(node, children, context, locations);
        }
        SemanticValue callee = Analyze(children[0], context);
        if (TryMetaCall(callee, node, children, context, out SemanticValue computedMetadata)) return computedMetadata;
        SemanticValue[] inputs = new SemanticValue[children.Length - 1];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = Analyze(children[index + 1], context);
        if (hir.Calls[node.Data] is { Kind: HirCallKind.Native } native)
            return InvokeNative(native.NativeName!, inputs, node.Source, context, callee);
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
        List<(string Name, HirNodeId Call)> nativeCalls = [];
        HirNodeId declaration = HirNodeId.None;
        SemanticValue selectedCallee = choices.Any(choice => choice.NativeName is not null)
            ? SaveCallee(callee, source, context, out declaration) : callee;
        HirEffects nativeEffects = HirEffects.None;
        ImmutableArray<SemanticDeferredWrite>.Builder deferredEffects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        List<SemanticContext>? alternatives = choices.Length == 1 ? null : [];
        foreach (var choice in choices)
        {
            SemanticContext selected = alternatives is null ? context : CallAlternative(context);
            SemanticValue output;
            if (choice.Template is { } template)
            {
                var instance = Instantiate(template, choice.Captures, arguments, source, selected);
                output = instance.Result;
                if (context.DeferredReads && alternatives is null) deferredEffects.AddRange(instance.Effects);
                dispatch.Add(new(choice.RuntimeIdentity ?? HirCallableId.From(template), instance.Id));
            }
            else if (choice.NativeName is { } name)
            {
                output = InvokeNative(name, arguments, source, selected, selectedCallee with { Callables = [choice] });
                nativeCalls.Add((name, output.Node));
                nativeEffects |= NativeEffects(NativeSignatures.Get(name));
            }
            else throw Error(source, "The selected Function has no resolved callable identity.", "E0204");
            alternatives?.Add(selected);
            result = result is null ? output : Join(result, output);
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
        else if (context.Publish && call.Kind == HirCallKind.Function
            && choices[0].Captures.IsDefaultOrEmpty && !callee.Node.IsNone
            && IsInertCalleeProjection(callee.Node))
        {
            // A closed symbol has no environment to fetch. Keep the original
            // projection for computed, captured and session-open callees.
            int symbol = context.Output!.AddClosure(new(call.Function, []));
            callee = callee with
            { Node = context.Output.Add(HirKind.Closure, callee.Type, source, data: symbol) };
        }
        SemanticValue emitted = result! with
        {
            Node = EmitMixedCall(call, nativeCalls.Count == 0 ? callee : selectedCallee, arguments, result!.Type, source, context,
                declaration, nativeCalls, nativeEffects),
            Location = null,
            DeferredEffects = result.DeferredEffects.IsDefaultOrEmpty ? deferredEffects.ToImmutable()
                : result.DeferredEffects.AddRange(deferredEffects)
        };
        return closedResult is { } requiredResult ? Convert(emitted, requiredResult, source, context) : emitted;
    }

    private SemanticValue SaveCallee(SemanticValue callee, HirSourceRange source,
        SemanticContext context, out HirNodeId declaration)
    {
        declaration = HirNodeId.None;
        if (!context.Publish) return callee;
        SemanticOutput output = context.Output!;
        HirLocationId saved = output.ReserveLocation(new("<callee>", callee.Type, source,
            context.Function, HirLocationKind.Temporary, Mutable: false));
        declaration = output.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [callee.Node], saved.Value,
            effects: HirEffects.WriteLocation);
        return callee with { Node = output.Add(HirKind.ReadLocation, callee.Type, source,
            data: saved.Value, effects: HirEffects.ReadLocation) };
    }

    private HirNodeId EmitMixedCall(HirCall call, SemanticValue callee,
        IReadOnlyList<SemanticValue> arguments, HirTypeId result, HirSourceRange source,
        SemanticContext context, HirNodeId declaration,
        List<(string Name, HirNodeId Call)> nativeCalls, HirEffects effects)
    {
        HirNodeId selected = EmitCall(call, callee, arguments, result, source, context, effects);
        if (!context.Publish || nativeCalls.Count == 0) return selected;
        SemanticOutput output = context.Output!;
        foreach (var native in nativeCalls.AsEnumerable().Reverse())
        {
            HirNodeId descriptor = output.Add(HirKind.NativeFunction, hir.Type(TypeRef.Function), source, text: native.Name);
            HirNodeId matches = output.Add(HirKind.Binary, hir.Type(TypeRef.Bool), source,
                [callee.Node, descriptor], (int)HirBinaryOperator.Equal);
            selected = output.Add(HirKind.If, result, source, [matches, native.Call, selected]);
        }
        return output.Add(HirKind.Block, result, source, [declaration, selected]);
    }

    private static SemanticContext CallAlternative(SemanticContext context) => new(context.Function, context.Output, context)
    {
        DeferredReads = context.DeferredReads,
        Queries = context.Queries,
        ExpectedReturn = context.ExpectedReturn,
        FlowAnalysis = context.FlowAnalysis,
        NextRegion = context.NextRegion,
        InitializedSingletons = context.InitializedSingletons,
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
