namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed class StorageShape(int element, int row, int header, int type, int valueRow, TypeKind carrier, TypeRef description)
        {
            internal int Element { get; set; } = element;
            internal int Row { get; } = row;
            internal int Header { get; } = header;
            internal int Type { get; } = type;
            internal int ValueRow { get; } = valueRow;
            internal TypeKind Carrier { get; } = carrier;
            internal TypeRef Description { get; } = description;
        }
        private readonly Dictionary<int, StorageShape> storageTypes = [];
        private readonly Dictionary<TypeKind, StorageShape> storageShapes = [];

        private void PrepareStorage()
        {
            int originalCount = Module.Types.Count;
            foreach (TypeKind kind in Module.Types.Where(type => (type.Kinds & (ushort)TypeKind.Storage) != 0)
                .Select(type => type.Element < 0 ? TypeKind.Any : Types[type.Element].Kind).Distinct())
            {
                int element = Type(TypeKind.Never);
                foreach (LegacyV6CodeType type in Module.Types.Where(type => (type.Kinds & (ushort)TypeKind.Storage) != 0))
                {
                    int candidate = type.Element < 0 ? Type(TypeKind.Any) : type.Element;
                    if (Types[candidate].Kind == kind) element = Join(element, candidate);
                }
                CreateStorage(element);
            }
            for (int type = 0; type < originalCount; type++)
            {
                LegacyV6CodeType legacy = Module.Types[type];
                if ((legacy.Kinds & (ushort)TypeKind.Storage) == 0) continue;
                TypeKind kind = legacy.Element < 0 ? TypeKind.Any : Types[legacy.Element].Kind;
                StorageShape shape = storageShapes[kind]; storageTypes[type] = shape;
                int[] headers = storageShapes.Values.Where(candidate => (kind & Types[candidate.Element].Kind) == Types[candidate.Element].Kind)
                    .Select(candidate => candidate.Header).Append(shape.Header).ToArray();
                Types[type] = new((TypeKind)(legacy.Kinds & ~(ushort)TypeKind.Storage) | TypeKind.Scope,
                    (legacy.Kinds & (ushort)TypeKind.Range) != 0 ? legacy.Element : -1,
                    (legacy.Kinds & (ushort)TypeKind.Scope) != 0 && legacy.Layouts.Count == 0 ? [] : legacy.Layouts.Concat(headers).Distinct().Order().ToArray());
            }
        }

        private StorageShape CreateStorage(int element, TypeRef? description = null)
        {
            description ??= NativeSourceType(element);
            TypeKind kind = description.Kind;
            if (storageShapes.TryGetValue(kind, out StorageShape? known)) return known;
            int number = Type(TypeKind.Number), boolean = Type(TypeKind.Bool);
            int row = Layouts.Count;
            Layouts.Add(new([new("<format6:value>", element), new("<format6:next>", number), new("<format6:left>", number), new("<format6:right>", number),
                new("<format6:parent>", number), new("<format6:red>", boolean), new("<format6:live>", boolean)]));
            int header = Layouts.Count;
            Layouts.Add(new([new("<format6:count>", number), new("<format6:end>", number), new("<format6:capacity>", number), new("<format6:free>", number),
                new("<format6:root>", number), new("<format6:data>", Scope(row)), new("<format6:element>", number), new("<format6:family>", number), new("<format6:element-type>", Type(TypeKind.Type))]));
            int valueRow = Layouts.Count; Layouts.Add(new([new("<format6:value>", element)]));
            StorageShape shape = new(element, row, header, Scope(header), valueRow, kind, description);
            storageShapes.Add(kind, shape); return shape;
        }

        private TypeRef NativeSourceType(int type)
        {
            NativeCodeType contract = Types[type];
            return new(contract.Kind, contract.Element < 0 ? null : NativeSourceType(contract.Element));
        }

        private sealed partial class FunctionImport
        {
            private readonly Dictionary<int, int> storageDemands = [];
            private void Demand(Item value, int type)
            {
                if (emitting || value.Producer < 0 || !owner.storageTypes.ContainsKey(type)) return;
                if (storageDemands.TryGetValue(value.Producer, out int previous) && (previous == type || owner.storageTypes[previous] == owner.storageTypes[type])) return;
                storageDemands[value.Producer] = type; owner.FactsChanged = true;
            }
            private StorageShape StorageOf(int type)
            {
                if (owner.storageTypes.TryGetValue(type, out StorageShape? shape)) return shape;
                foreach (StorageShape candidate in owner.storageShapes.Values)
                    if ((owner.Types[type].Layouts ?? []).Contains(candidate.Header)) return candidate;
                return owner.CreateStorage(owner.Type(TypeKind.Any));
            }
            private StorageShape NewStorageShape(int pc, Item argument, bool range)
            {
                int element = range ? owner.Types[argument.Type].Element : argument.KnownType;
                if (element >= 0 && owner.Types[element].Kind != TypeKind.Any
                    && owner.storageShapes.TryGetValue(owner.Types[element].Kind, out StorageShape? concrete)) return concrete;
                if (storageDemands.TryGetValue(pc, out int type)) return owner.storageTypes[type];
                if (element < 0) element = owner.Type(TypeKind.Any);
                if (owner.storageShapes.TryGetValue(owner.Types[element].Kind, out StorageShape? shape)) return shape;
                return owner.CreateStorage(element);
            }
            private Item RangeElementDescription(Item range)
            {
                int type = owner.Type(TypeKind.Type);
                Item description = Result(type, new(NativeOp.Unary, A: range.Register, Immediate: (long)NativeUnary.ValueType));
                Item element = Result(owner.Join(type, NullType), new(NativeOp.CallNative, Symbol: "meta.element_type", Inputs: [description.Register]));
                return As(element, type);
            }
            private Item Number(double value) => new(NumberType, Register: Constant(new(NativeKind.Number, Number: value), NumberType));
            private Item Boolean(bool value) => new(BoolType, Register: Constant(new(NativeKind.Bool, Boolean: value), BoolType));
            private Item MemoryRead(Item allocation, int layout, int field)
            {
                Item address = FieldAddress(allocation, layout, field);
                return Result(owner.Layouts[layout].Fields[field].Type, new(NativeOp.Load, A: address.Register));
            }
            private Item Arithmetic(NativeBinary operation, Item left, Item right) =>
                Result(NumberType, new(NativeOp.Binary, A: left.Register, B: right.Register, Immediate: (long)operation));
            private Item Compare(NativeBinary operation, Item left, Item right) =>
                Result(BoolType, new(NativeOp.Binary, A: left.Register, B: right.Register, Immediate: (long)operation));
            private int Label => code.Count;
            private int Jump() { int at = code.Count; Add(new(NativeOp.Jump)); return at; }
            private int Branch(Item condition) { condition = As(condition, BoolType); int at = code.Count; Add(new(NativeOp.Branch, A: condition.Register)); return at; }
            private void JumpTo(int at, int target) { if (emitting) code[at] = code[at] with { A = target }; }
            private void BranchTo(int at, int yes, int no) { if (emitting) code[at] = code[at] with { B = yes, C = no }; }
            private void Check(Item condition, string message)
            {
                int branch = Branch(condition), failure = Label;
                Add(new(NativeOp.Unreachable, Symbol: message));
                BranchTo(branch, Label, failure);
            }
            private Item DataRow(StorageShape shape, Item data, Item offset) =>
                Result(owner.Scope(shape.Row), new(NativeOp.IndexAddress, A: data.Register, B: offset.Register, C: shape.Row));
            private Item StorageRow(StorageShape shape, Item storage, Item offset) =>
                DataRow(shape, MemoryRead(storage, shape.Header, 5), offset);
            private void ChangeCount(StorageShape shape, Item storage, double delta)
            {
                Item count = MemoryRead(storage, shape.Header, 0);
                StoreField(storage, shape.Header, 0, Arithmetic(NativeBinary.Add, count, Number(delta)));
            }
            private void EnsureCapacity(StorageShape shape, Item storage, Item required)
            {
                Item capacity = MemoryRead(storage, shape.Header, 2);
                int branch = Branch(Compare(NativeBinary.LessEqual, required, capacity));
                int grow = Label;
                Item doubled = Arithmetic(NativeBinary.Multiply, capacity, Number(2));
                Item nextCapacity = Result(NumberType, new(NativeOp.CallNative, Symbol: "math.fmax", Inputs: [doubled.Register, required.Register]));
                Item data = Result(owner.Scope(shape.Row), new(NativeOp.Allocate, A: shape.Row, C: nextCapacity.Register));
                if (emitting) owner.Allocations.Add(new(code.Count - 1, shape.Row, Function: index));
                Item previous = MemoryRead(storage, shape.Header, 5);
                Item end = MemoryRead(storage, shape.Header, 1);
                Add(new(NativeOp.CopyMemory, A: data.Register, B: previous.Register, C: end.Register, Immediate: shape.Row));
                StoreField(storage, shape.Header, 5, data); StoreField(storage, shape.Header, 2, nextCapacity);
                BranchTo(branch, Label, grow);
            }
            private Item CreateOwner(StorageShape shape, bool arena, bool slots = false, Item? element = null)
            {
                Item storage = Result(shape.Type, new(NativeOp.Allocate, A: shape.Header));
                if (emitting) owner.Allocations.Add(new(code.Count - 1, shape.Header, Function: index));
                Item capacity = Number(4);
                Item data = Result(owner.Scope(shape.Row), new(NativeOp.Allocate, A: shape.Row, C: capacity.Register));
                if (emitting) owner.Allocations.Add(new(code.Count - 1, shape.Row, Function: index));
                StoreField(storage, shape.Header, 0, Number(0)); StoreField(storage, shape.Header, 1, Number(arena ? 1 : 0));
                StoreField(storage, shape.Header, 2, capacity); StoreField(storage, shape.Header, 3, Number(arena ? 0 : -1));
                StoreField(storage, shape.Header, 4, Number(0)); StoreField(storage, shape.Header, 5, data);
                StoreField(storage, shape.Header, 6, Number((ushort)shape.Carrier));
                StoreField(storage, shape.Header, 7, Number(arena ? 2 : slots ? 1 : 0));
                int type = owner.Type(TypeKind.Type);
                element ??= new(type, Register: Constant(new(NativeKind.Type, Type: SourceType(shape.Element)), type));
                StoreField(storage, shape.Header, 8, element);
                if (arena) InitializeRow(shape, data, ArenaDefault(shape), Number(0), false);
                return storage;
            }
            private void InitializeRow(StorageShape shape, Item row, Item? value, Item parent, bool live = true)
            {
                if (value is not null) StoreField(row, shape.Row, 0, StoreElement(shape, value));
                for (int field = 1; field <= 3; field++) StoreField(row, shape.Row, field, Number(0));
                StoreField(row, shape.Row, 4, parent); StoreField(row, shape.Row, 5, Boolean(live));
                StoreField(row, shape.Row, 6, Boolean(live));
            }
            private void ObserveStorageWrite(StorageShape shape, Item value)
            {
                if (emitting || shape.Carrier == TypeKind.Number || owner.Types[value.Type].Pointee >= 0) return;
                int incoming = value.Type;
                TypeKind kind = owner.Types[incoming].Kind;
                if ((shape.Carrier & ~TypeKind.Null) == TypeKind.String && (kind & TypeKind.Path) != 0)
                    incoming = owner.Type((kind & ~TypeKind.Path) | TypeKind.String);
                if ((shape.Carrier & ~TypeKind.Null) == TypeKind.Path && (kind & TypeKind.String) != 0)
                    incoming = owner.Type((kind & ~TypeKind.String) | TypeKind.Path);
                int element = owner.Join(shape.Element, incoming);
                if (element == shape.Element) return;
                shape.Element = element;
                NativeCodeField[] fields = owner.Layouts[shape.Row].Fields.ToArray(); fields[0] = fields[0] with { Type = element };
                owner.Layouts[shape.Row] = new(fields);
                owner.Layouts[shape.ValueRow] = new([owner.Layouts[shape.ValueRow].Fields[0] with { Type = element }]);
                owner.FactsChanged = true;
            }
            private Item StoreElement(StorageShape shape, Item value)
            {
                TypeKind expected = shape.Carrier & ~TypeKind.Null;
                TypeKind actual = owner.Types[value.Type].Kind;
                if (expected == TypeKind.Path && (actual & TypeKind.String) != 0 || expected == TypeKind.String && (actual & TypeKind.Path) != 0)
                    return Result(shape.Element, new(NativeOp.Convert, A: value.Register));
                return As(value, shape.Element);
            }
            private Item CheckedOffset(StorageShape shape, Item storage, Item offset, string operation, bool allowEnd = false, bool slots = false, bool arena = false)
            {
                offset = As(offset, NumberType);
                Item limit = MemoryRead(storage, shape.Header, slots || arena ? 1 : 0);
                Check(Compare(NativeBinary.GreaterEqual, offset, Number(0)), $"{operation}: index must be nonnegative");
                Check(Compare(allowEnd ? NativeBinary.LessEqual : NativeBinary.Less, offset, limit), $"{operation}: index is outside the initialized extent");
                Item floor = Result(NumberType, new(NativeOp.CallNative, Symbol: "math.floor", Inputs: [offset.Register]));
                Check(Compare(NativeBinary.Equal, offset, floor), $"{operation}: index must be an exact integer");
                if (allowEnd) return storage;
                Item row = StorageRow(shape, storage, offset);
                if (slots) Check(MemoryRead(row, shape.Row, 6), $"{operation}: index does not identify a live allocation");
                return row;
            }

            private Item StorageCall(int pc, string name, Item[] arguments)
            {
                _ = LegacyV6NativeSignatures.Get(name);
                bool arena = name.StartsWith("arena.", StringComparison.Ordinal);
                bool slots = name.StartsWith("slots.", StringComparison.Ordinal);
                string operation = name[(name.IndexOf('.') + 1)..];
                if (operation.StartsWith("vector_", StringComparison.Ordinal)) operation = operation[7..];
                if (operation is "new" or "empty")
                {
                    bool vectorSource = name == "storage.vector_new";
                    StorageShape shape = NewStorageShape(pc, arguments[0], vectorSource);
                    Item description = vectorSource ? RangeElementDescription(arguments[0]) : arguments[0];
                    Item storage = CreateOwner(shape, arena, slots, description) with { Producer = pc };
                    if (vectorSource) FillVector(shape, storage, arguments[0]);
                    return storage;
                }
                StorageShape[] alternatives = HeaderAlternatives(arguments[0]);
                if (alternatives.Length == 0) alternatives = [StorageOf(arguments[0].Type)];
                int resultType = alternatives.Select(shape => StorageResult(shape, operation, arena, slots)).Aggregate(owner.Type(TypeKind.Never), owner.Join);
                Item Apply(StorageShape shape)
                {
                    Item storage = As(arguments[0], shape.Type);
                    if (!arena && operation is "push" or "set" or "insert" or "allocate" or "reset")
                    {
                        int value = operation is "set" or "insert" ? 2 : 1;
                        ObserveStorageWrite(shape, arguments[value]);
                    }
                    Check(Compare(NativeBinary.Equal, MemoryRead(storage, shape.Header, 7), Number(arena ? 2 : slots ? 1 : 0)), name + ": incompatible owner family");
                    if (operation == "empty_like") return CreateOwner(shape, false, element: MemoryRead(storage, shape.Header, 8)) with { Producer = pc };
                    if (arena) return ArenaCall(shape, storage, operation, arguments);
                    if (slots) return SlotsCall(shape, storage, operation, arguments);
                    return VectorCall(shape, storage, operation, arguments);
                }
                if (alternatives.Length == 1 && owner.Types[arguments[0].Type].Kind == TypeKind.Scope
                    && owner.Types[arguments[0].Type].Layouts is { Count: 1 }) return Apply(alternatives[0]);
                Item Invalid()
                {
                    Add(new(NativeOp.Unreachable, Symbol: name + ": requires a compatible owner"));
                    return new(resultType, Register: Register(resultType));
                }
                return LayoutSwitch(arguments[0], alternatives, resultType, Apply, Invalid);
            }

            private int StorageResult(StorageShape shape, string operation, bool arena, bool slots)
            {
                if (operation == "empty_like") return shape.Type;
                if (operation is "snapshot" or "take") return owner.Type(TypeKind.Range, shape.Element);
                if (operation == "get" || !arena && !slots && operation is "pop" or "remove") return shape.Element;
                if (arena && operation == "key") return owner.Join(shape.Element, NullType);
                if (arena && operation == "red") return BoolType;
                if (operation is "size" or "count" or "push" or "root" or "child" or "parent" or "allocate" or "reset") return NumberType;
                return NullType;
            }

            private void FillVector(StorageShape shape, Item storage, Item range)
            {
                int iterator = iteratorIds.Count;
                if (emitting) iteratorIds.Add(-code.Count - 1, iterator);
                Add(new(NativeOp.OpenIterator, A: range.Register, B: iterator));
                int loop = Label;
                Item value = Result(shape.Element, new(NativeOp.NextIterator, A: iterator)); int next = code.Count - 1;
                VectorPush(shape, storage, value); int back = Jump(); JumpTo(back, loop);
                if (emitting) code[next] = code[next] with { B = Label };
                Add(new(NativeOp.CloseIterator, A: iterator));
            }
            private Item VectorPush(StorageShape shape, Item storage, Item value)
            {
                Item count = MemoryRead(storage, shape.Header, 0);
                Item next = Arithmetic(NativeBinary.Add, count, Number(1)); EnsureCapacity(shape, storage, next);
                Item row = StorageRow(shape, storage, count); InitializeRow(shape, row, value, Number(0));
                StoreField(storage, shape.Header, 0, next); StoreField(storage, shape.Header, 1, next); return count;
            }
            private Item VectorCall(StorageShape shape, Item storage, string operation, Item[] arguments)
            {
                if (operation == "size") return MemoryRead(storage, shape.Header, 0);
                if (operation == "push") return VectorPush(shape, storage, arguments[1]);
                if (operation is "snapshot" or "take") return Snapshot(shape, storage, operation == "take");
                if (operation == "clear") { ClearOwner(shape, storage, false); return NullItem(); }
                if (operation == "insert")
                {
                    Item offset = arguments[1]; CheckedOffset(shape, storage, offset, "storage.vector_insert", allowEnd: true);
                    Item count = MemoryRead(storage, shape.Header, 0), next = Arithmetic(NativeBinary.Add, count, Number(1));
                    EnsureCapacity(shape, storage, next);
                    Item row = StorageRow(shape, storage, offset);
                    Item length = Arithmetic(NativeBinary.Subtract, count, offset);
                    int shift = Branch(Compare(NativeBinary.Greater, length, Number(0))), shifting = Label;
                    Item after = StorageRow(shape, storage, Arithmetic(NativeBinary.Add, offset, Number(1)));
                    Add(new(NativeOp.CopyMemory, A: after.Register, B: row.Register, C: length.Register, Immediate: shape.Row));
                    BranchTo(shift, shifting, Label);
                    InitializeRow(shape, row, arguments[2], Number(0));
                    StoreField(storage, shape.Header, 0, next); StoreField(storage, shape.Header, 1, next); return NullItem();
                }
                Item index = operation == "pop" ? Arithmetic(NativeBinary.Subtract, MemoryRead(storage, shape.Header, 0), Number(1)) : arguments[1];
                Item target = CheckedOffset(shape, storage, index, "storage.vector_" + operation);
                if (operation == "set") { StoreField(target, shape.Row, 0, StoreElement(shape, arguments[2])); return NullItem(); }
                Item value = MemoryRead(target, shape.Row, 0);
                if (operation == "get") return value;
                Item oldCount = MemoryRead(storage, shape.Header, 0), newCount = Arithmetic(NativeBinary.Subtract, oldCount, Number(1));
                Item remaining = Arithmetic(NativeBinary.Subtract, newCount, index);
                int shiftRemaining = Branch(Compare(NativeBinary.Greater, remaining, Number(0))), move = Label;
                Item source = StorageRow(shape, storage, Arithmetic(NativeBinary.Add, index, Number(1)));
                Add(new(NativeOp.CopyMemory, A: target.Register, B: source.Register, C: remaining.Register, Immediate: shape.Row));
                BranchTo(shiftRemaining, move, Label);
                Item last = StorageRow(shape, storage, newCount);
                Add(new(NativeOp.ClearMemory, A: last.Register, C: Number(1).Register, Immediate: shape.Row));
                StoreField(storage, shape.Header, 0, newCount); StoreField(storage, shape.Header, 1, newCount); return value;
            }
            private Item NullItem() => new(NullType, Register: Constant(new(NativeKind.Null), NullType));
            private void ClearOwner(StorageShape shape, Item storage, bool arena)
            {
                Item data = MemoryRead(storage, shape.Header, 5), end = MemoryRead(storage, shape.Header, 1);
                Add(new(NativeOp.ClearMemory, A: data.Register, C: end.Register, Immediate: shape.Row));
                StoreField(storage, shape.Header, 0, Number(0)); StoreField(storage, shape.Header, 1, Number(arena ? 1 : 0));
                StoreField(storage, shape.Header, 3, Number(arena ? 0 : -1)); StoreField(storage, shape.Header, 4, Number(0));
                if (arena) InitializeRow(shape, data, ArenaDefault(shape), Number(0), false);
            }
            private Item Snapshot(StorageShape shape, Item storage, bool take)
            {
                Item count = MemoryRead(storage, shape.Header, 0), data = MemoryRead(storage, shape.Header, 5);
                Item result = Result(owner.Type(TypeKind.Range, shape.Element), new(NativeOp.RangeFromMemory,
                    A: data.Register, B: count.Register, C: shape.Row, Immediate: 0, Symbol: take ? "move" : null,
                    Inputs: [MemoryRead(storage, shape.Header, 8).Register]));
                if (!take) return result;
                Item capacity = Number(4);
                Item replacement = Result(owner.Scope(shape.Row), new(NativeOp.Allocate, A: shape.Row, C: capacity.Register));
                if (emitting) owner.Allocations.Add(new(code.Count - 1, shape.Row, Function: index));
                StoreField(storage, shape.Header, 5, replacement);
                StoreField(storage, shape.Header, 2, capacity);
                StoreField(storage, shape.Header, 0, Number(0)); StoreField(storage, shape.Header, 1, Number(0));
                return result;
            }

            private Item AllocateSlot(StorageShape shape, Item storage, Item value, Item parent, bool arena)
            {
                Item free = MemoryRead(storage, shape.Header, 3);
                int selected = Register(NumberType); int branch = Branch(Compare(arena ? NativeBinary.NotEqual : NativeBinary.GreaterEqual, free, Number(0)));
                int reuse = Label; Add(new(NativeOp.Copy, selected, free.Register));
                Item old = StorageRow(shape, storage, free); StoreField(storage, shape.Header, 3, MemoryRead(old, shape.Row, arena ? 2 : 1));
                int done = Jump(), extend = Label;
                Item end = MemoryRead(storage, shape.Header, 1); Add(new(NativeOp.Copy, selected, end.Register));
                Item next = Arithmetic(NativeBinary.Add, end, Number(1)); EnsureCapacity(shape, storage, next); StoreField(storage, shape.Header, 1, next);
                BranchTo(branch, reuse, extend); JumpTo(done, Label);
                Item index = new(NumberType, Register: selected); Item row = StorageRow(shape, storage, index);
                InitializeRow(shape, row, value, parent); ChangeCount(shape, storage, 1); return index;
            }
            private Item SlotsCall(StorageShape shape, Item storage, string operation, Item[] arguments)
            {
                if (operation == "count") return MemoryRead(storage, shape.Header, 0);
                if (operation == "reset") ClearOwner(shape, storage, false);
                if (operation is "allocate" or "reset") return AllocateSlot(shape, storage, arguments[1], Number(0), false);
                Item row = CheckedOffset(shape, storage, arguments[1], "slots." + operation, slots: true);
                if (operation == "get") return MemoryRead(row, shape.Row, 0);
                Item free = MemoryRead(storage, shape.Header, 3);
                Add(new(NativeOp.ClearMemory, A: row.Register, C: Number(1).Register, Immediate: shape.Row));
                StoreField(row, shape.Row, 1, free); StoreField(row, shape.Row, 6, Boolean(false));
                StoreField(storage, shape.Header, 3, arguments[1]); ChangeCount(shape, storage, -1); return NullItem();
            }
            private Item? ArenaDefault(StorageShape shape)
            {
                TypeKind kind = shape.Carrier;
                if (kind == TypeKind.Number) return Number(0);
                if (kind == TypeKind.Bool) return Boolean(false);
                if (kind is (TypeKind.Number | TypeKind.Null) or (TypeKind.Bool | TypeKind.Null)) return NullItem();
                return null;
            }
            private Item ArenaKey(StorageShape shape, Item row)
            {
                if (ArenaDefault(shape) is not null) return MemoryRead(row, shape.Row, 0);
                int type = owner.Join(shape.Element, NullType), value = Register(type);
                int branch = Branch(MemoryRead(row, shape.Row, 6)), live = Label;
                Item key = MemoryRead(row, shape.Row, 0); Add(new(NativeOp.Copy, value, key.Register)); int done = Jump(), dead = Label;
                Add(new(NativeOp.Copy, value, NullItem().Register)); BranchTo(branch, live, dead); JumpTo(done, Label);
                return new(type, Register: value);
            }
            private Item ArenaCall(StorageShape shape, Item storage, string operation, Item[] arguments)
            {
                if (operation == "size") return MemoryRead(storage, shape.Header, 0);
                if (operation == "root") return MemoryRead(storage, shape.Header, 4);
                if (operation == "clear") { ClearOwner(shape, storage, true); return NullItem(); }
                if (operation == "allocate")
                {
                    CheckedOffset(shape, storage, arguments[2], "arena.allocate", arena: true);
                    Item value = StoreElement(shape, arguments[1]);
                    if (shape.Carrier != TypeKind.Any && shape.Carrier != TypeKind.Never)
                    {
                        Item actual = ValueDescription(value); int type = owner.Type(TypeKind.Type);
                        Item expected = new(type, Register: Constant(new(NativeKind.Type, Type: new TypeRef(shape.Carrier)), type));
                        Check(Result(BoolType, new(NativeOp.Unary, A: actual.Register, B: expected.Register,
                            Immediate: (long)NativeUnary.IsType)), "arena.allocate: key violates its declared builtin carrier");
                    }
                    return AllocateSlot(shape, storage, value, arguments[2], true);
                }
                Item row = CheckedOffset(shape, storage, arguments[1], "arena." + operation, arena: true);
                if (operation == "set_root") { StoreField(storage, shape.Header, 4, arguments[1]); return NullItem(); }
                if (operation == "key") return ArenaKey(shape, row);
                if (operation == "parent") return MemoryRead(row, shape.Row, 4);
                if (operation == "red") return MemoryRead(row, shape.Row, 5);
                if (operation == "set_parent")
                { CheckedOffset(shape, storage, arguments[2], "arena.set_parent", arena: true); StoreField(row, shape.Row, 4, arguments[2]); return NullItem(); }
                if (operation == "set_red") { StoreField(row, shape.Row, 5, arguments[2]); return NullItem(); }
                if (operation == "release")
                {
                    Check(Compare(NativeBinary.NotEqual, arguments[1], Number(0)), "arena.release: cannot release the NIL row");
                    Item free = MemoryRead(storage, shape.Header, 3);
                    if (ArenaDefault(shape) is null)
                    {
                        Item key = FieldAddress(row, shape.Row, 0);
                        Add(new(NativeOp.ClearMemory, A: key.Register, C: Number(1).Register, Immediate: shape.ValueRow));
                    }
                    StoreField(row, shape.Row, 6, Boolean(false)); StoreField(row, shape.Row, 2, free);
                    StoreField(storage, shape.Header, 3, arguments[1]); ChangeCount(shape, storage, -1); return NullItem();
                }
                int field = Register(NumberType); int branch = Branch(arguments[2]), left = Label;
                Item a = MemoryRead(row, shape.Row, 2); Add(new(NativeOp.Copy, field, a.Register)); int done = Jump(), right = Label;
                Item b = MemoryRead(row, shape.Row, 3); Add(new(NativeOp.Copy, field, b.Register)); BranchTo(branch, left, right); JumpTo(done, Label);
                if (operation == "child") return new(NumberType, Register: field);
                CheckedOffset(shape, storage, arguments[3], "arena.set_child", arena: true);
                branch = Branch(arguments[2]); left = Label; StoreField(row, shape.Row, 2, arguments[3]); done = Jump(); right = Label;
                StoreField(row, shape.Row, 3, arguments[3]); BranchTo(branch, left, right); JumpTo(done, Label); return NullItem();
            }
        }
    }
}
