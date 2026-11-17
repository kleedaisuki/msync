using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Call(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        if (Trace is null) return CallCore(node, children, context);
        return Trace.MeasureDetailed("compiler.semantic.call", (self: this, node, context),
            static state => state.self.CallCore(state.node, state.self.hir.Children(state.node), state.context),
            attributes: new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, node.Source))
            { ["evaluation.order"] = "callee-then-arguments" });
    }

    private SemanticValue CallCore(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        Trace?.Metric("arguments", children.Length - 1);
        program.NamedCalls.TryGetValue(node.Data, out var labels);
        if (labels is null && TryMetaCall(node, children, context, out SemanticValue metadata)) return metadata;
        SemanticValue callee = Analyze(children[0], context);
        if (labels is null && TryMetaCall(callee, node, children, context, out SemanticValue computedMetadata)) return computedMetadata;
        SemanticValue[] inputs = new SemanticValue[children.Length - 1];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = Analyze(children[index + 1], context);
        if (context.Output is null && program.Node(children[0]) is { Kind: HirKind.ReadLocation, Data: var input }
            && program.TypeQueryInputs.Contains(new(input)))
            return QueryTypeCall(callee, inputs, node.Source, context);
        if (hir.Calls[node.Data] is { Kind: HirCallKind.Native } native)
            return InvokeNative(native.NativeName!, inputs, node.Source, context, callee);
        if (labels is not null) return InvokeNamed(callee, inputs, children[1..], labels, node.Source, context);
        if (TryFiniteCall(callee, inputs, children[1..], node.Source, context, out var finite)) return finite;
        return Invoke(callee, inputs, node.Source, context,
            program.CallResultHints.TryGetValue(node.Data, out var hint) ? hint : null);
    }

    private SemanticValue InvokeNamed(SemanticValue callee, SemanticValue[] inputs, ReadOnlySpan<HirNodeId> syntax,
        IReadOnlyDictionary<int, string> labels, HirSourceRange source, SemanticContext context)
    {
        var choices = Choices(callee).ToArray();
        Trace?.Metric("named_arguments.labels", labels.Count);
        Trace?.Metric("named_arguments.callable_alternatives", choices.Length);
        if (choices.Length == 0) throw Error(source, "Named arguments require a known function signature.", "E0204");
        var order = Enumerable.Range(0, inputs.Length).ToArray();
        HashSet<int> occupied = [];
        foreach (var (index, label) in labels)
        {
            int? target = null;
            foreach (var choice in choices)
            {
                int slot = NamedSlot(choice, label);
                if (slot < 0 || slot >= inputs.Length)
                    throw Error(source, $"No parameter named '{label}' on the selected signature.", "E0204");
                if (target is { } prior && slot != prior)
                    throw Error(source, $"Callable alternatives disagree about parameter '{label}'.", "E0204");
                target = slot;
            }
            order[index] = target!.Value;
        }
        bool reorder = false;
        for (int index = 0; index < order.Length; index++)
        {
            if (!occupied.Add(order[index]))
                throw Error(source, "The same parameter receives more than one argument.", "E0204");
            reorder |= order[index] != index;
        }
        Trace?.Metric("named_arguments.reordered", reorder ? 1 : 0);
        if (!reorder)
            return TryFiniteCall(callee, inputs, syntax, source, context, out var finite)
                ? finite : Invoke(callee, inputs, source, context);

        var output = context.Output;
        List<HirNodeId> evaluations = [];
        if (output is not null)
        {
            callee = SaveCallee(callee, source, context, out var initializer);
            evaluations.Add(initializer);
        }
        SemanticValue[] ordered = new SemanticValue[inputs.Length];
        for (int index = 0; index < inputs.Length; index++)
        {
            SemanticValue value = inputs[index];
            if (output is not null)
            {
                var location = output.ReserveLocation(new("<argument>", value.Type, source, context.Function,
                    HirLocationKind.Temporary, Mutable: false, AdmitsNull: Runtime(value).Nullable));
                evaluations.Add(output.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [value.Node],
                    location.Value, effects: HirEffects.WriteLocation));
                value = value with { Node = output.Add(HirKind.ReadLocation, value.Type, source,
                    data: location.Value, effects: HirEffects.ReadLocation) };
            }
            ordered[order[index]] = value;
        }
        var result = Invoke(callee, ordered, source, context);
        if (output is null) return result;
        evaluations.Add(result.Node);
        return result with { Node = output.Add(HirKind.Block, result.Type, source,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(evaluations)) };
    }

    private int NamedSlot(SemanticCallable choice, string label)
    {
        if (choice.Template is { } template)
        {
            var parameters = program.Templates[template.Value].Parameters;
            for (int index = 0; index < parameters.Length; index++)
                if (hir.Locations[parameters[index].Value].Name == label) return index;
        }
        if (choice.NativeName is { } native && program.NativeSignatures.TryGetValue(native, out var signature))
            return signature.ParameterNames.IndexOf(label);
        return -1;
    }

    private SemanticValue InvokeType(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context) => Invoke(callee, arguments, source, QueryContext(context));

    private SemanticValue Invoke(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context, HirTypeId? expectedResult = null)
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
                var instance = Instantiate(template, choice.Captures, arguments, source, selected, expectedResult: expectedResult);
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
        MergeFlowFacts(context, branches);
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
