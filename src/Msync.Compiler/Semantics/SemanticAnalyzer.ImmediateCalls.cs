using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<HirTemplateId, bool> immediateTemplateResults = [];

    // This is a result-policy proof, not a second invocation analysis. The
    // ordinary analyzer still evaluates the callee, operands and body in order.
    private bool HasImmediateResult(HirNode call, HirNodeId callee, SemanticContext context)
    {
        if (hir.Calls[call.Data].NativeName is { } direct) return ImmediateNativeResult(direct);
        HirNode node = program.Node(callee);
        if (node.Kind == HirKind.NativeFunction) return ImmediateNativeResult(node.Text!);
        if (node.Kind == HirKind.TemplateClosure) return ImmediateTemplateResult(hir.TemplateClosures[node.Data].Template);
        if (ImmediateCalleeValue(callee, context) is not { } value) return false;
        if (Runtime(value).WithoutNull == TypeRef.Path) return true;
        if (value.Callables.IsDefaultOrEmpty) return false;
        foreach (var target in value.Callables)
            if (!ImmediateTarget(target.NativeName, target.Template)) return false;
        return true;
    }

    private SemanticValue? ImmediateCalleeValue(HirNodeId id, SemanticContext context)
    {
        HirNode node = program.Node(id);
        HirLocationId? location;
        SemanticValue? value;
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
        {
            HirLocationId original = new(node.Data);
            location = context.Location(original);
            value = context.Values.GetValueOrDefault(location.Value);
            if (StandardDeclaration(original) is { } imported)
            {
                if (entryState?.ActivatedModules.Contains(imported.Location) != true) return null;
                if (imported.Kind == DeclarationKind.StandardModule)
                {
                    var module = context.ReplacedProgramCells.Contains(imported.Location)
                        ? context.Values.GetValueOrDefault(imported.Location) : null;
                    module ??= entryState.Values.GetValueOrDefault(imported.Location);
                    if (module?.Scope is not { } row
                        || context.Objects.GetValueOrDefault(row.Region, row).Layout != row.Layout) return null;
                    value = module;
                }
            }
        }
        else if (node.Kind == HirKind.MemberRead && node.Text is { } field
            && ImmediateCalleeValue(program.Children(id)[0], context)?.Scope is { } scope)
        {
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            value = scope.Fields.GetValueOrDefault(field);
            location = scope.Locations.TryGetValue(field, out HirLocationId member) ? member : null;
            if (location is { } live && context.Values.TryGetValue(live, out var currentMember))
                value = currentMember;
        }
        else return null;

        if (value is null || location is not { } slot
            || context.FlowAnalysis is not { } flow || !flow.Inputs.TryGetValue(id, out var facts)
            || !facts.Reachable || !facts.Contains(slot)) return value;
        FlowFact current = facts.Read(slot);
        if (!current.Assigned || current.Type.Kind == TypeKind.Never) return value;
        // The full semantic read may refine the selected Scope or callable.
        // Use a direct proof only when it sees the same domain.
        if ((current.SemanticType ?? hir.Type(current.Type)) != value.Type
            || current.Callables is { } callable && !SameFlowCallables(value.Callables, callable)) return null;
        return value;
    }

    private bool ImmediateTarget(string? native, HirTemplateId? template) =>
        native is not null ? ImmediateNativeResult(native) : template is { } source && ImmediateTemplateResult(source);

    private static bool ImmediateNativeResult(string name) =>
        NativeSignatures.TryGet(name, out var signature)
        && ((signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0
            || NonRangeNativeResult(signature));

    private static bool NonRangeNativeResult(NativeSignature signature) =>
        signature.ResultRule == NativeResultRule.Fixed && (signature.Result.Kind & TypeKind.Range) == 0;

    private bool ImmediateTemplateResult(HirTemplateId id)
    {
        if (immediateTemplateResults.TryGetValue(id, out bool result)) return result;
        var template = hir.Templates[id.Value];
        if (template.ResultAnnotation is { } resultType && NonRangeType(resultType))
            return immediateTemplateResults[id] = true;
        bool nonRangeReturns = true;
        bool nonRangeResult = NonRangeResult(template.Body, [], [], ref nonRangeReturns);
        return immediateTemplateResults[id] = nonRangeResult && nonRangeReturns;
    }

    private bool NonRangeResult(HirNodeId id, HashSet<HirLocationId> nonRangeLocals,
        HashSet<int> rangeBreaks, ref bool nonRangeReturns)
    {
        HirNode node = program.Node(id);
        var children = program.Children(id);
        if (node.Kind is HirKind.TemplateClosure or HirKind.Closure or HirKind.TypeOf) return true;

        bool last = true, arms = true;
        for (int index = 0; index < children.Length; index++)
        {
            last = NonRangeResult(children[index], nonRangeLocals, rangeBreaks, ref nonRangeReturns);
            if (node.Kind == HirKind.If && index != 0) arms &= last;
        }
        if (node.Kind == HirKind.Match)
            foreach (var arm in hir.Matches[node.Data].Arms)
                arms &= NonRangeResult(arm.Value, nonRangeLocals, rangeBreaks, ref nonRangeReturns);

        HirLocationId location = new(node.Data);
        if (node.Kind == HirKind.Declare && last && !hir.Locations[location.Value].AdmitsNull
            && program.TryDeclaration(location, out var declaration) && declaration.Annotation is null)
        {
            // A successfully initialized non-nullable local cannot later change
            // its inferred carrier, even if the binding itself is written.
            nonRangeLocals.Add(location);
        }
        if (node.Kind == HirKind.Return) nonRangeReturns &= last;
        if (node.Kind == HirKind.Break && !last) rangeBreaks.Add(node.Data);

        return node.Kind switch
        {
            HirKind.Block or HirKind.Barrier => last,
            HirKind.If or HirKind.Match => arms,
            HirKind.Loop => !rangeBreaks.Contains(node.Data),
            HirKind.ReadLocation or HirKind.ReadStorage => nonRangeLocals.Contains(location)
                || program.TryDeclaration(location, out var binding)
                    && binding.Annotation is { } declared && NonRangeType(declared),
            HirKind.Call => hir.Calls[node.Data].NativeName is { } name
                && NativeSignatures.TryGet(name, out var native) && NonRangeNativeResult(native),
            HirKind.Convert or HirKind.Coerce => NonRangeType(new(node.Data)),
            HirKind.Literal or HirKind.TypeValue or HirKind.NativeFunction
                or HirKind.Builtin or HirKind.Declare or HirKind.Assign or HirKind.MemberAssign
                or HirKind.Discard or HirKind.For or HirKind.Break or HirKind.Return or HirKind.Scope
                or HirKind.AdaptFunction or HirKind.Unary
                or HirKind.Binary or HirKind.IsNull or HirKind.IsType or HirKind.RegexMatch
                or HirKind.Qualify or HirKind.QualifyOn or HirKind.PathJoin or HirKind.Transfer => true,
            _ => false,
        };
    }

    private bool NonRangeType(HirTypeId type) => hir.Types[type.Value] is { TypeParameter: < 0 } target
        && (target.RuntimeType.Kind & TypeKind.Range) == 0;
}
