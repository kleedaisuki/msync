using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Body
    {
        private int addressScratch = -1, foldedFutureFields, sessionStoreDemands;
        private readonly Dictionary<LlvmValue, int> records = [];

        private int RecordLocation(LlvmValue record)
        {
            if (records.TryGetValue(record, out int location)) return location;
            int size = checked((int)Layout.GetTypeAllocSize(record.Type).GetFixedValue());
            int alignment = checked((int)Layout.GetABITypeAlign(record.Type).Value);
            if (alignment > HpirAbi.FrameAlignment) throw Error("overaligned value carrier", record as LlvmInstruction);
            if (UnionCell(record))
            {
                size = checked(UnionTail(record.Type) + 8);
                alignment = Math.Max(8, alignment);
            }
            location = Reserve(Math.Max(size, 1), alignment);
            records.Add(record, location);
            return location;
        }

        private static bool Aggregate(LlvmType type) => type is LlvmStructType or LlvmArrayType;

        private void Load(LlvmLoadInstruction load)
        {
            if (geometry.TextFieldOffset(load.GetOperandUse(0)) is { } text)
            {
                Copy(HpirWidth.U64, Location(load), Memory(load.PointerOperand, text, load), load.Source);
                return;
            }
            if (load.PointerOperand is LlvmAllocaInstruction item && frameCompletions.TryGetValue(item, out int completion))
            {
                Copy(HpirWidth.U64, Location(load), completion, load.Source);
                Copy(Width(load.Type), Payload(load), storage[item], load.Source);
                return;
            }
            if (geometry.FutureStorageDelta(load.GetOperandUse(0)) is { } delta)
            {
                if (load.GetOperandUse(0).TargetStorageType is not null) ++foldedFutureFields;
                if (!UnionCell(load))
                {
                    LoadFutureField(load, delta);
                    return;
                }
                Copy(HpirWidth.U64, UnionCompletion(load), Memory(load.PointerOperand, delta, load), load.Source);
            }
            if (!Aggregate(load.Type))
            {
                Copy(Width(load.Type), Location(load), Memory(load.PointerOperand), load.Source);
                return;
            }
            foreach (var leaf in geometry.Leaves(load.Type))
            {
                var width = Width(leaf.Type);
                int temporary = checked(RecordLocation(load) + leaf.Offset);
                Copy(width, temporary, Memory(load.PointerOperand, leaf.Offset, load), load.Source);
            }
        }

        private void Store(LlvmStoreInstruction store)
        {
            if (TryStoreTextField(store) || TryStorePromptInput(store)) return;
            int? tail = geometry.FutureStorageDelta(store.GetOperandUse(1));
            if (tail is { } delta)
            {
                if (store.GetOperandUse(1).TargetStorageType is not null) ++foldedFutureFields;
                if (geometry.Union(store.ValueOperand.Type) == Msync.Core.TypeKind.Never)
                {
                    StoreFutureField(store, delta);
                    return;
                }
                Copy(HpirWidth.U64, Memory(store.PointerOperand, delta, store), UnionCompletion(store.ValueOperand), store.Source);
            }
            var value = store.ValueOperand;
            if (!Aggregate(value.Type))
            {
                int source = Location(value);
                if (FutureCell(value) && geometry.ReadySessionScalarStore(store))
                {
                    var width = Width(value.Type);
                    source = Reserve((int)width, (int)width);
                    Demand(store, value, source);
                    ++sessionStoreDemands;
                }
                else if (TextCell(value) && geometry.ReadySessionTextStore(store))
                {
                    source = ObserveText(store, value);
                    ++sessionStoreDemands;
                }
                Copy(Width(value.Type), Memory(store.PointerOperand), source, store.Source);
                return;
            }
            int scalar = tail is null && UnionCell(value) && geometry.ReadySessionUnionStore(store)
                ? ObserveSessionUnion(store, value) : -1;
            if (scalar >= 0) ++sessionStoreDemands;
            foreach (var leaf in geometry.Leaves(value.Type))
            {
                int from = scalar >= 0 && leaf.Offset == geometry.FieldOffset(value.Type, 1) ? scalar : LeafLocation(value, leaf.Offset);
                Copy(Width(leaf.Type), Memory(store.PointerOperand, leaf.Offset, store), from, store.Source);
            }
        }

        private void Extract(LlvmExtractValueInstruction extract)
        {
            if (Aggregate(extract.Type)) return;
            int offset = geometry.FieldOffset(extract.AggregateOperand.Type, extract.Indices);
            Alias(extract, LeafLocation(extract.AggregateOperand, offset));
        }

        private void Alias(LlvmInstruction target, int source)
        {
            if (operands.TryAdd(target, source)) return;
            Copy(Width(target.Type), Location(target), source, target.Source);
        }

        private int LeafLocation(LlvmValue value, int offset)
        {
            while (true)
            {
                if (!Aggregate(value.Type)) return Location(value);
                if (value is LlvmArgument or LlvmLoadInstruction or LlvmCallInstruction or LlvmSelectInstruction or LlvmPhiInstruction)
                    return checked(RecordLocation(value) + offset);
                switch (value)
                {
                    case LlvmConstant constant:
                        return Location(LiteralField(constant, offset));
                    case LlvmInsertValueInstruction insert:
                        int start = geometry.FieldOffset(value.Type, insert.Indices);
                        int bytes = checked((int)Layout.GetTypeAllocSize(insert.InsertedValueOperand.Type).GetFixedValue());
                        if (offset >= start && offset - start < bytes)
                        {
                            offset -= start;
                            value = insert.InsertedValueOperand;
                        }
                        else value = insert.AggregateOperand;
                        break;
                    case LlvmExtractValueInstruction extract:
                        value = extract.AggregateOperand;
                        offset += geometry.FieldOffset(value.Type, extract.Indices);
                        break;
                    default: throw Error($"lowering physical leaf {offset} of {value.GetType().Name}", value as LlvmInstruction);
                }
            }
        }

        private LlvmConstant LiteralField(LlvmConstant value, int offset)
        {
            while (Aggregate(value.Type))
            {
                ulong count = value.Type is LlvmStructType structure ? (ulong)structure.Elements.Count : ((LlvmArrayType)value.Type).NumElements;
                uint field = 0;
                while (field + 1UL < count && geometry.FieldOffset(value.Type, field + 1) <= offset) ++field;
                offset -= geometry.FieldOffset(value.Type, field);
                value = value.GetAggregateElement(field) ?? throw Error($"materializing aggregate literal {value.GetType().Name}");
            }
            if (value is LlvmUndefValue) return LlvmConstant.GetNullValue(value.Type);
            return value;
        }

        private int Memory(LlvmValue pointer, int delta, LlvmInstruction instruction)
        {
            if (frameAddresses.TryGetValue(pointer, out int direct)) return checked(direct + delta);
            return Indirect(Location(pointer), delta, instruction);
        }

        private int Indirect(int pointer, int delta, LlvmInstruction instruction)
        {
            ++indirectAccesses;
            if (delta == 0) return ~pointer;
            if (addressScratch < 0) addressScratch = Reserve(8, 8);
            Add(new(HpirOp.OffsetPointer, addressScratch, pointer, Immediate: delta), instruction.Source);
            return ~addressScratch;
        }
    }
}
