using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public enum LlvmRuntimeConversionOperation : byte
{
    Represent, Coerce, Convert, IsNull, IsType, TypeOf, Narrow, ObservedTextNumber, ObservedTextBool
}
public sealed record LlvmRuntimeConversionBinding(LlvmRuntimeConversionOperation Operation, int TargetType = -1);

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        internal readonly Dictionary<LlvmFunction, LlvmRuntimeConversionBinding> runtimeConversions = [];
        private readonly Dictionary<(LlvmFunctionType Signature, LlvmRuntimeConversionBinding Binding), LlvmFunction> conversionBindings = [];
        internal HashSet<LlvmCallInstruction>? projectedRepresentations;

        private void PruneRepresentations()
        {
            if (projectedRepresentations is null) return;
            int dead = 0;
            foreach (var packed in projectedRepresentations)
            {
                if (!packed.UseEmpty) continue;
                packed.EraseFromParent();
                ++dead;
            }
            trace?.Detail?.Metric("represent.roundtrip_packs_removed", dead);
        }

        internal LlvmFunction ConversionBinding(LlvmFunctionType signature, LlvmRuntimeConversionBinding binding)
        {
            if (conversionBindings.TryGetValue((signature, binding), out var existing)) return existing;
            var target = LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage,
                "msync.value." + binding.Operation.ToString().ToLowerInvariant() + "." + conversionBindings.Count, module);
            if (binding.Operation == LlvmRuntimeConversionOperation.Represent)
            {
                // Moving a payload preserves its identity and never demands it.
                target.MemoryEffectsAttribute = new(0);
                target.NoUnwind = true;
                target.WillReturnAttribute = true;
            }
            conversionBindings.Add((signature, binding), target);
            runtimeConversions.Add(target, binding);
            return target;
        }
    }
    private sealed partial class Body
    {
        private readonly Dictionary<LlvmRuntimeConversionOperation, long>? conversionCounts = owner.trace?.Detail is null ? null : [];
        private LlvmValue ConversionCall(LlvmRuntimeConversionOperation operation, LlvmType resultType,
            int resultLogicalType, IReadOnlyList<LlvmValue> arguments, int targetType = -1)
        {
            var signature = LlvmFunctionType.Get(resultType, arguments.Select(value => value.Type).ToArray());
            var binding = new LlvmRuntimeConversionBinding(operation, targetType);
            var target = owner.ConversionBinding(signature, binding);
            if (conversionCounts is not null) conversionCounts[operation] = conversionCounts.GetValueOrDefault(operation) + 1;
            return Add(new LlvmCallInstruction(signature, target, arguments) { LogicalType = resultLogicalType });
        }
        private LlvmValue Represent(LlvmValue value, HirTypeId target, bool admitsNull = false)
        {
            int logical = owner.metadata.Type(target, admitsNull);
            var type = owner.Type(target, admitsNull);
            if (ReferenceEquals(value.Type, type)) return value;
            if (value is LlvmCallInstruction { CalledFunction: { } callee } packed
                && owner.runtimeConversions.TryGetValue(callee, out var binding)
                && binding.Operation == LlvmRuntimeConversionOperation.Represent
                && packed.GetArgument(0) is { } original && ReferenceEquals(original.Type, type)
                && original.LogicalType == logical)
            {
                (owner.projectedRepresentations ??= []).Add(packed);
                return original;
            }
            // A flow refinement selects an already-known representation. This
            // binding copies/tag-packs/extracts payload; it never admits a value.
            return ConversionCall(LlvmRuntimeConversionOperation.Represent, type, logical, [value]);
        }
        private LlvmValue ReadLocation(HirNodeId id, HirNode node)
        {
            var location = new HirLocationId(node.Data);
            var storage = Hir.Locations[location.Value];
            var loaded = Add(new LlvmLoadInstruction(owner.Type(storage.StorageType, storage.AdmitsNull), Address(location))
                { LogicalType = owner.metadata.Type(storage.StorageType, storage.AdmitsNull) });
            var value = Represent(loaded, node.Type);
            return Hir.SessionSnapshots?.TryGetValue(id, out var snapshot) == true ? SnapshotRead(node, snapshot, value) : value;
        }
        private LlvmValue Coerce(HirNode node)
        {
            var value = Child(node, 0);
            if (block.HasTerminator) return value;
            current = node;
            var target = new HirTypeId(node.Data);
            int targetType = owner.metadata.Type(target);
            TypeKind sourceKinds = value.LogicalType >= 0 ? owner.metadata.Types[value.LogicalType].Kind
                : Hir.Types[Hir.Node(Hir.Children(node)[0]).Type.Value].RuntimeType.Kind;
            TypeKind targetKinds = owner.metadata.Types[targetType].Kind;
            if (node.Kind == HirKind.Coerce && (sourceKinds & ~targetKinds) == 0)
                return Represent(value, node.Type);
            return ConversionCall(node.Kind == HirKind.Convert ? LlvmRuntimeConversionOperation.Convert
                : LlvmRuntimeConversionOperation.Coerce, owner.Type(node.Type), owner.metadata.Type(node.Type), [value], targetType);
        }
        private LlvmValue TypePredicate(HirNode node)
        {
            var value = Child(node, 0);
            if (block.HasTerminator) return value;
            if (node.Kind == HirKind.IsType && node.Text == "pattern.layout")
                return HasLayout(value, owner.metadata.Layout(Hir.Types[node.Data].Shape));
            var arguments = new List<LlvmValue> { value };
            if (node.Kind == HirKind.IsType)
                arguments.Add(node.Children.Count == 2 ? Child(node, 1) : TypeValue(new(node.Data)));
            if (block.HasTerminator) return owner.Null;
            current = node;
            if (node.Kind == HirKind.IsType && RelocatedType(value) is { } actual
                && RelocatedType(arguments[1]) is { } expected)
            {
                constantTypePredicates++;
                return LlvmConstantInt.GetBool(owner.context, expected.Accepts(actual));
            }
            var operation = node.Kind switch
            {
                HirKind.IsNull => LlvmRuntimeConversionOperation.IsNull,
                HirKind.IsType => LlvmRuntimeConversionOperation.IsType,
                _ => LlvmRuntimeConversionOperation.TypeOf
            };
            return ConversionCall(operation, owner.Type(node.Type), owner.metadata.Type(node.Type), arguments);
        }
        private long constantTypePredicates;

        private TypeRef? RelocatedType(LlvmValue value) =>
            value is LlvmLoadInstruction { PointerOperand: LlvmGlobalVariable global }
            && owner.constants.TryGetValue(global, out var literal) && literal.Kind == NativeKind.Type
                ? literal.Type : null;

        private LlvmValue TypeValue(HirTypeId type)
        {
            var source = Hir.Types[type.Value];
            var runtime = source.RuntimeType;
            if (source.Nullable) runtime = runtime with { Kind = runtime.Kind | TypeKind.Null };
            return RuntimeTypeValue(runtime);
        }
        private LlvmValue RuntimeTypeValue(TypeRef type) => RelocatedValue(owner.metadata.RuntimeType(TypeRef.Type),
            owner.DataPointer, new(NativeKind.Type, Type: type));
        private LlvmValue RelocatedValue(int logical, LlvmType type, NativeConstant value)
        {
            var global = new LlvmGlobalVariable(owner.module, type, true, LlvmLinkage.ExternalLinkage,
                name: "msync.literal." + owner.constants.Count) { LogicalType = owner.metadata.Pointer(logical) };
            owner.constants.Add(global, value);
            return Add(new LlvmLoadInstruction(type, global) { LogicalType = logical });
        }
    }
}
