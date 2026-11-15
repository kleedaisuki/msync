using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<int, LlvmStaticScopeInfo> staticScopes = [];

        internal LlvmStaticScopeInfo StaticScope(HirNode node, HirScopeConstruction construction)
        {
            int declaration = construction.Site!.Value.Value;
            if (staticScopes.TryGetValue(declaration, out var existing))
            {
                trace?.Detail?.Metric("binding.reused", 1);
                return existing;
            }
            var layout = Layout(construction.Layout);
            var storage = new LlvmGlobalVariable(module, layout, false, LlvmLinkage.InternalLinkage,
                LlvmConstantAggregateZero.Get(layout), "msync.static." + declaration)
                { LogicalType = metadata.Type(node.Type) };
            var ready = new LlvmGlobalVariable(module, LlvmIntegerType.Get(context, 1), false, LlvmLinkage.InternalLinkage,
                LlvmConstantInt.GetFalse(context), "msync.static.ready." + declaration)
                { LogicalType = metadata.Pointer(metadata.RuntimeType(TypeRef.Bool)) };
            var result = new LlvmStaticScopeInfo(storage, ready, metadata.Layout(construction.Layout), declaration);
            staticScopes.Add(declaration, result);
            trace?.Detail?.Metric("binding.created", 1);
            return result;
        }
    }

    private sealed partial class Body
    {
        private long staticAccessCount;
        private long staticInitializerCount;

        private LlvmValue Singleton(HirNode node, HirScopeConstruction construction)
        {
            if (owner.trace?.Detail is not { } detail) return EmitSingleton(node, construction);
            return detail.MeasureDetailed("compiler.llvm.lower.static_scope", (Body: this, Node: node, Construction: construction),
                static state => state.Body.EmitSingleton(state.Node, state.Construction),
                attributes: HirTelemetry.Source(Hir.Sources, node.Source));
        }

        private LlvmValue EmitSingleton(HirNode node, HirScopeConstruction construction)
        {
            var declaration = owner.StaticScope(node, construction);
            var pointer = declaration.Object;
            var layout = (LlvmStructType)pointer.ValueType;
            BindScope(node, construction, layout, pointer);
            var ready = Add(new LlvmLoadInstruction(LlvmIntegerType.Get(owner.context, 1), declaration.Initialized,
                alignment: new LlvmAlign(1), ordering: LlvmAtomicOrdering.Acquire)
                { LogicalType = owner.metadata.RuntimeType(TypeRef.Bool) });
            var initialize = new LlvmBasicBlock(owner.context, "static.initialize", function);
            var done = new LlvmBasicBlock(owner.context, "static.ready", function);
            Add(new LlvmCondBrInstruction(ready, done, initialize));
            block = initialize;
            foreach (var child in Hir.Children(node))
            {
                if (block.HasTerminator) break;
                Emit(child);
            }
            current = node;
            if (!block.HasTerminator)
            {
                Add(new LlvmStoreInstruction(LlvmConstantInt.GetTrue(owner.context), declaration.Initialized,
                    alignment: new LlvmAlign(1), ordering: LlvmAtomicOrdering.Release));
                Add(new LlvmUncondBrInstruction(done));
            }
            block = done;
            staticAccessCount++;
            staticInitializerCount++;
            owner.trace?.Detail?.Metric("initializers.statements", node.Children.Count);
            owner.trace?.Detail?.Metric("fields", construction.Fields.Length);
            return pointer;
        }
    }
}
