namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private static bool IsStorage(string name) => name.StartsWith("storage.", StringComparison.Ordinal)
        || name.StartsWith("arena.", StringComparison.Ordinal) || name.StartsWith("slots.", StringComparison.Ordinal);

    private sealed partial class Importer
    {
        private int StorageAdapter(string name)
        {
            if (adapterIds.TryGetValue(name, out int existing)) return existing;
            int identity = Module.Functions.Count + adapters.Count;
            int ordinal = adapters.Count; adapters.Add(null); adapterIds.Add(name, identity);
            LegacyV6NativeSignature native = LegacyV6NativeSignatures.Get(name);
            CreateStorage(Type(TypeKind.Any));
            int storage = Type(TypeKind.Scope, layouts: storageShapes.Values.Select(shape => shape.Header).Order().ToArray());
            int[] parameters = native.Parameters.Select(parameter => (parameter.Kind & TypeKind.Storage) != 0 ? storage : Type(parameter)).ToArray();
            int result = (native.Result.Kind & TypeKind.Storage) != 0 ? storage : Type(native.Result);
            LegacyV6Function function = new($"<format6:{name}>", parameters.Length,
                new(parameters, result, [], [], false), []);
            adapters[ordinal] = new FunctionImport(this, identity, function).StorageAdapter(name);
            return identity;
        }

        private sealed partial class FunctionImport
        {
            internal NativeFunctionCode StorageAdapter(string name)
            {
                emitting = true;
                foreach (int parameter in function.Signature.Parameters) slots.Add(new(parameter, NativeSlotRole.Parameter));
                Item[] arguments = function.Signature.Parameters.Select((type, parameter) => new Item(type, Register: parameter)).ToArray();
                bool arena = name.StartsWith("arena.", StringComparison.Ordinal), slotsOwner = name.StartsWith("slots.", StringComparison.Ordinal);
                string operation = name[(name.IndexOf('.') + 1)..];
                if (operation.StartsWith("vector_", StringComparison.Ordinal)) operation = operation[7..];
                bool create = operation is "new" or "empty";
                StorageShape[] shapes = owner.storageShapes.Values.ToArray();
                foreach (StorageShape shape in shapes.OrderBy(shape => shape.Carrier == TypeKind.Any))
                {
                    Item test;
                    if (create)
                    {
                        int type = owner.Type(TypeKind.Type);
                        TypeRef expectedType = shape.Description;
                        bool vectorSource = name == "storage.vector_new";
                        if (vectorSource) expectedType = new(TypeKind.Range, expectedType);
                        Item expected = new(type, Register: Constant(new(NativeKind.Type, Type: expectedType), type));
                        test = Result(BoolType, new(NativeOp.Unary, A: arguments[0].Register,
                            B: expected.Register, Immediate: (long)NativeUnary.IsType));
                    }
                    else
                    {
                        Item tag = HeaderRead(arguments[0], 6);
                        test = Compare(NativeBinary.Equal, tag, Number((ushort)shape.Carrier));
                    }
                    int branch = Branch(test), body = Label;
                    Item result;
                    if (create)
                    {
                        result = CreateOwner(shape, arena, slotsOwner, name == "storage.vector_new" ? RangeElementDescription(arguments[0]) : arguments[0]);
                        if (name == "storage.vector_new") FillVector(shape, result, arguments[0]);
                    }
                    else
                    {
                        Item storage = As(arguments[0], shape.Type);
                        Check(Compare(NativeBinary.Equal, MemoryRead(storage, shape.Header, 7), Number(arena ? 2 : slotsOwner ? 1 : 0)),
                            name + ": incompatible owner family");
                        if (operation == "empty_like") result = CreateOwner(shape, false, element: MemoryRead(storage, shape.Header, 8));
                        else if (arena) result = ArenaCall(shape, storage, operation, arguments);
                        else if (slotsOwner) result = SlotsCall(shape, storage, operation, arguments);
                        else result = VectorCall(shape, storage, operation, arguments);
                    }
                    Add(new(NativeOp.Return, A: result.Register)); BranchTo(branch, body, Label);
                }
                Add(new(NativeOp.Unreachable, Symbol: name + ": unsupported element contract"));
                Compact();
                return new(function.Name, slots.ToArray(), code.ToArray(),
                    new(function.Signature.Parameters, function.Signature.Result, []), function.Signature.Parameters.Count, IteratorCount: iteratorIds.Count)
                {
                    ReflectionSignature = new(LegacyV6NativeSignatures.Get(name).Parameters, LegacyV6NativeSignatures.Get(name).Result)
                };
            }
            private readonly Dictionary<int, int> headerProjections = [];
            private Item HeaderRead(Item storage, int field)
            {
                if (!headerProjections.TryGetValue(field, out int projection))
                {
                    projection = owner.Projections.Count;
                    owner.Projections.Add(new(owner.storageShapes.Values.Select(shape => new NativeProjectionCase(shape.Header, field)).OrderBy(item => item.Layout).ToArray()));
                    headerProjections.Add(field, projection);
                }
                Item address = Result(owner.Pointer(NumberType), new(NativeOp.ProjectAddress, A: storage.Register, B: projection));
                return Result(NumberType, new(NativeOp.Load, A: address.Register));
            }
            private TypeRef SourceType(int type)
            {
                NativeCodeType contract = owner.Types[type];
                return new(contract.Kind, contract.Element < 0 ? null : SourceType(contract.Element));
            }
        }
    }
}
