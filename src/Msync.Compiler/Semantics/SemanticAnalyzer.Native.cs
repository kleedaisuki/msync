using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue InvokeNative(string name, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context, SemanticValue? callee,
        Action<IReadOnlyList<SemanticValue>>? selectArguments = null)
    {
        if (!NativeSignatures.TryGet(name, out var signature))
            throw Error(source, $"Unknown native '{name}'.", "E0204");
        if (IsDependentMeta(name))
            throw Error(source, "Dependent metadata queries require a direct, statically known invocation.", "E0222");
        var inputs = CheckNativeArguments(signature, arguments, source, context).ToArray();
        selectArguments?.Invoke(inputs);
        if (name is "text.format" or "text.format_fields") CheckFormatCall(name, inputs, source, context);
        SemanticValue? callback = null;
        HirFunctionId? callbackTarget = null;
        ImmutableArray<SemanticDeferredWrite> callbackEffects = [];
        if (signature.Parameters.Count > 0 && inputs.Length >= signature.Parameters.Count
            && signature.Parameters[^1].WithoutNull == TypeRef.Function)
        {
            int index = signature.Parameters.Count - 1;
            var item = Item(inputs[0]);
            bool merge = signature.ResultRule is NativeResultRule.RangeMerge or NativeResultRule.RangeMergeLeft;
            if (merge) item = Join(item, Item(inputs[1]));
            IReadOnlyList<SemanticValue> callbackArguments = name == "ranges.sort_with" || merge ? [item, item]
                : signature.ResultRule == NativeResultRule.RangeScan ? [inputs[1], item] : [item];
            if (signature.ResultRule == NativeResultRule.RangeScan)
            {
                var domain = inputs[1];
                HirTypeId? carrier = Runtime(domain).WithoutNull.Kind == TypeKind.Never ? null : domain.Type;
                while (true)
                {
                    var result = InvokeType(inputs[index], [domain, item], source, context);
                    if (carrier is null && Runtime(result).WithoutNull.Kind != TypeKind.Never)
                        carrier = Runtime(inputs[1]).Nullable ? Nullable(result.Type) : result.Type;
                    if (carrier is { } required) Require(result, required, source, context: context);
                    var next = Join(domain, result);
                    if (TypeSchemaKey(next) == TypeSchemaKey(domain)) break;
                    domain = next;
                }
                callbackArguments = [domain, item];
            }
            (callback, inputs[index], callbackEffects, callbackTarget) = NativeCallback(inputs[index], callbackArguments, source, context);
            if (merge || name is "ranges.filter" or "ranges.take_while" or "ranges.drop_while" or "ranges.any" or "ranges.any_of"
                or "ranges.all_of" or "ranges.none" or "ranges.none_of" or "ranges.find_if" or "ranges.sort_with")
                Require(callback, hir.Type(TypeRef.Bool), source, context: context);
        }
        var value = NativeResult(signature, inputs, callback, source, context);
        if (name.StartsWith("ranges.", StringComparison.Ordinal))
            value = value with { RangeRecipe = NativeRangeRecipe(name, inputs, callbackTarget) };
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        ImmutableArray<SessionCallBinding> openCalls = [];
        foreach (var input in inputs) openCalls = CombineSessionCalls(openCalls, input.OpenSessionCalls);
        ImmutableArray<SemanticDeferredWrite> pending = callbackEffects;
        for (int index = 0; index < signature.Parameters.Count && index < inputs.Length; index++)
            if (signature.Parameters[index].WithoutNull.Kind == TypeKind.Range)
                pending = CombineDeferredEffects(pending, inputs[index].DeferredEffects);
        if (signature.ResultRule == NativeResultRule.RangeFlatten && inputs[0].Element is { } nested)
        {
            pending = pending.AddRange(nested.DeferredEffects.IsDefault ? [] : nested.DeferredEffects);
            openCalls = CombineSessionCalls(openCalls, nested.OpenSessionCalls);
        }
        if ((behavior & NativeBehavior.Deferred) != 0)
            value = value with
            {
                DeferredEffects = pending,
                EagerRange = (behavior & NativeBehavior.PreservesIdentity) != 0 && value.EagerRange,
                OpenSessionCalls = CombineSessionCalls(value.OpenSessionCalls, openCalls)
            };
        else if ((behavior & (NativeBehavior.Enumerates | NativeBehavior.Invokes)) != 0)
        {
            ApplySessionCallEffects(openCalls, context);
            ApplyDeferredEffects(pending, context);
            bool returnsPendingRange = signature.ResultRule == NativeResultRule.RangeItem
                && (Runtime(value).Kind & TypeKind.Range) != 0;
            if (!returnsPendingRange) value = value with { DeferredEffects = [], OpenSessionCalls = [] };
        }
        if (value.Literal is { } literal && signature.Name.StartsWith("meta.", StringComparison.Ordinal))
            return value with { Node = context.Publish ? context.Output!.Literal(literal, value.Type, source) : HirNodeId.None };
        if (name == "meta.alternatives" && inputs[0].DenotedType is { } denoted)
            return StaticAlternatives(denoted, callee, inputs[0], source, context);
        callee ??= new(hir.Type(TypeRef.Function), context.Publish
            ? context.Output!.Add(HirKind.NativeFunction, hir.Type(TypeRef.Function), source, text: name) : HirNodeId.None,
            Callables: [new(NativeName: name)]);
        int slot = OpenSessionCall(callee, inputs, value.Type, source, context);
        if (context.Publish && slot < 0 && !callee.Node.IsNone
            && hir.Nodes[callee.Node.Value].Kind != HirKind.NativeFunction && IsInertCalleeProjection(callee.Node))
            callee = callee with { Node = context.Output!.Add(HirKind.NativeFunction, callee.Type, source, text: name) };
        var emittedCall = new HirCall(HirCallKind.Native, new(-1), name) { SessionSlot = slot };
        return value with
        {
            Node = EmitCall(emittedCall, callee, inputs, value.Type, source, context, NativeEffects(signature)),
            Location = null,
            OpenSessionCalls = slot >= 0 ? AddSessionCall(value.OpenSessionCalls, SessionBinding(slot, callee, inputs, context)) : value.OpenSessionCalls
        };
    }

    private bool IsInertCalleeProjection(HirNodeId id)
    {
        while (true)
        {
            var node = hir.Nodes[id.Value];
            if ((node.Effects & ~(HirEffects.ReadLocation | HirEffects.ReadMemory)) != 0) return false;
            if (node.Kind is HirKind.ReadLocation or HirKind.NativeFunction) return true;
            if (node is not { Kind: HirKind.MemberRead, Data: >= 0 }) return false;
            id = hir.Children(node)[0];
            // Only semantic Scope projections have a known-present field and no accessor.
            if (hir.Types[hir.Nodes[id.Value].Type.Value].RuntimeType != TypeRef.Scope) return false;
        }
    }

    private static HirEffects NativeEffects(NativeSignature signature)
    {
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        bool memory = (behavior & NativeBehavior.LocalMemory) != 0;
        HirEffects read = memory ? HirEffects.ReadMemory : HirEffects.ReadExternal;
        HirEffects write = memory ? HirEffects.WriteMemory : HirEffects.WriteExternal;
        HirEffects effects = signature.Effect switch
        {
            NativeEffect.Read => read | HirEffects.MayFail,
            NativeEffect.Write => read | write | HirEffects.MayFail,
            _ => HirEffects.None
        };
        if ((behavior & NativeBehavior.MayFail) != 0) effects |= HirEffects.MayFail;
        if ((behavior & NativeBehavior.Enumerates) != 0) effects |= HirEffects.Enumerate | HirEffects.Invoke | HirEffects.ReadMemory | HirEffects.MayFail;
        if ((behavior & NativeBehavior.Invokes) != 0) effects |= HirEffects.Invoke | HirEffects.MayFail;
        return effects;
    }

    private (SemanticValue Result, SemanticValue Function, ImmutableArray<SemanticDeferredWrite> Effects, HirFunctionId? Target) NativeCallback(SemanticValue callable,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext context)
    {
        SemanticValue? output = null;
        ImmutableArray<HirDispatchTarget>.Builder targets = ImmutableArray.CreateBuilder<HirDispatchTarget>();
        ImmutableArray<SemanticDeferredWrite>.Builder effects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        foreach (var choice in Choices(callable))
        {
            SemanticValue result;
            if (choice.Template is { } template)
            {
                var instance = Instantiate(template, choice.Captures, arguments, source, context, applyEffects: false);
                result = instance.Result!;
                effects.AddRange(instance.Effects);
                targets.Add(new(choice.RuntimeIdentity ?? HirCallableId.From(template), instance.Id));
            }
            else if (choice.NativeName is { } native)
                result = InvokeType(new(hir.Type(TypeRef.Function), HirNodeId.None, Callables: [choice]), arguments, source, context);
            else throw Error(source, "A native callback has no semantic callable identity.", "E0204");
            output = output is null ? result : Join(output, result);
        }
        if (output is null) throw Error(source, "The callback is not a resolved Function value.", "E0204");
        if (context.Publish && targets.Count != 0)
        {
            int slot = OpenSessionCall(callable, arguments, output.Type, source, context);
            int adaptation = hir.Adaptations.Count;
            context.Output!.AddAdaptation(new(targets.ToImmutable()) { SessionSlot = slot });
            callable = callable with
            {
                Node = context.Output!.Add(HirKind.AdaptFunction, callable.Type, source, [callable.Node], adaptation),
                OpenSessionCalls = slot >= 0 ? AddSessionCall(callable.OpenSessionCalls, SessionBinding(slot, callable, arguments, context)) : callable.OpenSessionCalls
            };
        }
        return (output, callable, effects.ToImmutable(), targets.Count == 1 ? targets[0].Target : null);
    }

    private static ImmutableArray<SemanticDeferredWrite> CombineDeferredEffects(
        ImmutableArray<SemanticDeferredWrite> first, ImmutableArray<SemanticDeferredWrite> second)
    {
        if (first.IsDefaultOrEmpty) return second.IsDefault ? [] : second;
        return second.IsDefaultOrEmpty ? first : first.AddRange(second);
    }

    private void ApplyDeferredEffects(SemanticValue range, SemanticContext context)
    {
        ApplySessionCallEffects(range.OpenSessionCalls, context);
        ApplyDeferredEffects(range.DeferredEffects, context);
    }

    private void ApplyDeferredEffects(ImmutableArray<SemanticDeferredWrite> writes, SemanticContext context)
    {
        if (writes.IsDefaultOrEmpty) return;
        foreach (var write in writes)
        {
            if (write.Kind == SemanticWriteKind.Location)
            {
                if (context.Values.TryGetValue(write.Location, out var before))
                    context.Values[write.Location] = Join(before, write.After) with { Literal = null };
                context.CellWrites.Add(write.Location);
                if (context.Values.TryGetValue(write.Location, out var cellAfter))
                    context.RecordWrite(new(SemanticWriteKind.Location, cellAfter, Location: write.Location));
                continue;
            }
            if (write.Kind == SemanticWriteKind.Indexed)
            {
                if (context.Indexed.TryGetValue(write.Region, out var storage))
                {
                    var written = write.IndexedStorage ?? new(write.After,
                        Runtime(write.After).Kind == TypeKind.Never ? null : write.After.Type);
                    var joined = JoinIndexed(storage, written);
                    context.Indexed[write.Region] = joined with { Element = joined.Element with { Literal = null } };
                }
                context.IndexedWrites.Add(write.Region);
                if (context.Indexed.TryGetValue(write.Region, out var afterStorage))
                    context.RecordWrite(new(SemanticWriteKind.Indexed, afterStorage.Element, Region: write.Region,
                        IndexedStorage: afterStorage));
                continue;
            }
            if (!context.Objects.TryGetValue(write.Region, out var target)
                || !target.Fields.TryGetValue(write.Field!, out var previous)) continue;
            InvalidateScopeAliases(target, write.Field!, write.After, context);
            var after = Join(previous, write.After) with { Literal = null };
            target = context.Objects[write.Region] with { Fields = context.Objects[write.Region].Fields.SetItem(write.Field!, after) };
            context.Objects[write.Region] = target;
            if (target.Locations.TryGetValue(write.Field!, out var location)) context.Values[location] = after;
            context.FieldWrites.Add((write.Region, write.Field!));
            context.RecordWrite(new(SemanticWriteKind.Field, after, Region: write.Region, Field: write.Field));
        }
    }

    private string TypeSchemaKey(SemanticValue value) =>
        TypeSchemaKey(value, new HashSet<SemanticValue>(ReferenceEqualityComparer.Instance));

    private string TypeSchemaKey(SemanticValue value, HashSet<SemanticValue> visited)
    {
        if (!visited.Add(value)) return "<recursive>";
        var domain = hir.Types[value.Type.Value].ScopeDomain;
        string layouts = domain.Count == 0 ? "" : "@(" + string.Join(',', Enumerable.Range(0, domain.Count).Select(index => domain[index])) + ")";
        var element = value.Element is null ? "" : "/" + TypeSchemaKey(value.Element, visited);
        var fields = value.Scope is null ? "" : "{" + string.Join(';', value.Scope.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + TypeSchemaKey(pair.Value, visited))) + "}";
        var functions = "[" + string.Join(',', Choices(value).Select(choice =>
            (choice.NativeName ?? "t" + choice.Template?.Value) + "(" + string.Join(',', choice.Captures.IsDefault
                ? [] : choice.Captures.Select(capture => TypeSchemaKey(capture.Value, visited))) + ")")) + "]";
        return Runtime(value) + layouts + fields + element + functions;
    }

    private SemanticValue NativeResult(NativeSignature signature, IReadOnlyList<SemanticValue> arguments,
        SemanticValue? callbackResult, HirSourceRange source, SemanticContext context)
    {
        if (signature.Name.StartsWith("meta.", StringComparison.Ordinal)
            && arguments.Count > 0 && arguments.All(argument => argument.DenotedType is not null))
            return StaticMetaResult(signature.Name[5..], arguments);
        var result = signature.ResultRule switch
        {
            NativeResultRule.Fixed => FixedNativeResult(signature),
            NativeResultRule.FirstArgument => FirstNativeArgument(signature, arguments[0]),
            NativeResultRule.RangeOfArguments => RangeValue(arguments.Count == 0
                ? ValueForType(hir.Type(new TypeRef(TypeKind.Never))) : arguments.Aggregate(Join)),
            NativeResultRule.RangeItem => Item(arguments[0]) with { Node = HirNodeId.None, Location = null },
            NativeResultRule.Transform => RangeValue(callbackResult ?? ValueForType(hir.Type(TypeRef.Any))),
            NativeResultRule.CastTarget => NativeCastResult(arguments, source, context),
            NativeResultRule.RangeConcat or NativeResultRule.RangeMerge => RangeValue(Join(Item(arguments[0]), Item(arguments[1]))),
            NativeResultRule.RangeMergeLeft => FirstNativeArgument(signature, arguments[0]),
            NativeResultRule.RangeFlatten => Flatten(arguments[0], source),
            NativeResultRule.RangeZip => RangeValue(Record(("first", Item(arguments[0])), ("second", Item(arguments[1])))),
            NativeResultRule.RangeEnumerate => RangeValue(Record(("index", ValueForType(hir.Type(TypeRef.Number))), ("value", Item(arguments[0])))),
            NativeResultRule.RangeNested => RangeValue(arguments[0] with { Node = HirNodeId.None, Location = null }),
            NativeResultRule.RangeScan => RangeValue(callbackResult is null ? arguments[1] : Join(arguments[1], callbackResult)),
            NativeResultRule.RangeRepeat => RangeValue(arguments[0] with { Node = HirNodeId.None, Location = null }),
            NativeResultRule.RangeOfType => arguments[0].DenotedType is { } element
                ? RangeValue(ValueForType(element))
                : throw Error(source, "A typed empty Range requires a statically inferred element Type.", "E0220"),
            _ => throw Error(source, $"Unknown native result rule '{signature.ResultRule}'.")
        };
        if (signature.Name == "ranges.sort")
        {
            TypeRef item = Runtime(Item(arguments[0]));
            if (!HasNaturalRangeOrder(item))
                throw Error(source, $"Natural sorting requires Bool, Number or Path/String items, found {item}. Use sort_with for a custom order.", "E0221");
        }
        if (signature.ResultRule is NativeResultRule.RangeMerge or NativeResultRule.RangeMergeLeft && arguments.Count == 2)
            RequireNaturalRangeOrder(TypeRef.Union(Runtime(Item(arguments[0])), Runtime(Item(arguments[1]))), source);
        if (signature.Name == "ranges.less")
            RequireNaturalRangeOrder(TypeRef.Union(Runtime(arguments[0]), Runtime(arguments[1])), source);
        bool eager = (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0;
        return result with { EagerRange = eager || result.EagerRange };
    }

    private void RequireNaturalRangeOrder(TypeRef item, HirSourceRange source)
    {
        if (!HasNaturalRangeOrder(item))
            throw Error(source, $"Natural range ordering requires Bool, Number or Path/String items, found {item}. Supply a strict order with the _with variant.", "E0221");
    }

    private static bool HasNaturalRangeOrder(TypeRef type) =>
        type == TypeRef.Bool || type == TypeRef.Number || new TypeRef(TypeKind.Path | TypeKind.String).Accepts(type);

    private static SemanticValue FirstNativeArgument(NativeSignature signature, SemanticValue value)
    {
        if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.PreservesIdentity) != 0)
            return value with { Node = HirNodeId.None, Location = null };
        return value with
        {
            Node = HirNodeId.None,
            Location = null,
            IndexedRegion = null,
            IndexedAliases = [],
            Origin = null,
            Observation = null,
            EagerRange = false
        };
    }

    private ImmutableArray<SemanticValue> CheckNativeArguments(NativeSignature signature,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext context)
    {
        if ((!signature.Variadic && arguments.Count != signature.Parameters.Count)
            || signature.Variadic && arguments.Count < signature.Parameters.Count)
            throw SemanticDiagnosticFactory.Arity(hir.Sources[source.Source.Value], new(source.Start, source.Length),
                $"Native '{signature.Name}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, got {arguments.Count}.");
        var converted = ImmutableArray.CreateBuilder<SemanticValue>(arguments.Count);
        for (int index = 0; index < arguments.Count; index++)
        {
            if (index >= signature.Parameters.Count) { converted.Add(arguments[index]); continue; }
            var target = hir.Type(signature.Parameters[index]);
            Require(arguments[index], target, source, allowImplicit: true, context: context);
            converted.Add(Convert(arguments[index], target, source, context));
        }
        return converted.MoveToImmutable();
    }

    private SemanticValue FixedNativeResult(NativeSignature signature)
    {
        SemanticValue Scalar(TypeRef type) => ValueForType(hir.Type(type));
        if (signature.Name == "environment.variables")
            return RangeValue(Record(("name", Scalar(TypeRef.String)), ("value", Scalar(TypeRef.String))));
        if (signature.Name == "shell.process_table")
            return RangeValue(Record(("pid", Scalar(TypeRef.Number)), ("name", Scalar(TypeRef.String))));
        if (signature.Name is "regex.find" or "regex.matches")
        {
            var group = Record(("name", Scalar(TypeRef.String)), ("value", Scalar(TypeRef.String)),
                ("index", Scalar(TypeRef.Number)), ("length", Scalar(TypeRef.Number)), ("success", Scalar(TypeRef.Bool)));
            var match = Record(("value", Scalar(TypeRef.String)), ("index", Scalar(TypeRef.Number)),
                ("length", Scalar(TypeRef.Number)), ("groups", RangeValue(group)));
            return signature.Name == "regex.matches" ? RangeValue(match) : match with { Type = Nullable(match.Type) };
        }
        return Scalar(signature.Result);
    }

    private SemanticValue Record(params (string Name, SemanticValue Value)[] fields)
    {
        var members = fields.ToImmutableDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
        int layout = Shape(members);
        return new(hir.Type(new HirType(TypeRef.Scope, Shape: layout)), HirNodeId.None,
            Scope: new(-1, layout, members, ImmutableDictionary<string, HirLocationId>.Empty));
    }

    private SemanticValue RangeValue(SemanticValue element) => new(RangeType(element.Type), HirNodeId.None,
        Element: element with { Location = null });

    private SemanticValue Item(SemanticValue range)
    {
        if (range.Element is { } element) return element;
        var type = hir.Types[range.Type.Value];
        return ValueForType(type.Element >= 0 ? new(type.Element) : hir.Type(type.RuntimeType.Element ?? TypeRef.Any));
    }

    private SemanticValue Flatten(SemanticValue value, HirSourceRange source)
    {
        var inner = Item(value);
        Require(inner, hir.Type(new TypeRef(TypeKind.Range)), source);
        return RangeValue(Item(inner));
    }

    private SemanticValue NativeCastResult(IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context)
    {
        if (arguments[1].DenotedType is not { } target)
            throw Error(source, "A cast target must be a statically inferred Type value.", "E0220");
        TypeRef runtime = hir.Types[target.Value].RuntimeType;
        if (!ExplicitConversions.Possible(Runtime(arguments[0]), runtime))
            throw Error(source, $"Cannot cast {Runtime(arguments[0])} to {runtime}.", "E0220");
        if (hir.Types[target.Value].Concept >= 0) Require(arguments[0], target, source, context: context);
        if (runtime.Accepts(Runtime(arguments[0]))) return arguments[0] with { Node = HirNodeId.None, Location = null };
        return ValueForType(target);
    }

    private SemanticValue StaticMetaResult(string operation, IReadOnlyList<SemanticValue> arguments)
    {
        if (operation == "alternatives") return RangeValue(ValueForType(hir.Type(TypeRef.Type)));
        HirTypeId first = arguments[0].DenotedType!.Value;
        TypeRef type = hir.Types[first.Value].RuntimeType;
        TypeRef? second = arguments.Count > 1 ? hir.Types[arguments[1].DenotedType!.Value.Value].RuntimeType : null;
        if (operation == "range_rank")
            return new(hir.Type(TypeRef.Number), HirNodeId.None, new(HirLiteralKind.Number, Number: MetaTypes.RangeRank(type)));
        if (operation == "type_name")
            return new(hir.Type(TypeRef.String), HirNodeId.None, new(HirLiteralKind.String, Text: type.ToString()));
        if (operation is not ("add_null" or "remove_null" or "range_of" or "element_type" or "common_type"))
            return new(hir.Type(TypeRef.Bool), HirNodeId.None,
                new(HirLiteralKind.Bool, Boolean: MetaTypes.Predicate(operation, type, second)));
        if (MetaTypes.Derived(operation, type, second) is not { } derived)
            return new(hir.Type(TypeRef.Null), HirNodeId.None, new(HirLiteralKind.Null));
        HirTypeId denoted = operation switch
        {
            "add_null" => Nullable(first),
            "remove_null" => NonNullable(first),
            "range_of" => RangeType(first),
            "element_type" when hir.Types[first.Value].Element >= 0 => new(hir.Types[first.Value].Element),
            "common_type" => Join(ValueForType(first), ValueForType(arguments[1].DenotedType!.Value)).Type,
            _ => hir.Type(derived)
        };
        return new(hir.Type(TypeRef.Type), HirNodeId.None, new(HirLiteralKind.Type, Type: derived), DenotedType: denoted);
    }

    private SemanticValue StaticAlternatives(HirTypeId type, SemanticValue? callee, SemanticValue operand,
        HirSourceRange source, SemanticContext context)
    {
        var alternatives = MetaRange(MetaTypes.Alternatives(hir.Types[type.Value].RuntimeType)
            .Select(alternative => MetaType(hir.Type(alternative))), hir.Type(TypeRef.Type));
        Trace?.Metric("meta.static_alternative_sets", 1);
        Trace?.Metric("meta.static_alternatives", alternatives.Items.Length);
        var result = EmitMetaResult(alternatives, source, context);
        if (!context.Publish) return result;
        List<HirNodeId> effects = [];
        foreach (var evaluated in new[] { callee?.Node ?? HirNodeId.None, operand.Node })
            if (!evaluated.IsNone && (hir.Nodes[evaluated.Value].Effects & ~(HirEffects.ReadLocation | HirEffects.ReadMemory)) != 0)
                effects.Add(context.Output!.Add(HirKind.Discard, hir.Type(TypeRef.Null), source, [evaluated]));
        if (effects.Count == 0) return result;
        effects.Add(result.Node);
        return result with { Node = context.Output!.Add(HirKind.Block, result.Type, source, effects.ToArray()) };
    }

    private SemanticValue DynamicElement(SemanticValue value, SemanticContext context)
    {
        var element = value.Element is null ? null : DynamicElement(value.Element, context);
        var scope = value.Scope;
        if (scope is not null)
        {
            var fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => DynamicElement(pair.Value, context), StringComparer.Ordinal);
            scope = scope with
            {
                Region = ++nextRegion,
                Fields = fields,
                Dynamic = true,
                Aliases = ScopeAliases(scope).ToImmutableArray(),
                Locations = ImmutableDictionary<string, HirLocationId>.Empty
            };
            context.Objects[scope.Region] = scope;
            foreach (int alias in scope.Aliases)
                context.AliasViews[alias] = context.AliasViews.GetValueOrDefault(alias, ImmutableHashSet<int>.Empty).Add(scope.Region);
        }
        return value with
        {
            Scope = scope,
            Element = element,
            Literal = null,
            DenotedType = null,
            TypeCandidates = null,
            BranchValue = null,
            Location = null,
            Node = HirNodeId.None
        };
    }

}
