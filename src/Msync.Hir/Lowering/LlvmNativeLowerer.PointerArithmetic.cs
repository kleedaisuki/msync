using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Lowering
    {
        private Dictionary<int, LlvmStructType>? llvmStorageLayouts;
        private readonly Dictionary<LlvmStructType, PointerFields> pointerFields = [];
        private readonly record struct PointerFieldInfo(int Field, int Type, LlvmType LlvmType);
        private sealed class PointerFields
        {
            internal readonly Dictionary<ulong, PointerFieldInfo> First = [];
            internal readonly Dictionary<(ulong Offset, int Type), PointerFieldInfo> ByNativeType = [];
            internal readonly Dictionary<(ulong Offset, LlvmType Type), PointerFieldInfo> ByLlvmType = [];
        }
        internal LlvmStructType? StorageStructure(int layout)
        {
            if (llvmStorageLayouts is null)
            {
                llvmStorageLayouts = [];
                foreach (var pair in Input.StructLayouts) llvmStorageLayouts.TryAdd(pair.Value, pair.Key);
            }
            return llvmStorageLayouts.GetValueOrDefault(layout);
        }
        internal (int Field, int Type, LlvmType LlvmType)? StorageField(LlvmStructType structure, int nativeLayout,
            ulong offset, int preferredType, LlvmType? llvmType)
        {
            if (!pointerFields.TryGetValue(structure, out var fields))
            {
                fields = new(); pointerFields.Add(structure, fields);
                var source = Input.Module.DataLayout.GetStructLayout(structure);
                for (int index = 0; index < structure.Elements.Count; ++index)
                {
                    ulong fieldOffset = source.GetElementOffset((uint)index).GetFixedValue();
                    var field = new PointerFieldInfo(index, Layouts[nativeLayout].Fields[index].Type, structure.Elements[index]);
                    fields.First.TryAdd(fieldOffset, field);
                    fields.ByNativeType.TryAdd((fieldOffset, field.Type), field);
                    fields.ByLlvmType.TryAdd((fieldOffset, field.LlvmType), field);
                }
            }
            PointerFieldInfo result;
            if (preferredType >= 0)
            {
                if (fields.ByNativeType.TryGetValue((offset, preferredType), out result))
                    return (result.Field, result.Type, result.LlvmType);
                // Zero-sized LLVM members share offsets with their successor.
                // Native type-table IDs need not be equal for the same carrier.
                PointerFieldInfo? compatible = null;
                foreach (var candidate in fields.ByNativeType)
                {
                    if (candidate.Key.Offset != offset ||
                        !NativeTypeRelations.SameStorageRepresentation(Types, candidate.Value.Type, preferredType)) continue;
                    compatible = candidate.Value;
                    break;
                }
                if (compatible is { } selected)
                {
                    fields.ByNativeType.Add((offset, preferredType), selected);
                    trace?.Detail?.Metric("addresses.representation_field_aliases", 1);
                    return (selected.Field, selected.Type, selected.LlvmType);
                }
            }
            if (llvmType is not null && fields.ByLlvmType.TryGetValue((offset, llvmType), out result)
                || fields.First.TryGetValue(offset, out result)) return (result.Field, result.Type, result.LlvmType);
            return null;
        }
    }

    private sealed partial class Body
    {
        private readonly record struct PointerPosition(LlvmValue Root, LlvmStructType? Structure, int Layout, LlvmAPInt Offset);
        private readonly Dictionary<LlvmValue, PointerPosition> pointerRelocations = [];
        private int constantGepRelocations, constantGepMaterializations, zeroGepAliases, constantGepExpressionUses;

        private bool StructuredFieldAddress(LlvmGetElementPtrInstruction address)
        {
            if (address.SourceElementType is not LlvmStructType structure ||
                !owner.Input.StructLayouts.TryGetValue(structure, out int layout) || address.IndexCount != 2 ||
                address.GetIndex(0) is not LlvmConstantInt { IsZero: true } || address.GetIndex(1) is not LlvmConstantInt field) return false;
            int ordinal = checked((int)field.GetZExtValue());
            int type = address.LogicalType >= 0 ? address.LogicalType : owner.Pointer(owner.Layouts[layout].Fields[ordinal].Type);
            if (!values.TryGetValue(address, out int result))
            { result = Register(type); values.Add(address, result); }
            Add(new(NativeOp.AddressOffset, result, OperandSlot(address, 0), layout, ordinal, Source: address.Source));
            return true;
        }

        private void CompilePointerRelocation(LlvmGetElementPtrInstruction address)
        {
            _ = PointerPositionOf(address);
            ++constantGepRelocations;
        }

        private PointerPosition PointerPositionOf(LlvmValue value)
        {
            if (pointerRelocations.TryGetValue(value, out var cached)) return cached;
            var address = LlvmGepOperator.Get(value);
            uint width = owner.Input.Module.DataLayout.GetIndexTypeSizeInBits(address.Type);
            var delta = ConstantGepOffset(address, width);
            PointerPosition origin;
            if (LlvmGepOperator.TryGet(address.PointerOperand, out _)) origin = PointerPositionOf(address.PointerOperand);
            else origin = PointerRoot(address.PointerOperand, width,
                value is LlvmGetElementPtrInstruction { TargetStorageType: { } storage } ? storage :
                    address.User.GetOperandUse(0).TargetStorageType ?? (LlvmType)address.SourceElementType);
            if (value is LlvmGetElementPtrInstruction { TargetStorageType: not null })
                trace?.Metric("addresses.declared_storage_geps", 1);
            var result = origin with { Offset = origin.Offset + delta };
            pointerRelocations.Add(value, result);
            return result;
        }

        private PointerPosition PointerRoot(LlvmValue root, uint width, LlvmType? declaredType = null)
        {
            if (declaredType is LlvmStructType declared && owner.Input.StructLayouts.TryGetValue(declared, out int declaredLayout))
                return new(root, declared, declaredLayout, new(width, 0));
            if (root is LlvmGlobalVariable { ValueType: LlvmStructType globalType } && owner.Input.StructLayouts.TryGetValue(globalType, out int globalLayout))
                return new(root, globalType, globalLayout, new(width, 0));
            if (root is LlvmAllocaInstruction { AllocatedType: LlvmStructType stackType } && owner.Input.StructLayouts.TryGetValue(stackType, out int stackLayout))
                return new(root, stackType, stackLayout, new(width, 0));
            if (root is LlvmHeapAllocInst allocation && allocation.LogicalLayout >= 0)
                return new(root, owner.StorageStructure(allocation.LogicalLayout), allocation.LogicalLayout, new(width, 0));
            int logical = root.LogicalType >= 0 ? root.LogicalType : values.TryGetValue(root, out int slot) ? slots[slot].Type : -1;
            if (logical >= 0 && owner.Types[logical] is { Pointee: < 0, Layouts.Count: 1 } type)
                return new(root, owner.StorageStructure(type.Layouts[0]), type.Layouts[0], new(width, 0));
            return new(root, null, -1, new(width, 0));
        }

        private LlvmAPInt ConstantGepOffset(LlvmGepOperator address, uint width)
        {
            var layout = owner.Input.Module.DataLayout;
            var offset = new LlvmAPInt(width, 0);
            LlvmType indexed = address.SourceElementType;
            for (int index = 0; index < address.IndexCount; ++index)
            {
                if (address.GetIndex(index) is not LlvmConstantInt constant)
                    throw Unsupported(address.User, "dynamic integer GEP index carrier");
                if (index != 0 && indexed is LlvmStructType structure)
                {
                    uint field = checked((uint)constant.GetZExtValue());
                    ulong bytes = layout.GetStructLayout(structure).GetElementOffset(field).GetFixedValue();
                    offset += new LlvmAPInt(width, bytes, implicitTrunc: true);
                    indexed = structure.Elements[checked((int)field)];
                    continue;
                }
                if (index != 0) indexed = LlvmGetElementPtrInstruction.GetTypeAtIndex(indexed, constant)!;
                var size = layout.GetTypeAllocSize(indexed);
                if (size.IsScalable) throw Unsupported(address.User, "scalable GEP stride");
                offset += constant.Value.SExtOrTrunc(width) * new LlvmAPInt(width, size.GetFixedValue(), implicitTrunc: true);
            }
            return offset;
        }

        private (int Field, int Type, LlvmType LlvmType) PointerField(LlvmValue address, PointerPosition position,
            int preferredType = -1, LlvmType? llvmType = null)
        {
            if (position.Structure is null || position.Layout < 0)
            {
                int emittedType = values.TryGetValue(position.Root, out int rootSlot) ? slots[rootSlot].Type : -1;
                int selectedType = selectedCarriers.GetValueOrDefault(position.Root, -1);
                string inputs = position.Root is LlvmUser user ? string.Join(", ", user.OperandUses.Select(use =>
                    $"{use.OperandNo}:{use.Value?.GetType().Name} '{use.Value?.Name}' logical#{use.Value?.LogicalType}/use#{use.LogicalType}")) : "";
                throw Unsupported(address, $"constant GEP without a declared producer storage layout in '{function.Name}', " +
                    $"offset {position.Offset.GetSExtValue()}, root '{position.Root.Name}' ({position.Root.GetType().Name}, " +
                    $"logical #{position.Root.LogicalType}, emitted #{emittedType}, selected #{selectedType}), inputs [{inputs}]");
            }
            if (position.Offset.BitWidth > 64) throw Unsupported(address, "GEP index width above the native pointer carrier");
            if (owner.StorageField(position.Structure, position.Layout, position.Offset.GetZExtValue(), preferredType, llvmType) is { } field) return field;
            var structureLayout = owner.Input.Module.DataLayout.GetStructLayout(position.Structure);
            string fields = string.Join(", ", position.Structure.Elements.Select((type, index) =>
                $"{index}@{structureLayout.GetElementOffset((uint)index).GetFixedValue()}:native#{owner.Layouts[position.Layout].Fields[index].Type}/{type.Id}"));
            throw Unsupported(address, $"constant GEP byte offset {position.Offset.GetSExtValue()} in '{function.Name}', " +
                $"root '{position.Root.Name}' ({position.Root.GetType().Name}, logical #{position.Root.LogicalType}), " +
                $"structure '{position.Structure.Name}', native layout #{position.Layout}, fields [{fields}], " +
                $"requested native #{preferredType}/LLVM {llvmType?.Id}: inside an unrepresented native field or object array");
        }

        private int MaterializePointerRelocation(LlvmValue address, int expectedType = -1)
        {
            if (address is LlvmConstantExpr) ++constantGepExpressionUses;
            var position = PointerPositionOf(address);
            int preferred = expectedType >= 0 ? owner.Types[expectedType].Pointee : -1;
            if (position.Offset.IsZero && (position.Structure is null || preferred < 0))
            {
                ++zeroGepAliases;
                return Slot(position.Root);
            }
            var field = PointerField(address, position, preferred);
            if (preferred >= 0 && !NativeTypeRelations.SameStorageRepresentation(owner.Types, field.Type, preferred))
                throw Unsupported(address, "GEP native field representation for the declared operand view");
            int type = owner.Pointer(field.Type);
            int result = Register(type);
            Add(new(NativeOp.AddressOffset, result, Slot(position.Root), position.Layout, field.Field,
                Source: instructionSource ?? (address as LlvmInstruction)?.Source));
            ++constantGepMaterializations;
            return result;
        }

        private bool DeclaredZeroFieldView(LlvmValue root, int source, int type, out int result)
        {
            result = -1;
            if (owner.Types[slots[source].Type].Pointee >= 0) return false;
            var position = PointerRoot(root, owner.Input.Module.DataLayout.GetIndexTypeSizeInBits(root.Type));
            if (position.Structure is null) return false;
            int storage = owner.Types[type].Pointee;
            if (owner.StorageField(position.Structure, position.Layout, 0, storage, null) is not { } field) return false;
            if (!NativeTypeRelations.SameStorageRepresentation(owner.Types, field.Type, storage))
                throw Unsupported(root, "zero-offset pointer view inside an unrepresented native field");
            result = Register(type);
            Add(new(NativeOp.AddressOffset, result, source, position.Layout, field.Field, Source: instructionSource));
            ++constantGepMaterializations;
            return true;
        }

        private int MemoryStorageType(LlvmUse pointer, LlvmValue? payload = null)
        {
            int logical = pointer.LogicalType >= 0 ? pointer.LogicalType : pointer.Value!.LogicalType;
            if (logical >= 0 && owner.Types[logical].Pointee >= 0) return owner.Types[logical].Pointee;
            int storage;
            if (LlvmGepOperator.TryGet(pointer.Value!, out _))
            {
                var position = PointerPositionOf(pointer.Value!);
                if (position.Structure is not null)
                {
                    int preferred = PayloadTypeHint(payload);
                    storage = PointerField(pointer.Value!, position, preferred, payload?.Type).Type;
                }
                else storage = StorageFromRoot(position.Root, payload);
            }
            else storage = StorageFromRoot(pointer.Value!, payload);
            return storage;
        }

        private int MemoryAddressSlot(LlvmUser user, int index, int storage)
        {
            var use = user.GetOperandUse(index);
            if (use.TargetStorageType is { } declared) return DeclaredStorageFieldView(use.Value!, declared, storage);
            if (use.LogicalType >= 0 && owner.Types[use.LogicalType].Pointee >= 0) return Slot(use);
            return SlotForView(use.Value!, owner.Pointer(storage));
        }

        private int DeclaredStorageFieldView(LlvmValue root, LlvmStructType structure, int storage)
        {
            int layout = owner.Input.StructLayouts[structure];
            var field = owner.StorageField(structure, layout, 0, storage, null)!.Value;
            int result = Register(owner.Pointer(field.Type));
            Add(new(NativeOp.AddressOffset, result, Slot(root), layout, field.Field, Source: instructionSource));
            ++constantGepMaterializations;
            trace?.Metric("addresses.declared_zero_storage_views", 1);
            return result;
        }

        private int SetSelectedValueType(LlvmValue value, int type)
        {
            if (!values.ContainsKey(value)) values.Add(value, Register(type));
            return slots[values[value]].Type;
        }

        private int PayloadTypeHint(LlvmValue? value)
        {
            if (value is null) return -1;
            if (value.LogicalType >= 0) return value.LogicalType;
            if (value.Type.IsDoubleTy()) return owner.Primitive(TypeKind.Number);
            if (value.Type.IsIntegerTy(1)) return owner.Primitive(TypeKind.Bool);
            if (value.Type is LlvmStructType { Elements.Count: 0 }) return owner.Primitive(TypeKind.Null);
            return -1;
        }

        private int StorageFromRoot(LlvmValue root, LlvmValue? payload)
        {
            int logical = root.LogicalType >= 0 ? root.LogicalType
                : values.TryGetValue(root, out int slot) ? slots[slot].Type : -1;
            if (logical >= 0 && owner.Types[logical].Pointee >= 0) return owner.Types[logical].Pointee;
            var origin = PointerRoot(root, owner.Input.Module.DataLayout.GetIndexTypeSizeInBits(root.Type));
            if (origin.Structure is not null) return owner.Layouts[origin.Layout].Fields[0].Type;
            return owner.Type(payload!);
        }

        private int LoadResultType(LlvmLoadInstruction load, int storage)
        {
            if (load.LogicalType >= 0 || values.ContainsKey(load)) return SelectedValueType(load);
            if (load.Type.IsPointerTy() || load.Type.IsStructTy())
            {
                if (LlvmGepOperator.TryGet(load.PointerOperand, out _))
                {
                    var position = PointerPositionOf(load.PointerOperand);
                    if (position.Structure is not null && ReferenceEquals(PointerField(load.PointerOperand, position, storage, load.Type).LlvmType, load.Type))
                        return SetSelectedValueType(load, storage);
                }
                else if (load.PointerOperand.LogicalType >= 0 && owner.Types[load.PointerOperand.LogicalType].Pointee == storage ||
                    values.TryGetValue(load.PointerOperand, out int pointerSlot) && owner.Types[slots[pointerSlot].Type].Pointee == storage)
                    return SetSelectedValueType(load, storage);
            }
            return owner.Type(load);
        }

        private bool RelocatedConstantLoad(LlvmLoadInstruction load, out NativeConstant constant)
        {
            var pointer = load.PointerOperand;
            if (LlvmGepOperator.TryGet(pointer, out var address))
            {
                if (address.SourceElementType is LlvmStructType && !pointerRelocations.ContainsKey(pointer))
                { constant = null!; return false; }
                var position = PointerPositionOf(pointer);
                if (!position.Offset.IsZero) { constant = null!; return false; }
                pointer = position.Root;
            }
            constant = null!;
            if (pointer is not LlvmGlobalVariable global || !ReferenceEquals(load.Type, global.ValueType) ||
                !owner.Input.Constants.TryGetValue(global, out constant!)) return false;
            if (load.LogicalType < 0 && global.LogicalType >= 0) SetSelectedValueType(load, owner.Types[global.LogicalType].Pointee);
            return true;
        }

        private LlvmValue ZeroOffsetPointerRoot(LlvmValue pointer)
        {
            if (!LlvmGepOperator.TryGet(pointer, out var address) || address.SourceElementType is LlvmStructType) return pointer;
            var position = PointerPositionOf(pointer);
            return position.Offset.IsZero ? position.Root : pointer;
        }
    }
}
