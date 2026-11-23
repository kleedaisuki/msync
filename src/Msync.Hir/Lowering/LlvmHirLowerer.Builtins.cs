using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal readonly Dictionary<LlvmFunction, string> nativeCalls = [];
        internal readonly Dictionary<LlvmFunction, NativeKind> constructors = [];
        private readonly Dictionary<(string Name, LlvmFunctionType Type), LlvmFunction> builtinBindings = [];

        internal LlvmFunction BuiltinBinding(string name, LlvmFunctionType type, HirSourceRange source,
            NativeKind? constructor = null)
        {
            if (trace?.Detail is not { } detail) return BindBuiltin(name, type, constructor);
            var attributes = new Dictionary<string, string>
            {
                ["builtin.name"] = name,
                ["binding.kind"] = constructor is null ? "native" : "constructor",
                ["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (source.Source.Value >= 0) attributes["source.file"] = hir.Sources[source.Source.Value].Name;
            if (constructor is null)
            {
                var signature = NativeSignatures.Get(name);
                attributes["builtin.effect"] = signature.Effect.ToString();
                attributes["builtin.behavior"] = signature.Behavior.GetValueOrDefault().ToString();
                attributes["builtin.result_rule"] = signature.ResultRule.ToString();
            }
            return detail.MeasureDetailed("compiler.llvm.lower.builtin-binding", (Owner: this, name, type, constructor),
                static state => state.Owner.BindBuiltin(state.name, state.type, state.constructor), attributes: attributes);
        }

        private LlvmFunction BindBuiltin(string name, LlvmFunctionType type, NativeKind? constructor)
        {
            if (builtinBindings.TryGetValue((name, type), out var existing))
            {
                trace?.Metric("binding.reused", 1);
                return existing;
            }
            var target = LlvmFunction.Create(type, LlvmLinkage.ExternalLinkage,
                "msync.builtin." + name + "." + builtinBindings.Count, module);
            builtinBindings.Add((name, type), target);
            if (constructor is { } kind) constructors.Add(target, kind);
            else nativeCalls.Add(target, name);
            trace?.Metric("binding.created", 1);
            trace?.Metric("binding.parameters", type.NumParams);
            return target;
        }
    }

    private sealed partial class Body
    {
        private LlvmValue Builtin(HirNode node)
        {
            string name = node.Text!.TrimStart('$');
            ConnectionKind kind = name switch
            {
                "local" or "pwd" or "file" => ConnectionKind.Local,
                "wsl" => ConnectionKind.Wsl,
                "env" => ConnectionKind.Environment,
                "path" => ConnectionKind.SearchPath,
                _ => throw new LlvmHirLoweringException(node.Source, "builtin namespace " + name)
            };
            owner.trace?.Detail?.Metric("builtin.connection." + name, 1);
            return Relocated(node, new(HirLiteralKind.Connection,
                Text: name is "pwd" or "file" ? name : "", ConnectionKind: kind));
        }

        private LlvmValue Range(HirNode node)
        {
            var arguments = new List<LlvmValue>(node.Children.Count);
            var carrier = LiteralRangeElement(node);
            foreach (var child in Hir.Children(node))
            {
                var item = Emit(child);
                if (block.HasTerminator) return owner.Null;
                arguments.Add(PackRangeItem(item, carrier));
            }
            current = node;
            return ConstructRange(node, arguments);
        }

        private LlvmValue ConstructRange(HirNode node, IReadOnlyList<LlvmValue> arguments)
        {
            var type = LlvmFunctionType.Get(owner.Type(node.Type), arguments.Select(value => value.Type).ToArray());
            var target = owner.BuiltinBinding("constructor.range", type, node.Source, NativeKind.Range);
            owner.trace?.Metric("builtin.range.elements", arguments.Count);
            return Add(new LlvmCallInstruction(type, target, arguments)
            { LogicalType = owner.metadata.Type(node.Type) });
        }

        private LlvmValue NativeBuiltin(HirNode node, string name, IReadOnlyList<LlvmValue> arguments)
        {
            // The semantic HIR has already specialized result and element types.
            var signature = NativeSignatures.Get(name);
            if (signature.ResultRule == NativeResultRule.RangeEnumerate) owner.RegisterEnumeration(node);
            if (name == "regex.find") owner.RegisterRegexFind();
            if (signature.ResultRule == NativeResultRule.RangeZip) owner.RegisterZip(node);
            if (name == "ranges.concat") arguments = SelectConcatLiterals(node, arguments);
            var type = LlvmFunctionType.Get(owner.Type(node.Type), arguments.Select(value => value.Type).ToArray());
            var target = owner.BuiltinBinding(name, type, node.Source);
            current = node;
            var result = Add(new LlvmCallInstruction(type, target, arguments)
                { LogicalType = owner.metadata.Type(node.Type) });
            if (signature.MaySubmitScalar) return owner.DeferredSource(result, "native_submission");
            if ((signature.Behavior.GetValueOrDefault() & NativeBehavior.Invokes) != 0
                && (signature.IsStreamReduction || (signature.Result.Kind & TypeKind.Range) != 0))
                return owner.DeferredSource(result, "range_callback");
            return result;
        }
    }
}
