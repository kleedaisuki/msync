using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue Call(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context, bool allowDeferral = true)
    {
        ReturnShape? shape = allowDeferral ? CallShape(node, children, context) : null;
        if (shape is { Fact.Type.Kind: TypeKind.Range, Eager: false })
            return DeferredCall(node, children, context);
        var call = hir.Calls[node.Data];
        if (call.Kind == HirCallKind.Native)
        {
            SemanticValue nativeCallee = Analyze(children[0], context);
            SemanticValue[] arguments = new SemanticValue[children.Length - 1];
            for (int index = 0; index < arguments.Length; index++) arguments[index] = Analyze(children[index + 1], context);
            return InvokeNative(call.NativeName!, arguments, node.Source, context, nativeCallee);
        }
        SemanticValue callee = Analyze(children[0], context);
        SemanticValue[] inputs = new SemanticValue[children.Length - 1];
        for (int index = 0; index < inputs.Length; index++) inputs[index] = Analyze(children[index + 1], context);
        HirTypeId? resultDomain = shape is { } projected && !projected.Eager && projected.Fact.Type != TypeRef.Any
            && (projected.Fact.Type.Kind & TypeKind.Range) != 0
                ? projected.Fact.SemanticType : null;
        return Invoke(callee, inputs, node.Source, context, resultDomain);
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
        var choices = Choices(callee).ToArray();
        if (choices.Length == 0) throw Error(source, $"{Runtime(callee)} has no resolved callable identity.", "E0204");
        if (choices.Length == 1 && choices[0].NativeName is { } native)
            return InvokeNative(native, arguments, source, context, callee);

        SemanticValue? result = null;
        List<HirDispatchTarget> dispatch = [];
        ImmutableArray<SemanticDeferredWrite>.Builder deferredEffects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        foreach (var choice in choices)
        {
            if (choice.Template is not { } template) throw Error(source, "A mixed native/source call domain needs a resolved dispatch signature.", "E0204");
            var instance = Instantiate(template, choice.Captures, arguments, source, context);
            result = result is null ? instance.Result : Join(result, instance.Result!);
            if (context.DeferredReads) deferredEffects.AddRange(instance.Effects);
            dispatch.Add(new(HirCallableId.From(template), instance.Id));
        }
        var call = choices.Length == 1
            ? new HirCall(HirCallKind.Function, dispatch[0].Target)
            : new HirCall(HirCallKind.Dynamic, new(-1), Targets: dispatch.ToImmutableArray());
        if (resultDomain is { } domain) result = result! with { Type = domain };
        return result! with { Node = EmitCall(call, callee, arguments, result!.Type, source, context), Location = null,
            DeferredEffects = result.DeferredEffects.IsDefaultOrEmpty ? deferredEffects.ToImmutable()
                : result.DeferredEffects.AddRange(deferredEffects) };
    }

    private HirNodeId EmitCall(HirCall call, SemanticValue? callee, IReadOnlyList<SemanticValue> arguments,
        HirTypeId result, HirSourceRange source, SemanticContext context, HirEffects effects = HirEffects.None)
    {
        if (!context.Publish) return HirNodeId.None;
        int data = hir.Calls.Count; context.Output!.AddCall(call);
        HirNodeId[] children = new HirNodeId[arguments.Count + (callee is null ? 0 : 1)];
        int offset = 0;
        if (children.Length != arguments.Count) children[offset++] = callee!.Node;
        for (int index = 0; index < arguments.Count; index++) children[offset + index] = arguments[index].Node;
        return context.Output!.Add(HirKind.Call, result, source, children, data, effects: effects);
    }
}
