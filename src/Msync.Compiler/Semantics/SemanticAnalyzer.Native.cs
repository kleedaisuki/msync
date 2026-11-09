using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue InvokeNative(string name, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context, SemanticValue? callee)
    {
        if (!NativeSignatures.TryGet(name, out var signature))
            throw Error(source, $"Unknown native '{name}'.", "E0204");
        if (IsDependentMeta(name))
            throw Error(source, "Dependent metadata queries require a direct, statically known invocation.", "E0222");
        var inputs = CheckNativeArguments(signature, arguments, source, context).ToArray();
        SemanticValue? callback = null;
        ImmutableArray<SemanticDeferredWrite> callbackEffects = [];
        if (signature.Parameters.Count > 0 && inputs.Length >= signature.Parameters.Count
            && signature.Parameters[^1].WithoutNull == TypeRef.Function)
        {
            int index = signature.Parameters.Count - 1;
            var item = Item(inputs[0]);
            IReadOnlyList<SemanticValue> callbackArguments = name == "ranges.sort_with" ? [item, item]
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
            (callback, inputs[index], callbackEffects) = NativeCallback(inputs[index], callbackArguments, source, context);
            if (name is "ranges.filter" or "ranges.take_while" or "ranges.drop_while" or "ranges.any" or "ranges.any_of"
                or "ranges.all_of" or "ranges.none" or "ranges.none_of" or "ranges.find_if" or "ranges.sort_with")
                Require(callback, hir.Type(TypeRef.Bool), source, context: context);
        }
        var value = NativeResult(signature, inputs, callback, source, context);
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        ImmutableArray<SemanticDeferredWrite> pending = callbackEffects;
        for (int index = 0; index < signature.Parameters.Count && index < inputs.Length; index++)
            if (signature.Parameters[index].WithoutNull.Kind == TypeKind.Range)
                pending = CombineDeferredEffects(pending, inputs[index].DeferredEffects);
        if (signature.ResultRule == NativeResultRule.RangeFlatten && inputs[0].Element is { } nested)
            pending = pending.AddRange(nested.DeferredEffects.IsDefault ? [] : nested.DeferredEffects);
        if ((behavior & NativeBehavior.Deferred) != 0)
            value = value with { DeferredEffects = pending, EagerRange = false };
        else if ((behavior & (NativeBehavior.Enumerates | NativeBehavior.Invokes)) != 0)
        {
            ApplyDeferredEffects(pending, context);
            value = value with { DeferredEffects = [] };
        }
        if (value.Literal is { } literal && signature.Name.StartsWith("meta.", StringComparison.Ordinal))
            return value with { Node = context.Publish ? context.Output!.Literal(literal, value.Type, source) : HirNodeId.None };
        callee ??= new(hir.Type(TypeRef.Function), context.Publish
            ? context.Output!.Add(HirKind.NativeFunction, hir.Type(TypeRef.Function), source, text: name) : HirNodeId.None,
            Callables: [new(NativeName: name)]);
        return value with
        {
            Node = EmitCall(new(HirCallKind.Native, new(-1), name), callee, inputs, value.Type, source, context, NativeEffects(signature)),
            Location = null
        };
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

    private (SemanticValue Result, SemanticValue Function, ImmutableArray<SemanticDeferredWrite> Effects) NativeCallback(SemanticValue callable,
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
                targets.Add(new(HirCallableId.From(template), instance.Id));
            }
            else if (choice.NativeName is { } native)
                result = InvokeType(new(hir.Type(TypeRef.Function), HirNodeId.None, Callables: [choice]), arguments, source, context);
            else throw Error(source, "A native callback has no semantic callable identity.", "E0204");
            output = output is null ? result : Join(output, result);
        }
        if (output is null) throw Error(source, "The callback is not a resolved Function value.", "E0204");
        if (context.Publish && targets.Count != 0)
        {
            int adaptation = hir.Adaptations.Count;
            context.Output!.AddAdaptation(new(targets.ToImmutable()));
            callable = callable with { Node = context.Output!.Add(HirKind.AdaptFunction, callable.Type, source, [callable.Node], adaptation) };
        }
        return (output, callable, effects.ToImmutable());
    }

    private static ImmutableArray<SemanticDeferredWrite> CombineDeferredEffects(
        ImmutableArray<SemanticDeferredWrite> first, ImmutableArray<SemanticDeferredWrite> second)
    {
        if (first.IsDefaultOrEmpty) return second.IsDefault ? [] : second;
        return second.IsDefaultOrEmpty ? first : first.AddRange(second);
    }

    private void ApplyDeferredEffects(SemanticValue range, SemanticContext context) =>
        ApplyDeferredEffects(range.DeferredEffects, context);

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
                    context.Indexed[write.Region] = storage with { Element = Join(storage.Element, write.After) with { Literal = null } };
                context.IndexedWrites.Add(write.Region);
                if (context.Indexed.TryGetValue(write.Region, out var afterStorage))
                    context.RecordWrite(new(SemanticWriteKind.Indexed, afterStorage.Element, Region: write.Region));
                continue;
            }
            if (!context.Objects.TryGetValue(write.Region, out var target)
                || !target.Fields.TryGetValue(write.Field!, out var previous)) continue;
            InvalidateScopeAliases(target, context);
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
        var element = value.Element is null ? "" : "/" + TypeSchemaKey(value.Element, visited);
        var fields = value.Scope is null ? "" : "{" + string.Join(';', value.Scope.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + TypeSchemaKey(pair.Value, visited))) + "}";
        var functions = "[" + string.Join(',', Choices(value).Select(choice =>
            (choice.NativeName ?? "t" + choice.Template?.Value) + "(" + string.Join(',', choice.Captures.IsDefault
                ? [] : choice.Captures.Select(capture => TypeSchemaKey(capture.Value, visited))) + ")")) + "]";
        return Runtime(value) + fields + element + functions;
    }

    private SemanticValue NativeResult(NativeSignature signature, IReadOnlyList<SemanticValue> arguments,
        SemanticValue? callbackResult, HirSourceRange source, SemanticContext context)
    {
        if (signature.Name.StartsWith("storage.buffer_", StringComparison.Ordinal))
            return IndexedNativeResult(signature, arguments, source, context);
        if (signature.Name.StartsWith("meta.", StringComparison.Ordinal)
            && arguments.Count > 0 && arguments.All(argument => argument.DenotedType is not null))
            return StaticMetaResult(signature.Name[5..], arguments, source);
        var result = signature.ResultRule switch
        {
            NativeResultRule.Fixed => FixedNativeResult(signature),
            NativeResultRule.FirstArgument => arguments[0] with { Node = HirNodeId.None, Location = null },
            NativeResultRule.RangeOfArguments => RangeValue(arguments.Count == 0
                ? ValueForType(hir.Type(new TypeRef(TypeKind.Never))) : arguments.Aggregate(Join)),
            NativeResultRule.RangeItem => Item(arguments[0]) with { Node = HirNodeId.None, Location = null },
            NativeResultRule.Transform => RangeValue(callbackResult ?? ValueForType(hir.Type(TypeRef.Any))),
            NativeResultRule.CastTarget => NativeCastResult(arguments, source, context),
            NativeResultRule.RangeConcat => RangeValue(Join(Item(arguments[0]), Item(arguments[1]))),
            NativeResultRule.RangeFlatten => Flatten(arguments[0], source),
            NativeResultRule.RangeZip => RangeValue(Record(("first", Item(arguments[0])), ("second", Item(arguments[1])))),
            NativeResultRule.RangeEnumerate => RangeValue(Record(("index", ValueForType(hir.Type(TypeRef.Number))), ("value", Item(arguments[0])))),
            NativeResultRule.RangeNested => RangeValue(arguments[0] with { Node = HirNodeId.None, Location = null }),
            NativeResultRule.RangeScan => RangeValue(callbackResult is null ? arguments[1] : Join(arguments[1], callbackResult)),
            NativeResultRule.RangeRepeat => RangeValue(arguments[0] with { Node = HirNodeId.None, Location = null }),
            _ => throw Error(source, $"Unknown native result rule '{signature.ResultRule}'.")
        };
        if (signature.Name == "ranges.sort")
        {
            TypeRef item = Runtime(Item(arguments[0]));
            if (item != TypeRef.Bool && item != TypeRef.Number && !new TypeRef(TypeKind.Path | TypeKind.String).Accepts(item))
                throw Error(source, $"Natural sorting requires Bool, Number or Path/String items, found {item}. Use sort_with for a custom order.", "E0221");
        }
        bool eager = (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0;
        return result with { EagerRange = eager || result.EagerRange };
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
        if (signature.Name is "storage.buffer_set" or "storage.buffer_push" or "storage.buffer_insert"
            && arguments[0].IndexedRegion is { } region && context.Indexed.TryGetValue(region, out var indexed)
            && indexed.FixedCarrier is { } carrier)
        {
            int last = converted.Count - 1;
            Require(converted[last], carrier, source, allowImplicit: true, context: context);
            converted[last] = Convert(converted[last], carrier, source, context);
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

    private SemanticValue StaticMetaResult(string operation, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source)
    {
        var types = arguments.Select(argument => hir.Types[argument.DenotedType!.Value.Value].RuntimeType).ToArray();
        if (operation == "alternatives") return RangeValue(ValueForType(hir.Type(TypeRef.Type)));
        Value result = MetaTypes.Evaluate(operation, types);
        HirTypeId? denoted = result is TypeValue typed ? hir.Type(typed.Data) : null;
        if (arguments[0].DenotedType is { } first)
        {
            denoted = operation switch
            {
                "add_null" => Nullable(first),
                "remove_null" => NonNullable(first),
                "range_of" => RangeType(first),
                "element_type" when hir.Types[first.Value].Element >= 0 => new(hir.Types[first.Value].Element),
                "common_type" => Join(ValueForType(first), ValueForType(arguments[1].DenotedType!.Value)).Type,
                _ => denoted
            };
        }
        HirLiteral literal = result switch
        {
            NullValue => new(HirLiteralKind.Null),
            BoolValue boolean => new(HirLiteralKind.Bool, Boolean: boolean.Data),
            NumberValue number => new(HirLiteralKind.Number, Number: number.Data),
            StringValue text => new(HirLiteralKind.String, Text: text.Data),
            TypeValue type => new(HirLiteralKind.Type, Type: type.Data),
            _ => throw Error(source, $"Unsupported constant type metadata result '{result.Type}'.")
        };
        return new(hir.Type(result.Type), HirNodeId.None, literal, DenotedType: denoted);
    }

    private SemanticValue IndexedNativeResult(NativeSignature signature, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context)
    {
        if (signature.Name == "storage.buffer_new")
        {
            var element = DynamicElement(Item(arguments[0]), context);
            int region = ++nextRegion;
            context.Indexed[region] = new(element,
                Runtime(element).Kind == TypeKind.Never ? null : element.Type);
            return RangeValue(element) with { IndexedRegion = region, EagerRange = true };
        }
        if (arguments[0].IndexedRegion is not { } identity || !context.Indexed.TryGetValue(identity, out var storage))
            throw Error(source, "Indexed operations require a Buffer-owned Range; construct it with Buffer::new.", "E0222");
        if (signature.Effect == NativeEffect.Write) context.IndexedWrites.Add(identity);
        if (signature.Name is "storage.buffer_set" or "storage.buffer_push" or "storage.buffer_insert")
        {
            var value = arguments[^1];
            if (storage.FixedCarrier is null)
                storage = new(DynamicElement(value, context), value.Type);
            else Require(value, storage.FixedCarrier.Value, source, allowImplicit: true, context: context);
            storage = storage with { Element = DynamicElement(Join(storage.Element, value), context) };
            context.Indexed[identity] = storage;
        }
        if (signature.Effect == NativeEffect.Write)
            context.RecordWrite(new(SemanticWriteKind.Indexed, storage.Element, Region: identity));
        return signature.Name switch
        {
            "storage.buffer_get" or "storage.buffer_pop" or "storage.buffer_remove" =>
                DynamicElement(storage.Element, context) with { Node = HirNodeId.None, Location = null },
            "storage.buffer_snapshot" => RangeValue(storage.Element) with { EagerRange = true },
            _ => ValueForType(hir.Type(signature.Result))
        };
    }

    private SemanticValue DynamicElement(SemanticValue value, SemanticContext context)
    {
        var element = value.Element is null ? null : DynamicElement(value.Element, context);
        var scope = value.Scope;
        if (scope is not null)
        {
            var fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => DynamicElement(pair.Value, context), StringComparer.Ordinal);
            scope = scope with { Region = ++nextRegion, Fields = fields, Dynamic = true,
                Aliases = ScopeAliases(scope).ToImmutableArray(), Locations = ImmutableDictionary<string, HirLocationId>.Empty };
            context.Objects[scope.Region] = scope;
            foreach (int alias in scope.Aliases)
                context.AliasViews[alias] = context.AliasViews.GetValueOrDefault(alias, ImmutableHashSet<int>.Empty).Add(scope.Region);
        }
        return value with { Scope = scope, Element = element, Literal = null, DenotedType = null,
            Location = null, Node = HirNodeId.None };
    }
}
