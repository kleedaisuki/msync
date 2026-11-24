using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmUse, int?> textFieldStorage = [];
        internal bool HasTextFields => graph.TextCompletionFields is { Count: > 0 };
        internal bool SelectedTextField(int layout, int field) => graph.TextCompletionFields?.Contains(new(layout, field)) == true;

        internal bool TextFieldStorage(LlvmUse address) => TextFieldOffset(address) is not null;

        internal int? TextFieldOffset(LlvmUse address)
        {
            if (!HasTextFields) return null;
            if (textFieldStorage.TryGetValue(address, out int? known)) return known;
            return textFieldStorage[address] = KnownTextField(address);
        }

        private int? KnownTextField(LlvmUse address)
        {
            if (address.Value is LlvmCallInstruction { CalledFunction: { } target }
                && graph.ValueOperations.GetValueOrDefault(target)?.Projection is { } projection)
            {
                bool selected = projection.Cases.Any(lane => SelectedTextField(lane.Layout, lane.Field));
                if (selected && !projection.Cases.All(lane => SelectedTextField(lane.Layout, lane.Field)))
                    throw new MsyncException("Member access requires distinct ready Text and Text completion field layouts.",
                        (address.User as LlvmInstruction)?.Source);
                if (!selected) return null;
                int payload = TextFieldPayload(projection.Cases[0].Layout, projection.Cases[0].Field);
                if (!projection.Cases.All(lane => TextFieldPayload(lane.Layout, lane.Field) == payload))
                    throw new MsyncException("Member access requires one physical Text completion lane.", (address.User as LlvmInstruction)?.Source);
                return payload;
            }

            if (address.Value is LlvmGetElementPtrInstruction { HasAllConstantIndices: true } gep)
            {
                int offset = checked((int)Data.GetIndexedOffsetInType(gep.SourceElementType, gep.Indices.ToArray()));
                if (gep.GetTargetStorageType() is { } selected && graph.StructLayouts.TryGetValue(selected, out int layout))
                    return TextFieldAtOffset(layout, offset);
                if (gep.PointerOperand is LlvmHeapAllocInst owner) return TextFieldAtOffset(owner.LogicalLayout, offset);
            }
            if (address.TargetStorageType is { } structure && graph.StructLayouts.TryGetValue(structure, out int folded))
                return TextFieldAtOffset(folded, 0);
            return address.Value is LlvmHeapAllocInst allocated ? TextFieldAtOffset(allocated.LogicalLayout, 0) : null;
        }

        private int? TextFieldAtOffset(int layout, int offset)
        {
            if (layout < 0 || !structs.TryGetValue(layout, out var structure)) return null;
            foreach (var field in graph.TextCompletionFields!)
                if (field.Layout == layout && FieldOffset(structure, (uint)field.Ordinal) == offset)
                    return TextFieldPayload(layout, field.Ordinal);
            return null;
        }

        internal int TextFieldPayload(int layout, int field) => TextReference(structs[layout].Elements[field])!.Value;
        internal int? TextReference(LlvmType type)
        {
            foreach (var leaf in Leaves(type))
                if (IsGuestPointer(leaf.Type)) return leaf.Offset;
            return null;
        }

        internal int ReadyTextCompletionAllocator() => PhysicalAllocator(
            FutureLayout(-(int)HpirService.ReadLineText, HpirTextInputImports.Completion()));
    }

    private sealed partial class Body
    {
        private int textFieldWraps, textFieldStores;

        private bool TryStoreTextField(LlvmStoreInstruction store)
        {
            if (geometry.TextFieldOffset(store.GetOperandUse(1)) is not { } payload) return false;
            var value = store.ValueOperand;
            if (TextCell(value))
            {
                int input = Location(value);
                if (geometry.MixedTextResult(value) || geometry.StoredText(value))
                    SelectTextCarrier(store, value, pending => StoreTextField(store, input, payload, pending));
                else StoreTextField(store, input, payload, pending: true);
            }
            else
            {
                int input = geometry.TextReference(value.Type) is { } offset ? LeafLocation(value, offset) : FormatImmediate(store, 0);
                StoreTextField(store, input, payload, pending: false);
            }
            ++textFieldStores;
            return true;
        }

        private void StoreTextField(LlvmStoreInstruction store, int input, int payload, bool pending)
        {
            if (!pending)
            {
                int completion = Reserve(8, 8);
                EmitCall(store, ~geometry.ReadyTextCompletionAllocator(), completion, []);
                Copy(HpirWidth.U64, ~completion, input, store.Source);
                input = completion;
                ++textFieldWraps;
            }
            Copy(HpirWidth.U64, Memory(store.PointerOperand, payload, store), input, store.Source);
        }
    }
}
